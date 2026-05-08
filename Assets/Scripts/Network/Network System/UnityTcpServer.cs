using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using UnityEngine;
using UnityTcp;

/// <summary>
/// Unity 서버용 TCP 통신 컴포넌트.
///
/// 역할:
/// 1. 클라이언트 접속 수락
/// 2. 클라이언트별 수신 스레드 관리
/// 3. 사용자 ID 점유 상태 관리
/// 4. 수신 패킷을 Dispatcher를 통해 Handler로 전달
/// </summary>
public class UnityTcpServer : MonoBehaviour
{
    public int port = 7777;

    private TcpListener _listener;
    private Thread _acceptThread;
    private volatile bool _running;

    // 접속 세션마다 고유 키 부여
    private int _nextSessionKey = 0;

    // 각 UserId(0~9)의 점유 여부
    // 서버는 중복 ID를 허용하지 않으며,
    // 클라이언트는 선택 요청 후 승인받아야 사용 가능하다.
    private readonly bool[] _taken = new bool[10];

    // 현재 접속 중인 클라이언트 목록
    private readonly Dictionary<int, ClientConn> _clients = new Dictionary<int, ClientConn>();

    // _clients, _taken 보호용 락
    private readonly object _lock = new object();

    // 네트워크 스레드에서 발생한 Unity 작업은 메인 스레드에서 처리
    private readonly ConcurrentQueue<Action> _mainThreadQueue = new ConcurrentQueue<Action>();

    private ServerPacketDispatcher _dispatcher;
    private ServerPacketContext _packetContext;
    private ServerSystemPacketHandler _systemHandler;


    void Awake()
    {
        // 패킷 분기기와 시스템 핸들러 초기화
        _dispatcher = new ServerPacketDispatcher();
        _packetContext = new ServerPacketContext(this);
        _systemHandler = new ServerSystemPacketHandler(_packetContext);
    }

    void Start()
    {
        // 외부에서 AddRoute 할 시간 확보 후 등록 반영
        _systemHandler.Register(_dispatcher);

        Application.runInBackground = true;
        StartServer();
    }

    void OnDestroy()
    {
        StopServer();
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
    /// 서버 시작 및 접속 대기 스레드 실행
    /// </summary>
    public void StartServer()
    {
        if (_running) return;

        _listener = new TcpListener(IPAddress.Any, port);
        _listener.Start();

        _running = true;
        _acceptThread = new Thread(AcceptLoop) { IsBackground = true, Name = "TCP-Accept" };
        _acceptThread.Start();

        Debug.Log($"[Server] Listening on {port}");
    }

    /// <summary>
    /// 서버 종료.
    /// 모든 클라이언트 연결과 점유 상태를 정리한다.
    /// </summary>
    public void StopServer()
    {
        if (!_running) return;
        _running = false;

        try { _listener?.Stop(); } catch { }

        lock (_lock)
        {
            foreach (var c in _clients.Values) c.Close();
            _clients.Clear();

            for (int i = 0; i < _taken.Length; i++) _taken[i] = false;
        }

        Debug.Log("[Server] Stopped");
    }

    /// <summary>
    /// 클라이언트 접속을 계속 수락하는 루프
    /// </summary>
    private void AcceptLoop()
    {
        while (_running)
        {
            try
            {
                var tcp = _listener.AcceptTcpClient();
                tcp.NoDelay = true;

                int sessionKey = Interlocked.Increment(ref _nextSessionKey);

                var conn = new ClientConn(sessionKey, tcp, _mainThreadQueue, OnPacketFromClient, OnClientDisconnected);

                lock (_lock) _clients[sessionKey] = conn;

                _mainThreadQueue.Enqueue(() =>
                    Debug.Log($"[Server] Connected sessionKey={sessionKey}, remote={tcp.Client.RemoteEndPoint}")
                );

                conn.Start();

                // 새 클라이언트에게 현재 사용 가능한 ID 목록 전달
                SendIdOfferTo(conn);
            }
            catch (SocketException)
            {
                if (!_running) break;
            }
            catch (Exception e)
            {
                _mainThreadQueue.Enqueue(() => Debug.LogException(e));
            }
        }
    }

    /// <summary>
    /// 외부에서 서버 수신 라우트 추가
    /// </summary>
    public void AddRoute(Msg msg, Sub sub, Action<int, byte[]> handler)
    {
        _systemHandler.AddRoute(msg, sub, handler);
    }

    /// <summary>
    /// 클라이언트에서 온 완성 패킷을 Dispatcher로 전달
    /// </summary>
    private void OnPacketFromClient(int sessionKey, byte[] fullPacket)
    {
        _dispatcher.Dispatch(sessionKey, fullPacket);
    }

    /// <summary>
    /// 특정 클라이언트에게 현재 사용 가능한 ID 목록 전송
    /// </summary>
    private void SendIdOfferTo(ClientConn conn)
    {
        ushort mask;

        lock (_lock)
        {
            mask = BuildAvailableMask();
        }

        var offer = new IdOffer
        {
            AvailableMask = mask
        };

        conn.Send(PacketBuilder.Build(Msg.System, Sub.IdOffer, offer));

        _mainThreadQueue.Enqueue(() =>
            Debug.Log($"[Server] Send IdOffer to new client => {IdMaskUtil.MaskToString(mask)}"));
    }

    /// <summary>
    /// 클라이언트 연결 종료 처리.
    /// 연결 목록 제거 후, 점유 중이던 ID가 있으면 반환한다.
    /// </summary>
    private void OnClientDisconnected(int sessionKey)
    {
        int releasedId = -1;
        bool changed = false;

        lock (_lock)
        {
            if (_clients.TryGetValue(sessionKey, out var conn))
            {
                releasedId = conn.AssignedUserId;
                conn.Close();
                _clients.Remove(sessionKey);

                if (releasedId >= 0 && releasedId <= 9)
                {
                    _taken[releasedId] = false;
                    changed = true;
                }
            }
        }

        _mainThreadQueue.Enqueue(() =>
            Debug.Log($"[Server] Disconnected sessionKey={sessionKey}, releasedUserId={releasedId}")
        );

        // 사용 가능 ID 목록이 바뀌었으면 전체 클라이언트에게 재전송
        if (changed)
            BroadcastAvailableMask();
    }

    /// <summary>
    /// 현재 선택 가능한 ID를 비트마스크로 생성
    /// </summary>
    private ushort BuildAvailableMask()
    {
        ushort mask = 0;
        for (int i = 0; i <= 9; i++)
        {
            if (!_taken[i]) mask |= (ushort)(1 << i);
        }
        return mask;
    }

    /// <summary>
    /// 특정 ID를 점유 시도
    /// </summary>
    private bool TryTakeId(int id)
    {
        if (id < 0 || id > 9) return false;
        if (_taken[id]) return false;
        _taken[id] = true;
        return true;
    }

    /// <summary>
    /// 현재 사용 가능 ID 목록을 모든 클라이언트에게 전송
    /// </summary>
    private void BroadcastAvailableMask()
    {
        List<ClientConn> targets = new List<ClientConn>();
        ushort mask;

        lock (_lock)
        {
            mask = BuildAvailableMask();

            foreach (var conn in _clients.Values)
                targets.Add(conn);
        }

        var offer = new IdOffer { AvailableMask = mask };
        byte[] packet = PacketBuilder.Build(Msg.System, Sub.IdOffer, offer);

        foreach (var conn in targets)
            conn.Send(packet);

        _mainThreadQueue.Enqueue(() =>
            Debug.Log($"[Server] Broadcast IdOffer => {IdMaskUtil.MaskToString(mask)}"));
    }

    /// <summary>
    /// 특정 플레이어가 붙었는지 확인
    /// </summary>
    /// <param name="playerId">userID, 세션아이디 아님</param>
    /// <returns></returns>
    public bool IsPlayerConnected(int playerId)
    {
        if (playerId < 0 || playerId > NetConst.MaxUserId)
            return false;

        lock (_lock)
        {
            return _taken[playerId];
        }
    }

    /// <summary>
    /// 현재 각 Player ID의 연결 상태를 복사본으로 반환
    /// </summary>
    public bool[] GetConnectedPlayerStates()
    {
        lock (_lock)
        {
            bool[] copy = new bool[_taken.Length];
            Array.Copy(_taken, copy, _taken.Length);
            return copy;
        }
    }

    public List<int> GetConnectedPlayerIds()
    {
        List<int> ids = new List<int>();

        lock (_lock)
        {
            for (int i = 0; i < _taken.Length; i++)
            {
                if (_taken[i])
                    ids.Add(i);
            }
        }

        return ids;
    }

    public string GetConnectedPlayerSummary()
    {
        List<int> ids = GetConnectedPlayerIds();
        return ids.Count == 0 ? "(none)" : string.Join(", ", ids);
    }

    /// <summary>
    /// 특정 Player ID에게만 패킷 전송
    /// </summary>
    /// <param name="playerId">userID, 세션아이디 아님</param>
    /// <param name="packet">메시지 패킷</param>
    public void SendToPlayer(int playerId, byte[] packet)
    {
        ClientConn target = null;

        lock (_lock)
        {
            foreach (var conn in _clients.Values)
            {
                if (conn.AssignedUserId == playerId)
                {
                    target = conn;
                    break;
                }
            }
        }

        target?.Send(packet);
    }

    /// <summary>
    /// 전체 클라이언트에게 패킷 전송
    /// </summary>
    public void Broadcast(byte[] packet)
    {
        List<ClientConn> targets = new List<ClientConn>();

        lock (_lock)
        {
            foreach (var conn in _clients.Values)
                targets.Add(conn);
        }

        foreach (var conn in targets)
            conn.Send(packet);
    }

    /// <summary>
    /// 특정 Player ID를 제외한 나머지 클라이언트에게 전송
    /// </summary>
    public void BroadcastExcept(int exceptPlayerId, byte[] packet)
    {
        List<ClientConn> targets = new List<ClientConn>();

        lock (_lock)
        {
            foreach (var conn in _clients.Values)
            {
                if (conn.AssignedUserId != exceptPlayerId)
                    targets.Add(conn);
            }
        }

        foreach (var conn in targets)
            conn.Send(packet);
    }

    /// <summary>
    /// 서버 내부 ClientConn을 직접 노출하지 않기 위한 핸들 래퍼 조회
    /// </summary>
    public bool TryGetClientHandle(int sessionKey, out ClientConnHandle conn)
    {
        lock (_lock)
        {
            if (_clients.TryGetValue(sessionKey, out var client))
            {
                conn = new ClientConnHandle(client, _lock);
                return true;
            }
        }

        conn = null;
        return false;
    }

    public bool TryTakeIdInternal(int id)
    {
        lock (_lock)
        {
            return TryTakeId(id);
        }
    }

    public void ReleaseIdInternal(int id)
    {
        lock (_lock)
        {
            if (id < 0 || id > NetConst.MaxUserId)
                return;

            _taken[id] = false;
        }
    }

    public ushort BuildAvailableMaskInternal()
    {
        lock (_lock)
        {
            return BuildAvailableMask();
        }
    }

    public void BroadcastAvailableMaskInternal()
    {
        BroadcastAvailableMask();
    }

    public void EnqueueMain(Action action)
    {
        if (action == null) return;
        _mainThreadQueue.Enqueue(action);
    }


    /// <summary>
    /// Handler 쪽에서 안전하게 클라이언트 상태를 다루기 위한 래퍼
    /// </summary>
    public sealed class ClientConnHandle
    {
        private readonly ClientConn _conn;
        private readonly object _serverLock;

        internal ClientConnHandle(ClientConn conn, object serverLock)
        {
            _conn = conn;
            _serverLock = serverLock;
        }

        public object SyncRoot => _serverLock;

        public int AssignedUserId
        {
            get => _conn.AssignedUserId;
            set => _conn.AssignedUserId = value;
        }

        public void Send(byte[] packet)
        {
            _conn.Send(packet);
        }
    }

    /// <summary>
    /// 클라이언트 1개 연결을 나타내는 내부 객체
    /// </summary>
    public sealed class ClientConn
    {
        /// <summary>
        /// 이 클라이언트에 최종 할당된 userID
        /// </summary>
        public int AssignedUserId { get; set; } = -1;

        private readonly int _sessionKey;
        private readonly TcpClient _tcp;
        private readonly NetworkStream _stream;

        // 클라이언트별 수신 전용 스레드
        private readonly Thread _recvThread;
        private volatile bool _alive;

        private readonly PacketStreamParser _parser = new PacketStreamParser();
        private readonly ConcurrentQueue<Action> _mainThreadQueue;
        private readonly Action<int, byte[]> _onPacket;
        private readonly Action<int> _onDisconnected;

        // 같은 연결에 대한 송신 동기화
        private readonly object _sendLock = new object();
        // Close 중복 호출 방지
        private int _closed = 0;

        public ClientConn(int sessionKey, TcpClient tcp, ConcurrentQueue<Action> mainThreadQueue,
            Action<int, byte[]> onPacket, Action<int> onDisconnected)
        {
            _sessionKey = sessionKey;
            _tcp = tcp;
            _stream = tcp.GetStream();
            _mainThreadQueue = mainThreadQueue;
            _onPacket = onPacket;
            _onDisconnected = onDisconnected;

            _recvThread = new Thread(RecvLoop) { IsBackground = true, Name = $"TCP-Recv-{sessionKey}" };
        }

        public void Start()
        {
            _alive = true;
            _recvThread.Start();
        }

        /// <summary>
        /// 연결 종료 및 소켓 정리
        /// </summary>
        public void Close()
        {
            if (Interlocked.Exchange(ref _closed, 1) == 1) return;
            _alive = false;
            try { _stream?.Close(); } catch { }
            try { _tcp?.Close(); } catch { }
        }

        /// <summary>
        /// 이 클라이언트에게 패킷 전송
        /// </summary>
        public void Send(byte[] packet)
        {
            if (packet == null || packet.Length == 0) return;
            if (!_tcp.Connected) return;

            lock (_sendLock)
            {
                try { _stream.Write(packet, 0, packet.Length); }
                catch { _onDisconnected?.Invoke(_sessionKey); }
            }
        }

        /// <summary>
        /// 클라이언트별 수신 루프.
        /// 수신 바이트를 PacketStreamParser로 넘겨 완전한 패킷 단위로 처리한다.
        /// </summary>
        private void RecvLoop()
        {
            byte[] recvBuf = new byte[8192];
            try
            {
                while (_alive)
                {
                    int read = _stream.Read(recvBuf, 0, recvBuf.Length);
                    if (read <= 0) break;

                    _parser.Append(recvBuf, read);
                    _parser.ConsumeAllAvailable(fullPacket =>
                    {
                        _onPacket?.Invoke(_sessionKey, fullPacket);
                    });
                }
            }
            catch (Exception e)
            {
                _mainThreadQueue.Enqueue(() => Debug.LogException(e));
            }
            finally
            {
                _onDisconnected?.Invoke(_sessionKey);
            }
        }
    }
}