using System;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
using UnityTcp;
using UnityTcpEvent;

/// <summary>
/// 간단하게 메시지를 주고 받는 서버 예시 코드
/// </summary>
public class ChatServer : MonoBehaviour
{
    [SerializeField] private UnityTcpServer server;

    [Header("Chat UI")]
    [SerializeField] private TMP_InputField inputField;
    [SerializeField] private TMP_Text message;
    [SerializeField] private Button sendButton;

    private ServerPacketSender _sender;

    private void Awake()
    {
        _sender = new ServerPacketSender(server);

        // dispatcher 직접 접근 X
        // 핸들러를 통해 register 항목 추가
        server.AddRoute(Msg.Chat, Sub.ChatEventReq, HandleChatEventReq);
    }
    private void OnEnable()
    {
        if (sendButton != null)
            sendButton.onClick.AddListener(BroadcastMessage);
    }
    private void OnDisable()
    {
        if (sendButton != null)
            sendButton.onClick.RemoveListener(BroadcastMessage);
    }

    private void HandleChatEventReq(int sessionKey, byte[] packet)
    {
        var req = MarshalUtil.BytesToStruct<ChatEventReq>(packet, NetConst.HeaderSize);

        Debug.Log($"[ChatServer] sessionKey={sessionKey}, userId={req.UserId}, msg={req.Message}");

        server.EnqueueMain(() =>
        {
            if (message != null)
                message.SetText($"{(req.UserId < 999 ? ("Player" + (req.UserId + 1)) : "Server")}: {req.Message}");
        });

        // 예시: 받은 채팅을 전체 브로드캐스트
        byte[] outPacket = PacketBuilder.Build(Msg.Chat, Sub.ChatEventReq, req);
        _sender.Broadcast(outPacket);
    }

    public void BroadcastMessage()
    {
        ChatEventReq body = new ChatEventReq
        {
            UserId = 999,
            Message = inputField.text
        };

        byte[] packet = PacketBuilder.Build(Msg.Chat, Sub.ChatEventReq, body);
        _sender.Broadcast(packet);
    }
}