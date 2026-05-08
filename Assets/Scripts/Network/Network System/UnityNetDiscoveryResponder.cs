using System;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using UnityEngine;
using UnityNetDiscovery;

/// <summary>
/// LAN 환경에서 "서버 자동 탐색"을 위한 응답기(Responder).
///
/// 역할:
/// - 클라이언트가 UDP 브로드캐스트로 서버를 찾으면
/// - 해당 요청을 수신하고 자신의 접속 정보를 응답으로 돌려준다.
///
/// 동작 흐름:
/// 1. 클라이언트가 "Discovery 요청" 패킷을 브로드캐스트로 전송
/// 2. 이 컴포넌트가 해당 패킷을 수신
/// 3. 서버의 IP 및 포트 정보를 담아 응답 전송
/// 4. 클라이언트는 응답을 받은 서버로 TCP/UDP 연결 수행
///
/// 주의:
/// - 이 컴포넌트는 "연결"을 처리하지 않는다.
/// - 실제 통신은 UnityTcpClient / UnityNetSyncClient에서 수행된다.
/// - 즉, Discovery는 "서버 주소를 알아내는 단계"일 뿐이다.
/// </summary>
public sealed class UnityNetDiscoveryResponder : MonoBehaviour
{
    [Header("Discovery")]
    [Tooltip("Start문에서 시작할지?")]
    [SerializeField] private bool autoStart = true;
    [Tooltip("탐색용 port")]
    [SerializeField] private int discoveryPort = 7776;
    [SerializeField] private string serverName = "UnityServer";

    [Header("Ports")]
    [SerializeField] private UnityTcpServer tcpServer;

    // Discovery 요청을 수신하기 위한 UDP 소켓
    private UdpClient _udp;

    //UDP 수신 루프를 실행하는 백그라운드 스레드
    private Thread _thread;

    //수신 루프 실행 상태 플래그
    private volatile bool _running;

    private void Start()
    {
        if (autoStart)
            StartResponder();
    }

    private void OnDestroy()
    {
        StopResponder();
    }
    private void OnApplicationQuit()
    {
        StopResponder();
    }

    /// <summary>
    /// Discovery 응답 시스템 시작.
    ///
    /// UDP 소켓을 열고 별도 스레드에서 클라이언트의 브로드캐스트 요청을 대기한다.
    ///
    /// ※ 서버를 찾기 위한 용도
    /// </summary>
    public void StartResponder()
    {
        if (_running)
            return;

        _udp = new UdpClient(discoveryPort);
        _udp.EnableBroadcast = true;
        _running = true;

        _thread = new Thread(Loop)
        {
            IsBackground = true,
            Name = "Discovery-Responder"
        };
        _thread.Start();

        Debug.Log("[Discovery] Responder started on port " + discoveryPort);
    }

    public void StopResponder()
    {
        _running = false;

        try { _udp?.Close(); } catch { }

        if (_thread != null && _thread.IsAlive)
        {
            if (!_thread.Join(500))
                Debug.LogWarning("[Discovery] Responder thread did not stop within timeout.");
        }

        _thread = null;
        _udp = null;
    }

    /// <summary>
    /// Discovery 요청 수신 루프.
    ///
    /// 동작:
    /// - 클라이언트가 UDP 브로드캐스트로 보낸 요청을 수신
    /// - 요청을 보낸 클라이언트에게 서버 정보 응답
    ///
    /// 왜 UDP인가?
    /// - TCP는 연결 대상(IP)을 알아야 접속 가능
    /// - Discovery 단계에서는 서버 IP를 모르기 때문에 브로드캐스트 가능한 UDP를 사용해야 함
    ///
    /// 특징:
    /// - 연결 없이 요청/응답 가능
    /// - 같은 LAN 환경에서만 동작
    /// </summary>
    private void Loop()
    {
        IPEndPoint remote = new IPEndPoint(IPAddress.Any, 0);

        while (_running)
        {
            try
            {
                byte[] data = _udp.Receive(ref remote);
                if (!DiscoveryPacket.IsRequest(data))
                    continue;

                DiscoveryResponse res = new DiscoveryResponse
                {
                    ServerName = serverName,
                    EventTcpPort = tcpServer != null ? tcpServer.port : 0,
                };

                byte[] ack = DiscoveryPacket.BuildResponse(res);
                _udp.Send(ack, ack.Length, remote);
            }
            catch (SocketException)
            {
                if (!_running)
                    break;
            }
            catch (ObjectDisposedException)
            {
                break;
            }
            catch (Exception e)
            {
                Debug.LogException(e);
            }
        }
    }
}