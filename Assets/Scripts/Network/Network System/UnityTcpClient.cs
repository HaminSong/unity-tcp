using System;
using System.Collections.Concurrent;
using System.Net.Sockets;
using System.Threading;
using UnityEngine;
using UnityTcp;


/// <summary>
/// Unity 클라이언트용 TCP 통신 컴포넌트.
/// 
/// 역할:
/// 1. 서버 연결 / 재연결
/// 2. 수신 데이터 스트림을 패킷 단위로 파싱
/// 3. Dispatcher를 통해 패킷 핸들러로 전달
/// 4. 메인 스레드에서 UI/이벤트 처리
/// </summary>
public class UnityTcpClient : MonoBehaviour
{
    [Header("Connect")]
    public string host = "127.0.0.1";
    public int port = 7777;

    [Header("Reconnect")]
    public bool autoReconnect = true;

    [Tooltip("재접속 시 이전에 선택했던 ID를 유지해서 자동으로 다시 요청할지 여부")]
    public bool keepDesiredUserIdOnReconnect = true;

    [Header("User ID Selection (0~9)")]
    [Tooltip("서버가 Offer 준 다음, 사용자가 선택할 ID(0~9). -1이면 아직 미선택.")]
    public int desiredUserId = -1;

    private TcpClient _tcp;
    private NetworkStream _stream;

    // 실제 네트워크 송수신은 별도 스레드에서 처리
    private Thread _netThread;
    private volatile bool _running;

    // TCP 스트림을 완전한 패킷 단위로 조립
    private readonly PacketStreamParser _parser = new PacketStreamParser();

    // Unity API 호출은 메인 스레드에서만 처리하기 위해 큐 사용
    private readonly ConcurrentQueue<Action> _mainThreadQueue = new ConcurrentQueue<Action>();

    // 여러 곳에서 Send가 호출될 수 있으므로 송신 동기화
    private readonly object _sendLock = new object();

    private PacketDispatcher _dispatcher;
    private ClientPacketContext _packetContext;
    private ClientSystemPacketHandler _systemHandler;

    public event Action OnPlayerAvailabilityChanged;
    public event Action<bool> OnConnectionStateChanged;

    //서버와 연결되어 있는지 여부
    public bool IsConnected { get; private set; }

    // 서버가 최종 승인한 내 사용자 ID
    public int AssignedUserId { get; set; } = -1;

    // 서버가 마지막으로 알려준 사용 가능 ID 마스크
    public ushort LastOfferMask { get; set; } = 0;

    // 서버로부터 IdOffer를 한 번이라도 받았는지 여부
    public bool ReceivedOffer { get; set; } = false;



    void Awake()
    {
        // 패킷 분기기와 시스템 패킷 핸들러 초기화
        _dispatcher = new PacketDispatcher();
        _packetContext = new ClientPacketContext(this);
        _systemHandler = new ClientSystemPacketHandler(_packetContext);
    }

    void Start()
    {
        // 외부에서 AddRoute 할 시간 확보 후 실제 등록
        _systemHandler.Register(_dispatcher);

        Application.runInBackground = true;
        StartClient();
    }

    void OnDestroy()
    {
        StopClient();
    }

    void Update()
    {
        // 네트워크 스레드에서 예약한 메인 스레드 작업 실행
        while (_mainThreadQueue.TryDequeue(out var job))
        {
            try { job?.Invoke(); }
            catch (Exception e) { Debug.LogException(e); }
        }
    }

    /// <summary>
    /// 외부 기능 스크립트가 클라이언트 수신 route를 추가하는 창구
    /// </summary>
    public void AddRoute(Msg msg, Sub sub, Action<byte[]> handler)
    {
        _systemHandler.AddRoute(msg, sub, handler);
    }

    /// <summary>
    /// 메인 쓰레드에서 처리하기 위함
    /// </summary>
    /// <param name="action"></param>
    public void EnqueueMain(Action action)
    {
        _mainThreadQueue.Enqueue(action);
    }

    public void RaisePlayerAvailabilityChanged()
    {
        OnPlayerAvailabilityChanged?.Invoke();
    }

    public void TrySendSelectIdReqNowInternal()
    {
        TrySendSelectIdReqNow();
    }


    /// <summary>
    /// 사용자가 원하는 ID를 선택.
    /// 연결 상태와 서버 Offer 상태가 맞으면 즉시 요청 전송.
    /// </summary>
    public void ChooseUserId(int id)
    {
        if (id < 0 || id > NetConst.MaxUserId) return;

        desiredUserId = id;
        _mainThreadQueue.Enqueue(() => Debug.Log($"[Client] User selected desiredUserId={desiredUserId}"));

        TrySendSelectIdReqNow();
    }

    /// <summary>
    /// SelectIdReq를 지금 보낼 수 있는 상태인지 확인 후 전송.
    /// </summary>
    private void TrySendSelectIdReqNow()
    {
        if (!IsConnected) return;
        if (AssignedUserId >= 0) return;
        if (!ReceivedOffer) return;
        if (desiredUserId < 0 || desiredUserId > NetConst.MaxUserId) return;

        if (!IdMaskUtil.IsAvailable(LastOfferMask, desiredUserId))
            return;

        SendSelectIdReq((byte)desiredUserId);
        ReceivedOffer = false;
    }

    /// <summary>
    /// 클라이언트 네트워크 스레드 시작
    /// </summary>
    public void StartClient()
    {
        if (_running) return;

        _running = true;
        _netThread = new Thread(NetworkMain)
        {
            IsBackground = true,
            Name = "TCP-Client"
        };
        _netThread.Start();
    }

    /// <summary>
    /// 클라이언트 종료 및 연결 해제
    /// </summary>
    public void StopClient()
    {
        _running = false;
        DisconnectInternal(clearDesiredUserId: true);
    }

    /// <summary>
    /// 네트워크 메인 루프.
    /// 연결 시도 → 수신 루프 → 끊기면 재접속 처리
    /// </summary>
    private void NetworkMain()
    {
        int backoffSec = 1;

        while (_running)
        {
            if (!IsConnected)
            {
                try
                {
                    ConnectOnce();
                    backoffSec = 1;
                }
                catch (Exception e)
                {
                    _mainThreadQueue.Enqueue(() =>
                        Debug.LogWarning($"[Client] Connect failed: {e.Message}")
                    );

                    DisconnectInternal(clearDesiredUserId: false);

                    if (!autoReconnect || !_running)
                        break;

                    int sleep = backoffSec;
                    backoffSec = Math.Min(backoffSec * 2, 4);
                    Thread.Sleep(sleep * 1000);
                    continue;
                }
            }

            try
            {
                byte[] recvBuf = new byte[8192];

                while (_running && IsConnected)
                {
                    int read = _stream.Read(recvBuf, 0, recvBuf.Length);
                    if (read <= 0)
                        throw new SocketException((int)SocketError.ConnectionReset);

                    _parser.Append(recvBuf, read);
                    _parser.ConsumeAllAvailable(OnPacketComplete);
                }
            }
            catch (Exception e)
            {
                _mainThreadQueue.Enqueue(() =>
                    Debug.LogWarning($"[Client] Disconnected/Read error: {e.Message}")
                );

                DisconnectInternal(clearDesiredUserId: !keepDesiredUserIdOnReconnect);

                if (!autoReconnect || !_running)
                    break;

                int sleep = backoffSec;
                backoffSec = Math.Min(backoffSec * 2, 4);
                Thread.Sleep(sleep * 1000);
            }
        }
    }

    /// <summary>
    /// 서버에 1회 연결 시도
    /// </summary>
    private void ConnectOnce()
    {
        DisconnectInternal(clearDesiredUserId: false);

        _tcp = new TcpClient
        {
            NoDelay = true
        };

        _tcp.Connect(host, port);
        _stream = _tcp.GetStream();

        IsConnected = true;

        _mainThreadQueue.Enqueue(() =>
        {
            Debug.Log($"[Client] Connected to {host}:{port}");
            OnConnectionStateChanged?.Invoke(true);
            OnPlayerAvailabilityChanged?.Invoke();
        });
    }

    /// <summary>
    /// 내부 연결 종료 처리.
    /// 상태 초기화 + 소켓 정리 + UI 갱신 이벤트 발생
    /// </summary>
    private void DisconnectInternal(bool clearDesiredUserId)
    {
        bool wasConnected = IsConnected;

        IsConnected = false;
        AssignedUserId = -1;

        if (clearDesiredUserId)
            desiredUserId = -1;

        ReceivedOffer = false;
        LastOfferMask = 0;

        _parser.Clear();

        try { _stream?.Close(); } catch { }
        try { _tcp?.Close(); } catch { }

        _stream = null;
        _tcp = null;

        _mainThreadQueue.Enqueue(() =>
        {
            if (wasConnected)
            {
                Debug.Log("[Client] Connection closed");
                OnConnectionStateChanged?.Invoke(false);
            }

            OnPlayerAvailabilityChanged?.Invoke();
        });
    }

    /// <summary>
    /// 서버로 패킷 전송
    /// </summary>
    public void Send(byte[] packet)
    {
        if (!IsConnected || packet == null || packet.Length == 0) return;

        lock (_sendLock)
        {
            try
            {
                _stream.Write(packet, 0, packet.Length);
            }
            catch
            {
                DisconnectInternal(clearDesiredUserId: !keepDesiredUserIdOnReconnect);
            }
        }
    }

    /// <summary>
    /// 원하는 사용자 ID를 서버에 요청
    /// </summary>
    private void SendSelectIdReq(byte desired)
    {
        var req = new SelectIdReq { DesiredId = desired };
        Send(PacketBuilder.Build(Msg.System, Sub.SelectIdReq, req));

        _mainThreadQueue.Enqueue(() =>
            Debug.Log($"[Client] Sent SelectIdReq desired={desired}")
        );
    }

    /// <summary>
    /// PacketStreamParser가 완성한 패킷을 Dispatcher로 전달
    /// </summary>
    private void OnPacketComplete(byte[] fullPacket)
    {
        _dispatcher.Dispatch(fullPacket);
    }

    /// <summary>
    /// 특정 ID가 현재 선택 가능한 상태인지 확인
    /// </summary>
    public bool IsPlayerAvailable(int id)
    {
        if (id < 0 || id > NetConst.MaxUserId)
            return false;

        return (LastOfferMask & (1 << id)) != 0;
    }
}