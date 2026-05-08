using System;
using System.Collections.Generic;
using UnityEngine;
using UnityTcp;

/// <summary>
/// 클라이언트 수신 패킷을 MessageId / SubMessageId 기준으로
/// 등록된 핸들러에 분기하는 라우터.
///
/// 역할:
/// - 패킷 헤더 확인
/// - msg/sub 조합으로 handler 조회
/// - 해당 handler 호출
/// </summary>
public sealed class PacketDispatcher
{
    /// <summary>
    /// 패킷 라우팅 테이블
    /// key : msg/sub 조합
    /// value : 실제 처리 함수
    /// </summary>
    private readonly Dictionary<int, Action<byte[]>> _routes = new Dictionary<int, Action<byte[]>>();

    /// <summary>
    /// msg/sub 조합을 Dictionary 조회용 int key로 변환
    /// </summary>
    private static int MakeKey(ushort msg, ushort sub)
    {
        return (msg << 16) | sub;
    }
    /// <summary>
    /// Dictionary 조회용 key 생성
    /// </summary>
    private static int MakeKey(Msg msg, Sub sub)
    {
        return MakeKey((ushort)msg, (ushort)sub);
    }

    /// <summary>
    /// 특정 msg/sub 조합에 대한 패킷 핸들러 등록
    /// </summary>
    public void Register(Msg msg, Sub sub, Action<byte[]> handler)
    {
        if (handler == null)
            throw new ArgumentNullException(nameof(handler));

        int key = MakeKey(msg, sub);

        if (_routes.ContainsKey(key))
            throw new InvalidOperationException($"Route already registered: {msg}/{sub}");

        _routes[key] = handler;
    }

    /// <summary>
    /// 수신 패킷의 헤더를 읽고 등록된 handler로 전달
    /// </summary>
    public bool Dispatch(byte[] packet)
    {
        if (packet == null || packet.Length < NetConst.HeaderSize)
            return false;

        PacketHeader header = MarshalUtil.BytesToStruct<PacketHeader>(packet, 0);
        int key = MakeKey((ushort)header.MessageId, (ushort)header.SubMessageId);

        if (_routes.TryGetValue(key, out var handler))
        {
            handler(packet);
            return true;
        }

        // handler가 없는 경우:
        // - 해당 msg/sub에 대한 처리 코드가 등록되지 않은 상태
        // - 잘못된 패킷이거나 핸들러 누락 가능성 있음
        Debug.LogWarning($"[Dispatcher] No handler for {header.MessageId}/{header.SubMessageId}");
        return false;
    }
}

/// <summary>
/// 서버 수신 패킷을 MessageId / SubMessageId 기준으로
/// 등록된 핸들러에 분기하는 라우터.
///
/// 클라이언트 버전과 차이:
/// - 어떤 클라이언트가 보냈는지 식별하기 위해 sessionKey를 함께 전달
/// </summary>
public sealed class ServerPacketDispatcher
{
    /// <summary>
    /// 서버 패킷 라우팅 테이블
    /// key : msg/sub 조합
    /// value : handler(sessionKey, packet)
    /// </summary>
    private readonly Dictionary<int, Action<int, byte[]>> _routes = new Dictionary<int, Action<int, byte[]>>();

    private static int MakeKey(ushort msg, ushort sub)
    {
        return (msg << 16) | sub;
    }

    private static int MakeKey(Msg msg, Sub sub)
    {
        return MakeKey((ushort)msg, (ushort)sub);
    }

    /// <summary>
    /// 특정 msg/sub 조합에 대한 서버 패킷 핸들러 등록
    /// </summary>
    public void Register(Msg msg, Sub sub, Action<int, byte[]> handler)
    {
        if (handler == null)
            throw new ArgumentNullException(nameof(handler));

        int key = MakeKey(msg, sub);

        if (_routes.ContainsKey(key))
            throw new InvalidOperationException($"Route already registered: {msg}/{sub}");

        _routes[key] = handler;
    }

    /// <summary>
    /// 수신 패킷의 헤더를 읽고 등록된 handler로 전달
    /// </summary>
    public bool Dispatch(int sessionKey, byte[] packet)
    {
        if (packet == null || packet.Length < NetConst.HeaderSize)
            return false;

        PacketHeader header = MarshalUtil.BytesToStruct<PacketHeader>(packet, 0);
        int key = MakeKey((ushort)header.MessageId, (ushort)header.SubMessageId);

        if (_routes.TryGetValue(key, out var handler))
        {
            handler(sessionKey, packet);
            return true;
        }

        Debug.LogWarning($"[ServerDispatcher] No handler for {header.MessageId}/{header.SubMessageId}");
        return false;
    }
}