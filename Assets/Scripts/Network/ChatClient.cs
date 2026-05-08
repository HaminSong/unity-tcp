using TMPro;
using UnityEditor.PackageManager;
using UnityEngine;
using UnityEngine.UI;
using UnityTcp;
using UnityTcpEvent;

/// <summary>
/// 간단하게 메시지를 주고 받는 클라이언트 예시 코드
/// </summary>
public class ChatClient : MonoBehaviour
{
    [SerializeField] private UnityTcpClient client;

    [Header("Chat UI")]
    [SerializeField] private TMP_InputField inputField;
    [SerializeField] private TMP_Text message;
    [SerializeField] private Button sendButton;

    private ClientPacketSender _sender;

    private void Awake()
    {
        _sender = new ClientPacketSender(client);

        // 클라이언트가 dispatcher를 직접 몰라도 됨
        // 핸들러를 통해 route 등록 요청만 함
        client.AddRoute(Msg.Chat, Sub.ChatEventReq, HandleChatEventReq);
    }
    private void OnEnable()
    {
        if (sendButton != null)
            sendButton.onClick.AddListener(SendChat);
    }
    private void OnDisable()
    {
        if (sendButton != null)
            sendButton.onClick.RemoveListener(SendChat);
    }

    public void SendChat()
    {
        ChatEventReq body = new ChatEventReq
        {
            UserId = (ushort)Mathf.Max(0, client.AssignedUserId),
            Message = inputField.text
        };

        byte[] packet = PacketBuilder.Build(Msg.Chat, Sub.ChatEventReq, body);
        _sender.Send(packet);
    }

    private void HandleChatEventReq(byte[] packet)
    {
        var chat = MarshalUtil.BytesToStruct<ChatEventReq>(packet, NetConst.HeaderSize);

        client.EnqueueMain(() =>
        {
            message.SetText($"{(chat.UserId < 999 ? ("Player" + (chat.UserId + 1)) : "Server")}: {chat.Message}");
        });

        Debug.Log($"[ChatClient] userId={chat.UserId}, message={chat.Message}");
    }
}