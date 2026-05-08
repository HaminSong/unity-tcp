using UnityEngine;
using UnityEngine.UI;
using TMPro;

/// <summary>
/// 플레이어 슬롯 버튼 UI 제어 컴포넌트.
///
/// 역할:
/// - 서버가 알려준 사용 가능 슬롯 상태를 버튼에 반영
/// - 버튼 클릭 시 원하는 Player ID 선택 요청
/// - 연결 상태 텍스트 표시
/// </summary>
public class PlayerSlotButtonUI : MonoBehaviour
{
    [Header("Network")]
    [SerializeField] private UnityTcpClient client;

    [Header("Connection UI")]
    [SerializeField] private TMP_Text connectionText;
    [SerializeField] private TMP_Text myPlayerText;
    [SerializeField] private GameObject sendingUI;

    [Header("Buttons 부모 오브젝트")]
    [SerializeField] private GameObject buttonRoot;

    [Header("Buttons (0~9 순서대로)")]
    [SerializeField] private Button[] playerButtons;

    [Header("Optional Labels")]
    [SerializeField] private TMP_Text[] playerLabels;

    private void OnEnable()
    {
        // 네트워크 상태 변화 이벤트 구독
        if (client != null)
        {
            client.OnPlayerAvailabilityChanged += RefreshButtons;
            client.OnConnectionStateChanged += OnConnectionStateChanged;
        }
    }

    private void OnDisable()
    {
        // 비활성화 시 이벤트 구독 해제
        if (client != null)
        {
            client.OnPlayerAvailabilityChanged -= RefreshButtons;
            client.OnConnectionStateChanged -= OnConnectionStateChanged;
        }
    }

    private void Start()
    {
        BindButtonEvents();
        RefreshButtons();
        OnConnectionStateChanged(false);
    }

    /// <summary>
    /// 현재 연결 상태를 텍스트에 반영
    /// </summary>
    private void OnConnectionStateChanged(bool connected)
    {
        if (connectionText == null)
            return;

        connectionText.text = connected ? "Connected" : "Disconnected";
    }

    /// <summary>
    /// 각 버튼 클릭 시 해당 Player ID가 선택되도록 이벤트 연결
    /// </summary>
    private void BindButtonEvents()
    {
        if (playerButtons == null) return;

        for (int i = 0; i < playerButtons.Length; i++)
        {
            int capturedId = i;
            if (playerButtons[i] == null) continue;

            playerButtons[i].onClick.RemoveAllListeners();
            playerButtons[i].onClick.AddListener(() => OnClickPlayer(capturedId));
        }
    }

    /// <summary>
    /// 현재 사용 가능 상태와 내 선택 상태를 기준으로
    /// 버튼 활성화/비활성화 및 라벨 텍스트 갱신
    /// </summary>
    public void RefreshButtons()
    {
        if (client == null) return;

        // 내 플레이어 번호 표시
        if (myPlayerText != null)
        {
            if (client.AssignedUserId != -1)
                myPlayerText.text = $"Player Number : {client.AssignedUserId + 1}";
            else
                myPlayerText.text = "Player Number : -";
        }

        if (client.AssignedUserId != -1)
        {
            buttonRoot.SetActive(false);
            sendingUI.SetActive(true);
            return;
        }

        buttonRoot.SetActive(true);
        sendingUI.SetActive(false);

        if (playerButtons == null) return;

        for (int i = 0; i < playerButtons.Length; i++)
        {
            Button btn = playerButtons[i];
            if (btn == null) continue;

            bool available = client.IsPlayerAvailable(i);
            bool alreadyAssigned = (client.AssignedUserId == i);

            btn.interactable = available && !alreadyAssigned;
        }
    }

    /// <summary>
    /// 플레이어 슬롯 버튼 클릭 처리
    /// </summary>
    public void OnClickPlayer(int id)
    {
        if (client == null) return;
        if (!client.IsPlayerAvailable(id)) return;

        client.ChooseUserId(id);
        RefreshButtons();
    }
}