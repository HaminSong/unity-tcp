using System;
using System.Collections.Generic;
using UnityEngine;
using UnityTcp;

/// <summary>
/// 서버 측 System 계열 패킷 처리기.
///
/// 역할:
/// - Dispatcher에 System 패킷 핸들러 등록
/// - 클라이언트의 ID 선택 요청을 검증/반영
/// - 결과를 요청한 클라이언트와 전체 클라이언트에 전파
/// </summary>
public sealed class ServerSystemPacketHandler
{
    private readonly ServerPacketContext _ctx;

    // msg/sub/handler 등록 요청을 임시 보관
    private readonly List<RouteEntry> _pendingRoutes = new List<RouteEntry>();

    private struct RouteEntry
    {
        public Msg Msg;
        public Sub Sub;
        public Action<int, byte[]> Handler;
    }
    public ServerSystemPacketHandler(ServerPacketContext ctx)
    {
        _ctx = ctx;

        // 기본 시스템 핸들러는 생성 시 미리 적재
        AddRoute(Msg.System, Sub.SelectIdReq, HandleSelectIdReq);
    }
    /// <summary>
    /// 외부에서 msg/sub/handler를 추가할 수 있는 창구
    /// </summary>
    public void AddRoute(Msg msg, Sub sub, Action<int, byte[]> handler)
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
    /// 서버 Dispatcher에 System 패킷 핸들러 등록
    /// </summary>
    public void Register(ServerPacketDispatcher dispatcher)
    {
        if (dispatcher == null)
            throw new ArgumentNullException(nameof(dispatcher));

        foreach (var route in _pendingRoutes)
            dispatcher.Register(route.Msg, route.Sub, route.Handler);
    }

    /// <summary>
    /// 클라이언트의 ID 선택 요청 처리.
    ///
    /// 처리 흐름:
    /// 1. 요청한 ID 파싱
    /// 2. 기존에 점유 중인 ID가 있으면 반납
    /// 3. 새 ID 점유 시도
    /// 4. 결과 Ack 전송
    /// 5. 전체 클라이언트에 최신 가능 목록 브로드캐스트
    /// </summary>
    private void HandleSelectIdReq(int sessionKey, byte[] packet)
    {
        var req = MarshalUtil.BytesToStruct<SelectIdReq>(packet, NetConst.HeaderSize);

        if (!_ctx.TryGetClient(sessionKey, out var conn))
            return;

        bool success;
        ushort maskAfter;
        int desired = req.DesiredId;

        // ID 점유 상태와 클라이언트 할당 상태는 공유 자원이므로 동기화 필요
        lock (conn.SyncRoot)
        {
            int oldUserId = conn.AssignedUserId;
            if (oldUserId >= 0 && oldUserId <= NetConst.MaxUserId)
            {
                _ctx.ReleaseId(oldUserId);
                conn.AssignedUserId = -1;
            }

            success = _ctx.TryTakeId(desired);
            if (success)
                conn.AssignedUserId = desired;

            maskAfter = _ctx.BuildAvailableMask();
        }

        // Unity 로그는 메인 스레드에서 처리
        _ctx.EnqueueMain(() =>
        {
            if (success)
                Debug.Log($"[Server] SelectId OK sessionKey={sessionKey} => userId={desired}, remain: {IdMaskUtil.MaskToString(maskAfter)}");
            else
                Debug.LogWarning($"[Server] SelectId FAIL sessionKey={sessionKey} desired={desired}, remain: {IdMaskUtil.MaskToString(maskAfter)}");
        });

        var ack = new SelectIdAck
        {
            Success = (byte)(success ? 1 : 0),
            AssignedId = (byte)(success ? desired : 255),
            AvailableMask = maskAfter
        };

        // 요청한 클라이언트에게 결과 응답
        conn.Send(_ctx.BuildPacket(Msg.System, Sub.SelectIdAck, ack));

        // 전체 클라이언트에게 최신 가능 목록 전파
        _ctx.BroadcastAvailableMask();
    }
}