using TMPro;
using UnityEngine;
using UnityTcp;

/// <summary>
/// 서버의 플레이어 연결 상태를 UI 텍스트 색상으로 표시하는 모니터.
///
/// 역할:
/// - 서버가 관리 중인 Player ID 연결 상태 조회
/// - 상태가 바뀌었을 때만 UI 갱신
/// </summary>
public class ServerPlayerConnectionMonitor : MonoBehaviour
{
    [Header("Server")]
    [SerializeField] private UnityTcpServer server;

    [Header("Player Texts (0~9 순서)")]
    [SerializeField] private TMP_Text[] playerTexts;

    [Header("Colors")]
    [SerializeField] private Color connectedColor = Color.white;
    [SerializeField] private Color disconnectedColor = Color.gray;

    // 이전 프레임 상태를 저장해 변경 여부 비교에 사용
    private bool[] _lastStates;

    private void Start()
    {
        _lastStates = new bool[NetConst.MaxUserId + 1];
        RefreshNow(true);
    }

    private void Update()
    {
        RefreshNow(false);
    }

    /// <summary>
    /// 서버 상태를 읽어서 변경이 있을 때만 UI 갱신
    /// </summary>
    private void RefreshNow(bool force)
    {
        if (server == null)
            return;

        bool[] states = server.GetConnectedPlayerStates();
        if (states == null || states.Length == 0)
            return;

        bool changed = force || HasStateChanged(states);

        if (!changed)
            return;

        SaveStates(states);
        UpdateTexts(states);
    }

    /// <summary>
    /// 현재 연결 상태를 텍스트/색상에 반영
    /// </summary>
    private void UpdateTexts(bool[] states)
    {
        if (playerTexts == null)
            return;

        for (int i = 0; i < states.Length && i < playerTexts.Length; i++)
        {
            TMP_Text txt = playerTexts[i];
            if (txt == null)
                continue;

            bool connected = states[i];

            txt.text = $"Player {i + 1}";
            txt.color = connected ? connectedColor : disconnectedColor;
        }
    }

    /// <summary>
    /// 이전 상태와 비교해 변경이 있는지 확인
    /// </summary>
    private bool HasStateChanged(bool[] newStates)
    {
        if (_lastStates == null || _lastStates.Length != newStates.Length)
            return true;

        for (int i = 0; i < newStates.Length; i++)
        {
            if (_lastStates[i] != newStates[i])
                return true;
        }

        return false;
    }

    /// <summary>
    /// 현재 상태를 다음 비교를 위해 저장
    /// </summary>
    private void SaveStates(bool[] states)
    {
        if (_lastStates == null || _lastStates.Length != states.Length)
            _lastStates = new bool[states.Length];

        for (int i = 0; i < states.Length; i++)
            _lastStates[i] = states[i];
    }
}