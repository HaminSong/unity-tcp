using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace UnityTcp
{
    /// <summary>
    /// 네트워크 공통 상수 정의
    /// </summary>
    public static class NetConst
    {
        /// <summary>
        /// PacketHeader 구조체의 실제 바이트 크기.
        /// 패킷 파싱 시 헤더 길이 계산에 사용
        /// </summary>
        public static readonly int HeaderSize = Marshal.SizeOf<PacketHeader>();

        /// <summary>
        /// 허용 가능한 최대 패킷 크기
        /// </summary>
        public const int MaxPacketSize = 1024 * 1024;

        /// <summary>
        /// 사용 가능한 최대 User ID
        /// </summary>
        public const int MaxUserId = 9; // 0~9
    }

    // 주의:
    // 모든 전송 구조체는 반드시
    // - LayoutKind.Sequential
    // - 동일한 Pack 값
    // 을 유지해야 한다.
    //
    // 서버/클라이언트 구조가 다르면 데이터 깨짐 발생
    #region 추가 구조체
    /// <summary>
    /// 서버가 클라이언트에게 전달하는 "현재 사용 가능한 ID 목록" 패킷 바디.
    /// 예) 0~9 전부 가능 -> 0b11_1111_1111 = 1023
    /// </summary>
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct IdOffer
    {
        /// <summary>
        /// 하위 10비트만 사용
        /// </summary>
        public ushort AvailableMask;
    }

    /// <summary>
    /// 클라이언트가 특정 ID를 선택하겠다고 서버에 요청하는 패킷 바디
    /// </summary>
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct SelectIdReq
    {
        public byte DesiredId; // 0~9
    }

    /// <summary>
    /// 서버가 ID 선택 요청에 대한 결과를 반환하는 패킷 바디
    /// </summary>
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct SelectIdAck
    {
        /// <summary>
        /// 선택 성공 여부 1 = 성공, 0 = 실패
        /// </summary>
        public byte Success;

        /// <summary>
        /// 실제 할당된 ID 성공 시 선택된 ID, 실패 시 255
        /// </summary>
        public byte AssignedId;

        /// <summary>
        /// 응답 시점의 최신 사용 가능 ID 목록
        /// </summary>
        public ushort AvailableMask;
    }
    #endregion

    #region Packet Serialization & Utilities

    /// <summary>
    /// [구조체 - 바이트] 배열 변환 유틸.
    ///
    /// 이 프로젝트는 TCP 송수신 시 구조체를 바이너리 바이트로 변환해서 사용하므로 패킷 Body 직렬화/역직렬화의 핵심 역할을 한다.
    /// </summary>
    public static class MarshalUtil
    {
        /// <summary>
        /// 구조체를 바이트 배열로 변환
        /// </summary>
        public static byte[] StructToBytes<T>(in T value) where T : struct
        {
            int size = Marshal.SizeOf<T>();
            byte[] bytes = new byte[size];

            IntPtr ptr = Marshal.AllocHGlobal(size);
            try
            {
                Marshal.StructureToPtr(value, ptr, false);
                Marshal.Copy(ptr, bytes, 0, size);
                return bytes;
            }
            finally
            {
                Marshal.FreeHGlobal(ptr);
            }
        }

        /// <summary>
        /// 바이트 배열의 특정 위치(offset)에서 구조체를 복원
        /// </summary>
        public static T BytesToStruct<T>(byte[] bytes, int offset = 0) where T : struct
        {
            int size = Marshal.SizeOf<T>();
            if (bytes == null) throw new ArgumentNullException(nameof(bytes));
            if (offset < 0 || offset + size > bytes.Length) throw new ArgumentOutOfRangeException(nameof(offset));

            IntPtr ptr = Marshal.AllocHGlobal(size);
            try
            {
                Marshal.Copy(bytes, offset, ptr, size);
                return Marshal.PtrToStructure<T>(ptr);
            }
            finally
            {
                Marshal.FreeHGlobal(ptr);
            }
        }

        /// <summary>
        /// 구조체를 바이트 배열의 특정 위치에 직접 기록. PacketBuilder에서 Header를 packet 버퍼 앞부분에 기록하기 위함
        /// </summary>
        public static void WriteStructToBuffer<T>(in T value, byte[] dst, int offset) where T : struct
        {
            var bytes = StructToBytes(value);
            Buffer.BlockCopy(bytes, 0, dst, offset, bytes.Length);
        }
    }

    /// <summary>
    /// 패킷 생성 전용 유틸.
    ///
    /// 역할:
    /// 1. Body 구조체를 바이트로 변환
    /// 2. Header 작성
    /// 3. [Header][Body] 형태의 최종 패킷 생성
    /// </summary>
    public static class PacketBuilder
    {
        /// <summary>
        /// Body 구조체를 받아 완전한 패킷(byte[])으로 만든다.
        ///
        /// 결과 형태:
        /// [PacketHeader][Body]
        /// </summary>
        public static byte[] Build<TBody>(Msg msgId, Sub subId, in TBody body) where TBody : struct
        {
            byte[] bodyBytes = MarshalUtil.StructToBytes(body);
            int packetSize = NetConst.HeaderSize + bodyBytes.Length;

            // 비정상적인 패킷 크기 방어
            if (packetSize < NetConst.HeaderSize || packetSize > NetConst.MaxPacketSize)
                throw new InvalidOperationException($"Invalid packetSize={packetSize}");

            PacketHeader header = new PacketHeader
            {
                MessageId = msgId,
                SubMessageId = subId,
                PacketSize = packetSize
            };

            byte[] packet = new byte[packetSize];

            // 패킷 앞부분에 Header 기록
            MarshalUtil.WriteStructToBuffer(header, packet, 0);

            // Header 뒤에 Body 기록
            Buffer.BlockCopy(bodyBytes, 0, packet, NetConst.HeaderSize, bodyBytes.Length);
            return packet;
        }
    }

    /// <summary>
    /// TCP 스트림에서 "온전한 패킷" 단위로 잘라내는 파서.
    ///
    /// TCP는 메시지 단위가 아니라 바이트 스트림이기 때문에,
    /// 다음과 같은 상황이 발생할 수 있다.
    ///
    /// - 패킷 1개가 여러 번에 나뉘어 도착
    /// - 패킷 여러 개가 한 번에 붙어서 도착
    ///
    /// 따라서 Read() 결과를 그대로 패킷 1개라고 가정하면 안 되고,
    /// 내부 버퍼에 누적한 뒤 Header.PacketSize 기준으로 완성된 패킷만 꺼내야 한다.
    /// </summary>
    public sealed class PacketStreamParser
    {
        /// <summary>
        /// 아직 완전한 패킷으로 조립되지 않은 수신 데이터 버퍼
        /// </summary>
        private readonly List<byte> _buffer = new List<byte>(8192);

        /// <summary>
        /// 새로 읽은 바이트를 내부 버퍼에 추가
        /// </summary>
        public void Append(byte[] src, int count)
        {
            if (src == null) throw new ArgumentNullException(nameof(src));
            if (count <= 0) return;

            for (int i = 0; i < count; i++)
                _buffer.Add(src[i]);
        }

        /// <summary>
        /// 현재 버퍼에서 완성된 패킷이 있는 동안 계속 꺼내서 콜백 호출.
        ///
        /// 처리 순서:
        /// 1. 헤더 길이만큼 데이터가 있는지 확인
        /// 2. Header 파싱 후 PacketSize 확인
        /// 3. 패킷 전체 길이만큼 버퍼에 쌓였는지 확인
        /// 4. 완성된 패킷 하나를 잘라 콜백으로 전달
        /// </summary>
        public void ConsumeAllAvailable(Action<byte[]> onPacketComplete)
        {
            if (onPacketComplete == null) throw new ArgumentNullException(nameof(onPacketComplete));

            while (true)
            {
                // 헤더조차 다 안 들어온 상태면 더 기다림
                if (_buffer.Count < NetConst.HeaderSize)
                    return;

                byte[] headerBytes = _buffer.GetRange(0, NetConst.HeaderSize).ToArray();
                PacketHeader header = MarshalUtil.BytesToStruct<PacketHeader>(headerBytes);

                // PacketSize 방어 검증
                if (header.PacketSize < NetConst.HeaderSize || header.PacketSize > NetConst.MaxPacketSize)
                    throw new InvalidOperationException($"Invalid PacketSize={header.PacketSize} (bufferCount={_buffer.Count})");

                // 패킷 전체가 아직 안 들어왔으면 더 기다림
                if (_buffer.Count < header.PacketSize)
                    return;

                // 완전한 패킷 1개 추출
                byte[] fullPacket = _buffer.GetRange(0, header.PacketSize).ToArray();

                // 추출한 만큼 버퍼에서 제거
                _buffer.RemoveRange(0, header.PacketSize);

                // 상위 로직(Dispatcher 등)에 완성 패킷 전달
                onPacketComplete(fullPacket);
            }
        }

        /// <summary>
        /// 연결 종료/재연결 시 내부 버퍼 초기화
        /// </summary>
        public void Clear() => _buffer.Clear();
    }

    /// <summary>
    /// User ID 비트마스크 해석용 유틸.
    ///
    /// 현재 ID 사용 가능 상태를 ushort의 비트로 표현하기 때문에
    /// 사람이 읽기 쉽게 판별/문자열화하는 역할을 담당한다.
    /// </summary>
    public static class IdMaskUtil
    {
        /// <summary>
        /// 특정 ID가 현재 사용 가능한지 확인
        /// </summary>
        public static bool IsAvailable(ushort mask, int id)
        {
            if (id < 0 || id > NetConst.MaxUserId) return false;
            return (mask & (1 << id)) != 0;
        }

        /// <summary>
        /// 사용 가능한 ID 목록을 사람이 읽기 쉬운 문자열로 변환
        ///
        /// 예)
        /// "0 1 3 4"
        /// "(none)"
        /// </summary>
        public static string MaskToString(ushort mask)
        {
            List<int> ids = new List<int>();
            for (int i = 0; i <= NetConst.MaxUserId; i++)
                if (IsAvailable(mask, i)) ids.Add(i);
            return ids.Count == 0 ? "(none)" : string.Join(" ", ids);
        }
    }
    #endregion
}