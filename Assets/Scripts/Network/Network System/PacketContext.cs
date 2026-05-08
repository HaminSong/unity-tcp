using System;
using UnityTcp;

/// <summary>
/// 클라이언트 패킷 핸들러가 UnityTcpClient를 직접 참조하지 않고
/// 필요한 기능만 사용하도록 중간에서 묶어주는 컨텍스트.
///
/// 목적:
/// - 핸들러와 실제 네트워크 컴포넌트 결합도 완화
/// - 테스트/확장 시 접근 지점 단순화
/// </summary>
public sealed class ClientPacketContext
{
    private readonly UnityTcpClient _client;

    public ClientPacketContext(UnityTcpClient client)
    {
        _client = client;
    }

    /// <summary>
    /// 서버가 승인한 내 사용자 ID
    /// </summary>
    public int AssignedUserId
    {
        get => _client.AssignedUserId;
        set => _client.AssignedUserId = value;
    }

    /// <summary>
    /// 서버가 마지막으로 보낸 사용 가능 ID 마스크
    /// </summary>
    public ushort LastOfferMask
    {
        get => _client.LastOfferMask;
        set => _client.LastOfferMask = value;
    }

    /// <summary>
    /// IdOffer를 수신했는지 여부
    /// </summary>
    public bool ReceivedOffer
    {
        get => _client.ReceivedOffer;
        set => _client.ReceivedOffer = value;
    }

    /// <summary>
    /// Unity 메인 스레드 작업 예약
    /// </summary>
    public void EnqueueMain(Action action)
    {
        _client.EnqueueMain(action);
    }

    /// <summary>
    /// 플레이어 선택 가능 상태 변경 이벤트 발생
    /// </summary>
    public void RaisePlayerAvailabilityChanged()
    {
        _client.RaisePlayerAvailabilityChanged();
    }

    /// <summary>
    /// 현재 상태 기준으로 SelectIdReq 즉시 전송 시도
    /// </summary>
    public void TrySendSelectIdReqNow()
    {
        _client.TrySendSelectIdReqNowInternal();
    }
}

/// <summary>
/// 서버 패킷 핸들러가 UnityTcpServer 내부 구현을 직접 다루지 않고
/// 필요한 기능만 접근하도록 묶어주는 컨텍스트.
///
/// 목적:
/// - Handler와 Server 본체의 역할 분리
/// - 내부 메서드 접근을 한 곳으로 정리
/// </summary>
public sealed class ServerPacketContext
{
    private readonly UnityTcpServer _server;

    public ServerPacketContext(UnityTcpServer server)
    {
        _server = server;
    }

    /// <summary>
    /// sessionKey로 클라이언트 핸들 조회
    /// </summary>
    public bool TryGetClient(int sessionKey, out UnityTcpServer.ClientConnHandle conn)
    {
        return _server.TryGetClientHandle(sessionKey, out conn);
    }

    /// <summary>
    /// 특정 ID 점유 시도
    /// </summary>
    public bool TryTakeId(int id)
    {
        return _server.TryTakeIdInternal(id);
    }

    /// <summary>
    /// 특정 ID 반납
    /// </summary>
    public void ReleaseId(int id)
    {
        _server.ReleaseIdInternal(id);
    }

    /// <summary>
    /// 현재 사용 가능 ID 마스크 생성
    /// </summary>
    public ushort BuildAvailableMask()
    {
        return _server.BuildAvailableMaskInternal();
    }

    /// <summary>
    /// 최신 사용 가능 ID 목록을 전체 클라이언트에 전파
    /// </summary>
    public void BroadcastAvailableMask()
    {
        _server.BroadcastAvailableMaskInternal();
    }

    /// <summary>
    /// Unity 메인 스레드 작업 예약
    /// </summary>
    public void EnqueueMain(Action action)
    {
        _server.EnqueueMain(action);
    }

    /// <summary>
    /// 공통 PacketBuilder를 이용한 패킷 생성
    /// </summary>
    public byte[] BuildPacket<T>(Msg msg, Sub sub, T body) where T : struct
    {
        return PacketBuilder.Build(msg, sub, body);
    }
}