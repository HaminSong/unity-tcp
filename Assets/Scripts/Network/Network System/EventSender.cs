using System;
using UnityTcp;

namespace UnityTcpEvent
{
    /// <summary>
    /// 네트워크 송신 전용 중간 계층.
    ///
    /// 목적:
    /// - Unity 컴포넌트가 직접 TCP/UDP 코드에 접근하지 않도록 차단
    /// - 패킷 생성 및 전송 책임을 한 곳으로 집중
    /// - 테스트 및 구조 분리를 쉽게 하기 위함
    ///
    /// 즉, 게임 로직은 이 클래스를 통해서만 네트워크 송신을 수행한다.
    /// </summary>
    public sealed class ClientPacketSender
    {
        private readonly UnityTcpClient _client;

        public ClientPacketSender(UnityTcpClient client)
        {
            _client = client ?? throw new ArgumentNullException(nameof(client));
        }
        /// <summary>
        /// 서버로 패킷 전송
        /// </summary>
        /// <param name="packet"></param>
        public void Send(byte[] packet)
        {
            if (packet == null || packet.Length == 0)
                return;

            _client.Send(packet);
        }
    }

    /// <summary>
    /// 네트워크 송신 전용 중간 계층.
    ///
    /// 목적:
    /// - Unity 컴포넌트가 직접 TCP/UDP 코드에 접근하지 않도록 차단
    /// - 패킷 생성 및 전송 책임을 한 곳으로 집중
    /// - 테스트 및 구조 분리를 쉽게 하기 위함
    ///
    /// 즉, 게임 로직은 이 클래스를 통해서만 네트워크 송신을 수행한다.
    /// </summary>
    public sealed class ServerPacketSender
    {
        private readonly UnityTcpServer _server;

        public ServerPacketSender(UnityTcpServer server)
        {
            _server = server ?? throw new ArgumentNullException(nameof(server));
        }
        /// <summary>
        /// 특정 플레이어에게만 패킷 전송
        /// </summary>
        /// <param name="playerId"></param>
        /// <param name="packet"></param>
        public void SendToPlayer(int playerId, byte[] packet)
        {
            if (packet == null || packet.Length == 0)
                return;

            _server.SendToPlayer(playerId, packet);
        }
        /// <summary>
        /// 모든 플레이어에게 패킷 전송
        /// </summary>
        /// <param name="packet"></param>
        public void Broadcast(byte[] packet)
        {
            if (packet == null || packet.Length == 0)
                return;

            _server.Broadcast(packet);
        }
        /// <summary>
        /// 특정 플레이어 ID를 제외한 나머지 클라이언트에게 전송
        /// </summary>
        /// <param name="exceptPlayerId"></param>
        /// <param name="packet"></param>
        public void BroadcastExcept(int exceptPlayerId, byte[] packet)
        {
            if (packet == null || packet.Length == 0)
                return;

            _server.BroadcastExcept(exceptPlayerId, packet);
        }
    }
}