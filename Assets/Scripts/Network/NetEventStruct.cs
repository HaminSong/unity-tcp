using System.Runtime.InteropServices;

// NetCommon 스크립트에 이어지는 내용
// 주의:
// 모든 전송 구조체는 반드시
// - LayoutKind.Sequential
// - 동일한 Pack 값
// 을 유지해야 한다.
//
// 서버/클라이언트 구조가 다르면 데이터 깨짐 발생
namespace UnityTcp
{
    /// <summary>
    /// 메시지 대분류
    /// </summary>
    public enum Msg : ushort
    {
        System = 1,
        Chat = 2,
    }

    /// <summary>
    /// 메시지 소분류
    /// </summary>
    public enum Sub : ushort
    {
        // 시스템 플로우
        IdOffer = 2,           // 서버 -> 클라 : 현재 선택 가능한 ID 목록 전달
        SelectIdReq = 3,       // 클라 -> 서버 : 특정 ID를 선택 요청
        SelectIdAck = 4,       // 서버 -> 클라 : 선택 승인/거절 + 최신 가능 목록 전달

        ChatEventReq = 100,
    }

    /// <summary>
    /// 모든 TCP 패킷의 공통 헤더.
    ///
    /// 패킷 구조:
    /// [PacketHeader][Body]
    ///
    /// MessageId / SubMessageId 로 패킷 종류를 구분하고,
    /// PacketSize 로 전체 패킷 길이(Header + Body)를 알 수 있다.
    /// </summary>
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct PacketHeader
    {
        /// <summary>
        /// 대분류 메시지 ID
        /// </summary>
        public Msg MessageId;

        /// <summary>
        /// 소분류 메시지 ID
        /// </summary>
        public Sub SubMessageId;

        /// <summary>
        /// 패킷 전체 크기 = Header 크기 + Body 크기
        /// </summary>
        public int PacketSize; // Header + Body
    }

    /// <summary>
    /// 채팅 예시를 위한 구조체
    /// </summary>
    [StructLayout(LayoutKind.Sequential, Pack = 1, CharSet = CharSet.Ansi)]
    public struct ChatEventReq
    {
        public ushort UserId;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string Message;
    }
}