using System;
using System.Collections.Generic;
using UnityEngine;
using UnityTcp;

/// <summary>
/// 클라이언트 측 System 계열 패킷 처리기.
///
/// 역할:
/// - 서버가 보낸 System 패킷을 처리
/// - 현재 선택 가능 ID 목록 갱신
/// - ID 선택 결과 반영
/// - UI 갱신 이벤트 발생
/// </summary>
public sealed class ClientSystemPacketHandler
{
    private readonly ClientPacketContext _ctx;

    private readonly List<RouteEntry> _pendingRoutes = new List<RouteEntry>();

    private struct RouteEntry
    {
        public Msg Msg;
        public Sub Sub;
        public Action<byte[]> Handler;
    }
    public ClientSystemPacketHandler(ClientPacketContext ctx)
    {
        _ctx = ctx ?? throw new ArgumentNullException(nameof(ctx));

        // 기본 시스템 패킷은 생성 시 등록 목록에 적재
        AddRoute(Msg.System, Sub.IdOffer, HandleIdOffer);
        AddRoute(Msg.System, Sub.SelectIdAck, HandleSelectIdAck);
    }
    /// <summary>
    /// 외부 기능 스크립트가 route 추가 요청하는 창구
    /// </summary>
    public void AddRoute(Msg msg, Sub sub, Action<byte[]> handler)
    {
        if (handler == null)
            throw new ArgumentNullException(nameof(handler));

        _pendingRoutes.Add(new RouteEntry
        {
            Msg = msg,
            Sub = sub,
            Handler = handler
        });
    }
    /// <summary>
    /// 클라이언트 Dispatcher에 System 패킷 핸들러 등록
    /// </summary>
    public void Register(PacketDispatcher dispatcher)
    {
        if (dispatcher == null)
            throw new ArgumentNullException(nameof(dispatcher));

        foreach (var route in _pendingRoutes)
            dispatcher.Register(route.Msg, route.Sub, route.Handler);
    }

    /// <summary>
    /// 서버가 보낸 "현재 선택 가능한 ID 목록" 처리.
    ///
    /// 처리 후:
    /// - 최신 마스크 저장
    /// - UI 갱신
    /// - 내가 이미 선택한 desiredUserId가 있으면 자동 재요청 시도
    /// </summary>
    private void HandleIdOffer(byte[] packet)
    {
        var offer = MarshalUtil.BytesToStruct<IdOffer>(packet, NetConst.HeaderSize);

        _ctx.LastOfferMask = offer.AvailableMask;
        _ctx.ReceivedOffer = true;

        _ctx.EnqueueMain(() =>
        {
            Debug.Log($"[Client] IdOffer available: {IdMaskUtil.MaskToString(_ctx.LastOfferMask)}");
            _ctx.RaisePlayerAvailabilityChanged();
        });

        _ctx.TrySendSelectIdReqNow();
    }

    /// <summary>
    /// 서버의 ID 선택 결과 응답 처리.
    ///
    /// 성공 시:
    /// - AssignedUserId 반영
    ///
    /// 실패 시:
    /// - AssignedUserId 초기화
    /// - 다음 Offer 기준으로 다시 선택 가능 상태로 전환
    /// </summary>
    private void HandleSelectIdAck(byte[] packet)
    {
        var ack = MarshalUtil.BytesToStruct<SelectIdAck>(packet, NetConst.HeaderSize);

        _ctx.LastOfferMask = ack.AvailableMask;

        if (ack.Success == 1)
        {
            _ctx.AssignedUserId = ack.AssignedId;

            _ctx.EnqueueMain(() =>
            {
                Debug.Log($"[Client] SelectId OK. AssignedUserId={_ctx.AssignedUserId}. Remain: {IdMaskUtil.MaskToString(_ctx.LastOfferMask)}");
                _ctx.RaisePlayerAvailabilityChanged();
            });
        }
        else
        {
            _ctx.AssignedUserId = -1;
            _ctx.ReceivedOffer = true;

            _ctx.EnqueueMain(() =>
            {
                Debug.LogWarning($"[Client] SelectId FAIL. Remain: {IdMaskUtil.MaskToString(_ctx.LastOfferMask)}");
                _ctx.RaisePlayerAvailabilityChanged();
            });
        }
    }
}