using System;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace UnityNetDiscovery
{
    /// <summary>
    /// Discovery 통신에서 사용하는 고정 문자열(매직 값) 정의.
    /// 
    /// 목적:
    /// - 일반 UDP 패킷과 구분하기 위한 식별자
    /// - 클라이언트 ↔ 서버 간 "Discovery 프로토콜" 일치 여부 확인
    /// 
    /// 흐름:
    /// Client → RequestMagic 전송
    /// Server → ResponseMagic 포함 응답
    /// </summary>
    public static class DiscoveryProtocol
    {
        /// <summary>
        /// 클라이언트가 브로드캐스트로 보내는 "서버 찾기 요청" 식별자
        /// </summary>
        public const string RequestMagic = "UNITY_NET_DISCOVERY_REQ_V1";

        /// <summary>
        /// 서버가 응답할 때 사용하는 "Discovery 응답" 식별자
        /// </summary>
        public const string ResponseMagic = "UNITY_NET_DISCOVERY_ACK_V1";
    }

    /// <summary>
    /// 서버가 Discovery 응답으로 전달하는 정보 구조체.
    /// 
    /// 역할:
    /// - 클라이언트가 접속해야 할 서버 정보 제공
    /// - TCP/UDP 포트 분리 구조 지원
    /// 
    /// 사용 시점:
    /// - UDP Discovery 성공 후 TCP 연결 전에 사용됨
    /// </summary>
    [Serializable]
    public struct DiscoveryResponse
    {
        /// <summary>
        /// 서버 이름 (UI 표시용, 디버깅용)
        /// </summary>
        public string ServerName;

        /// <summary>
        /// 이벤트용 TCP 포트 (기존 TCP 이벤트 시스템)
        /// </summary>
        public int EventTcpPort;

        /// <summary>
        /// 실시간 동기화용 UDP 포트
        /// </summary>
        public int SyncUdpPort;

        /// <summary>
        /// 실시간 동기화용 TCP 포트 (UDP fallback 또는 옵션)
        /// </summary>
        public int SyncTcpPort;
    }

    /// <summary>
    /// Discovery 패킷 생성 및 파싱 유틸.
    /// 
    /// 특징:
    /// - JSON/바이너리 대신 "간단한 문자열 프로토콜" 사용
    /// - 빠르고 가볍고 디버깅이 쉬움
    /// - UDP 브로드캐스트에 적합
    /// </summary>
    public static class DiscoveryPacket
    {
        /// <summary>
        /// 서버 탐색 요청 패킷 생성.
        /// 
        /// 내용:
        /// - 단순히 RequestMagic 문자열만 전송
        /// - 서버는 이 문자열을 보고 Discovery 요청인지 판단
        /// </summary>
        public static byte[] BuildRequest()
        {
            return Encoding.UTF8.GetBytes(DiscoveryProtocol.RequestMagic);
        }

        /// <summary>
        /// 수신된 데이터가 Discovery 요청인지 판별.
        /// 
        /// 검증:
        /// - null / empty 체크
        /// - 문자열 변환 후 RequestMagic과 비교
        /// </summary>
        public static bool IsRequest(byte[] data)
        {
            if (data == null || data.Length == 0)
                return false;

            string s = Encoding.UTF8.GetString(data);
            return s == DiscoveryProtocol.RequestMagic;
        }

        /// <summary>
        /// 서버 → 클라이언트 응답 패킷 생성.
        /// 
        /// 포맷:
        /// [ResponseMagic|ServerName|EventTcpPort|SyncUdpPort|SyncTcpPort]
        /// 
        /// 특징:
        /// - 구분자 '|' 기반 문자열 직렬화
        /// - 간단하지만 순서 의존적 (스키마 변경 시 주의)
        /// </summary>
        public static byte[] BuildResponse(DiscoveryResponse response)
        {
            string payload =
                DiscoveryProtocol.ResponseMagic + "|" +
                response.ServerName + "|" +
                response.EventTcpPort + "|" +
                response.SyncUdpPort + "|" +
                response.SyncTcpPort;

            return Encoding.UTF8.GetBytes(payload);
        }

        /// <summary>
        /// 서버 응답 패킷 파싱.
        /// 
        /// 처리 단계:
        /// 1. null / empty 체크
        /// 2. 문자열 변환
        /// 3. '|' 기준 분리
        /// 4. ResponseMagic 검증
        /// 5. 포트 값 파싱
        /// 
        /// 실패 조건:
        /// - 필드 개수 불일치
        /// - Magic 값 불일치
        /// - 포트 파싱 실패
        /// </summary>
        public static bool TryParseResponse(byte[] data, out DiscoveryResponse response)
        {
            response = default;

            if (data == null || data.Length == 0)
                return false;

            string s = Encoding.UTF8.GetString(data);

            // "Magic|Name|Port|Port|Port"
            string[] parts = s.Split('|');

            // 필드 개수 검증 (고정 5개)
            if (parts.Length != 5)
                return false;

            // Discovery 응답인지 확인
            if (parts[0] != DiscoveryProtocol.ResponseMagic)
                return false;

            int eventTcpPort;
            int syncUdpPort;
            int syncTcpPort;

            // 각 포트 숫자 파싱
            if (!int.TryParse(parts[2], out eventTcpPort))
                return false;

            if (!int.TryParse(parts[3], out syncUdpPort))
                return false;

            if (!int.TryParse(parts[4], out syncTcpPort))
                return false;

            // 구조체로 재구성
            response = new DiscoveryResponse
            {
                ServerName = parts[1],
                EventTcpPort = eventTcpPort,
                SyncUdpPort = syncUdpPort,
                SyncTcpPort = syncTcpPort
            };

            return true;
        }
    }

    /// <summary>
    /// 서버 자동 탐색 클라이언트.
    ///
    /// 동작:
    /// - UDP 브로드캐스트로 Discovery 요청 전송
    /// - 일정 시간 동안 응답 대기
    /// - 응답이 오면 해당 서버 정보를 반환
    ///
    /// 실패 시:
    /// - 서버를 찾지 못한 것으로 간주
    /// - 수동 IP 입력 fallback 필요
    /// </summary>
    public static class DiscoveryClient
    {
        public static bool TryDiscover(int discoveryPort, int timeoutMs, out IPAddress serverIp, out DiscoveryResponse response)
        {
            serverIp = null;
            response = default;

            using (UdpClient udp = new UdpClient())
            {
                udp.EnableBroadcast = true;
                udp.Client.ReceiveTimeout = timeoutMs;

                byte[] req = DiscoveryPacket.BuildRequest();
                IPEndPoint broadcastEp = new IPEndPoint(IPAddress.Broadcast, discoveryPort);
                udp.Send(req, req.Length, broadcastEp);

                IPEndPoint remote = new IPEndPoint(IPAddress.Any, 0);
                byte[] recv = udp.Receive(ref remote);

                DiscoveryResponse parsed;
                if (!DiscoveryPacket.TryParseResponse(recv, out parsed))
                    return false;

                serverIp = remote.Address;
                response = parsed;
                return true;
            }
        }
    }
}