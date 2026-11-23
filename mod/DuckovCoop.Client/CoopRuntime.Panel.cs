using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using DuckovCoop.Client;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace DuckovCoopClient
{
    public sealed partial class CoopRuntime : MonoBehaviour
    {
        private bool _panelVisible = true;
        private GUIStyle _style;
        private GUIStyle _small;
        private GUIStyle _hudRight;
        private void DrawReloadBar()
        {
            if (!AmmoReloadActive || _localDead)
                return;
            var progress = AmmoReloadProgress;
            var width = Mathf.Min(360f, Screen.width * 0.35f);
            var rect = new Rect((Screen.width - width) * 0.5f, Screen.height - 110f,
                                width, 16f);
            GUI.Box(new Rect(rect.x - 3f, rect.y - 3f, rect.width + 6f, rect.height + 6f),
                    GUIContent.none);
            var oldColor = GUI.color;
            GUI.color = new Color(0.95f, 0.75f, 0.25f, 0.95f);
            GUI.DrawTexture(new Rect(rect.x, rect.y, rect.width * progress, rect.height),
                            Texture2D.whiteTexture);
            GUI.color = oldColor;
            GUI.Label(new Rect(rect.x, rect.y - 24f, rect.width, 22f),
                      $"RELOADING {progress * 100f:F0}%", _small);
        }

        private void OnGUI()
        {
            if (_style == null)
            {
                _style = new GUIStyle(GUI.skin.label)
                {
                    fontSize = Mathf.Clamp(Screen.height / 48, 18, 34),
                    fontStyle = FontStyle.Bold,
                };
                _small = new GUIStyle(_style) { fontSize = Mathf.Max(14, _style.fontSize - 6) };
                _hudRight = new GUIStyle(_style) { alignment = TextAnchor.UpperRight };
            }

            var h = _style.fontSize + 8;
            const float y = 14f;
            const float panelWidth = 560f;
            var panelX = Mathf.Max(14f, Screen.width - panelWidth - 14f);


            if (_panelVisible)
            {
                if (_client != null && _client.State == ClientState.InGame && !IsLocalHost())
                {
                    DrawPermissionPanel(panelX, y, panelWidth, h);
                    return;
                }

                if (_client != null)
                {
                    var knownPlayers = GetKnownPlayerIds();
                    var hostId = HostPlayerId();
                    var allReady = AllNonHostReady();
                    var raidPendingOrActive = _raidSpawnAllowed || _inRaidScene;
                    // Connecting before a save is loaded is allowed (the lobby is useful),
                    // but starting/readying from the menu would make the party gate wait for
                    // a character that does not exist. Gate those two actions on a loaded
                    // save character; loading the save clears the gate automatically.
                    var hasCharacter = LocalMainCharacter() != null;
                    var canStart = !raidPendingOrActive && allReady && hasCharacter;

                    GUI.Box(new Rect(panelX, y, panelWidth, 400), GUIContent.none);
                    GUI.Label(new Rect(panelX + 12, y + 8, panelWidth - 24, h), "DuckovCoop 控制台", _style);

                    var snapshot = _client.LatestSnapshot;
                    GUI.Label(new Rect(panelX + 12, y + 46, panelWidth - 24, h),
                        $"玩家 {snapshot?.Players.Length ?? 0}    你是 #{_client.PlayerId}    阶段 {_client.RaidPhase}", _small);

                    GUI.Label(new Rect(panelX + 12, y + 78, 50, h), "场景", _small);

                    GUI.enabled = _sceneChoices.Count > 0;
                    if (GUI.Button(new Rect(panelX + 68, y + 78, 36, h + 2), "<"))
                        _sceneIndex = (_sceneIndex - 1 + _sceneChoices.Count) % _sceneChoices.Count;
                    GUI.enabled = true;

                    GUI.Label(new Rect(panelX + 112, y + 78, 330, h),
                        _sceneChoices.Count > 0 ? _sceneChoices[_sceneIndex].Label : "-", _small);

                    GUI.enabled = _sceneChoices.Count > 0;
                    if (GUI.Button(new Rect(panelX + 448, y + 78, 36, h + 2), ">"))
                        _sceneIndex = (_sceneIndex + 1) % _sceneChoices.Count;
                    GUI.enabled = true;

                    GUI.enabled = canStart;
                    if (GUI.Button(new Rect(panelX + 12, y + 120, 160, h + 6), "开始游戏"))
                    {
                        if (_sceneChoices.Count > 0)
                        {
                            CleanupRaidState(); // pre-start: no leftovers from the last raid
                            _client.SendStartRaid((uint)_sceneIndex, _sceneChoices[_sceneIndex].Id);
                        }
                    }
                    GUI.enabled = true;

                    GUI.enabled = raidPendingOrActive;
                    if (GUI.Button(new Rect(panelX + 182, y + 120, 160, h + 6), "强制重置"))
                    {
                        _client.SendForceResetRaid();
                        _interaction = "已请求强制重置";
                    }
                    GUI.enabled = true;

                    if (GUI.Button(new Rect(panelX + 352, y + 120, 120, h + 6), "断开"))
                        Disconnect();

                    var nonHostCount = 0;
                    var readyCount = 0;
                    foreach (var playerId in knownPlayers)
                    {
                        if (playerId == hostId)
                            continue;
                        nonHostCount++;
                        if (_client.PlayerReady.TryGetValue(playerId, out var ready) && ready)
                            readyCount++;
                    }
                    var startHint = !hasCharacter
                        ? "尚未进入存档：请先读取/新建存档进入基地"
                        : raidPendingOrActive
                            ? "本局进行中：撤离或强制重置后可再开"
                            : allReady
                                ? "所有非 host 已准备，可以开始"
                                : $"等待其他玩家准备（{readyCount}/{nonHostCount}）";
                    GUI.Label(new Rect(panelX + 12, y + 156, panelWidth - 30, h), startHint, _small);

                    GUI.Label(new Rect(panelX + 12, y + 190, panelWidth - 30, h), "玩家", _small);

                    var rowY = y + 218f;
                    foreach (var playerId in knownPlayers)
                    {
                        var isHost = playerId == hostId;
                        var isLocal = playerId == _client.PlayerId;
                        string state;
                        if (isHost)
                        {
                            state = "host";
                        }
                        else
                        {
                            _client.PlayerReady.TryGetValue(playerId, out var ready);
                            state = ready ? "已准备" : "未准备";
                        }

                        GUI.Label(new Rect(panelX + 20, rowY, panelWidth - 40, h),
                            $"#{playerId}{(isLocal ? "（你）" : string.Empty)}    {state}", _small);
                        rowY += 24f;
                        if (rowY > y + 330f)
                            break;
                    }

                    if (GUI.Button(new Rect(panelX + 12, y + 352, 160, h + 6), "关闭 (F1)"))
                        CloseConsolePanel();

                    DrawConsoleWindow(panelX, y, panelWidth, h);
                    return;
                }

                GUI.Box(new Rect(panelX, y, panelWidth, 280), GUIContent.none);
                GUI.Label(new Rect(panelX + 12, y + 8, panelWidth - 24, h), "DuckovCoop Connection", _style);

                GUI.Label(new Rect(panelX + 12, y + 46, 60, h), "Host", _small);
                _host = GUI.TextField(new Rect(panelX + 84, y + 46, 190, h), _host);

                GUI.Label(new Rect(panelX + 286, y + 46, 40, h), "Port", _small);
                _portText = GUI.TextField(new Rect(panelX + 332, y + 46, 80, h), _portText, 5);

                GUI.Label(new Rect(panelX + 12, y + 86, 60, h), "Name", _small);
                _name = GUI.TextField(new Rect(panelX + 84, y + 86, 328, h), _name);

                if (GUI.Button(new Rect(panelX + 12, y + 132, 130, h + 6), "Connect"))
                    Connect();

                if (GUI.Button(new Rect(panelX + 150, y + 132, 130, h + 6), "Disconnect"))
                    Disconnect();

                if (GUI.Button(new Rect(panelX + 288, y + 132, 130, h + 6), "Reset"))
                    ResetConfig();

                GUI.Label(new Rect(panelX + 12, y + 178, panelWidth - 24, h), _status, _small);
                GUI.Label(new Rect(panelX + 12, y + 210, panelWidth - 24, h),
                    "In game: F1 host controls, F7 ready (non-host), F6 mark, F4 debug extract", _small);
                GUI.Label(new Rect(panelX + 12, y + 242, panelWidth - 24, h),
                    $"client {ClientVersionText}", _small);
                DrawConsoleWindow(panelX, y, panelWidth, h);
                return;
            }

            var playersOnline = _client?.LatestSnapshot?.Players.Length ?? 0;
            var stateText = _client == null
                ? "离线"
                : _client.State == ClientState.InGame ? "已连接" : _client.State.ToString();
            var readyText = string.Empty;
            if (_client != null && _client.State == ClientState.InGame)
            {
                var hostId = HostPlayerId();
                var ready = 0;
                var total = 0;
                foreach (var playerId in GetKnownPlayerIds())
                {
                    if (playerId == hostId)
                        continue;
                    total++;
                    if (_client.PlayerReady.TryGetValue(playerId, out var value) && value)
                        ready++;
                }
                readyText = $"  准备 {ready}/{total}";
            }
            GUI.Label(new Rect(0, y, Screen.width - 14, h),
                $"[DuckovCoop] {stateText}  玩家 {playersOnline}{readyText}", _hudRight);
            GUI.Label(new Rect(0, y + h, Screen.width - 14, h),
                (_inRaidScene || _raidSpawnAllowed) ? "F1 菜单 | 局内不可准备" : "F1 菜单 | F7 准备",
                _hudRight);

            if (!string.IsNullOrEmpty(_interaction))
                GUI.Label(new Rect(0, y + h * 2, Screen.width - 14, h), _interaction, _hudRight);

            if (_showInventory && _client != null)
            {
                var inventory = _client.GetContainerView((uint)(1000 + _client.PlayerId));
                var rows = inventory?.Entries.Length ?? 0;
                const float windowWidth = 460f;
                var windowX = Screen.width - windowWidth - 14f;
                var windowTop = y + h * 3 + 10f;
                var windowHeight = 96f + rows * (h + 4);

                GUI.Box(new Rect(windowX, windowTop, windowWidth, windowHeight), GUIContent.none);
                GUI.Label(new Rect(windowX + 12, windowTop + 8, windowWidth - 24, h),
                    $"Backpack   items={rows}", _style);

                if (inventory != null)
                {
                    for (var i = 0; i < rows; i++)
                    {
                        var entry = inventory.Entries[i];
                        GUI.Label(new Rect(windowX + 12, windowTop + 46 + i * (h + 4), windowWidth - 24, h),
                            $"[{entry.Slot}]  item {entry.ItemId}  x{entry.Count}", _small);
                    }
                }

                GUI.Label(new Rect(windowX + 12, windowTop + windowHeight - 30, windowWidth - 24, h),
                    "I closes", _small);
            }

            if (_showContainer && _client != null)
            {
                _client.ContainerMirror.TryGet(_activeContainerId, out var view);
                var rows = view?.Entries.Count ?? 0;
                const float windowWidth = 460f;
                var windowX = Screen.width - windowWidth - 14f;
                var windowTop = y + h * 2 + 10f;
                var windowHeight = 96f + rows * (h + 4);

                GUI.Box(new Rect(windowX, windowTop, windowWidth, windowHeight), GUIContent.none);
                GUI.Label(new Rect(windowX + 12, windowTop + 8, windowWidth - 24, h),
                    $"Container {_activeContainerId}   items={rows}", _style);

                if (view != null)
                {
                    for (var i = 0; i < rows; i++)
                    {
                        var entry = view.Entries[i];
                        var rowY = windowTop + 46 + i * (h + 4);
                        GUI.Label(new Rect(windowX + 12, rowY, windowWidth - 130, h),
                            $"[{entry.Slot}]  item {entry.ItemId}  x{entry.Count}", _small);

                        if (GUI.Button(new Rect(windowX + windowWidth - 110, rowY, 98, h + 2), "Take"))
                        {
                            var targetSlot = _client.InventoryMirror.FindFirstFreeSlot(1);
                            if (targetSlot < 0)
                            {
                                _interaction = "inventory: backpack full";
                            }
                            else
                            {
                                var sent = _client.SendItemTransfer(++_correlation,
                                    ContainerMirrorState.MapContainerKind, _activeContainerId,
                                    entry.Slot, 1, _client.PlayerId, (ushort)targetSlot,
                                    entry.ItemUid, entry.Count, entry.ItemId, view.Revision);
                                if (sent)
                                {
                                    _interaction = $"take item={entry.ItemId} x{entry.Count}";
                                    _containerViewRefreshAt = Time.unscaledTime + 0.4f;
                                }
                            }
                        }
                    }
                }

                GUI.Label(new Rect(windowX + 12, windowTop + windowHeight - 30, windowWidth - 24, h),
                    "E / F2 closes", _small);
            }
        }


        private void DrawPermissionPanel(float panelX, float y, float panelWidth, float h)
        {
            GUI.Box(new Rect(panelX, y, panelWidth, 164), GUIContent.none);
            GUI.Label(new Rect(panelX + 12, y + 8, panelWidth - 24, h), "DuckovCoop", _style);
            GUI.Label(new Rect(panelX + 12, y + 46, panelWidth - 24, h), "无相关权限", _style);
            GUI.Label(new Rect(panelX + 12, y + 80, panelWidth - 24, h),
                "只有 host 可以开始游戏或强制重置。", _small);
            _client.PlayerReady.TryGetValue(_client.PlayerId, out var ready);
            var inRaid = _inRaidScene || _raidSpawnAllowed;
            GUI.Label(new Rect(panelX + 12, y + 106, panelWidth - 24, h),
                inRaid
                    ? "你的准备状态：局内不可切换"
                    : $"你的准备状态：{(ready ? "已准备" : "未准备")}（F7 切换）", _small);
            if (GUI.Button(new Rect(panelX + 12, y + 128, 140, h + 4), "关闭 (F1)"))
                CloseConsolePanel();
        }

        /// <summary>
        /// Smallest joined player id. The server elects its host from the same rule, so use
        /// its snapshot roster as the source of truth: state samples may belong to players
        /// that already left and must not keep a departed host alive in this UI.
        /// </summary>
        private ushort HostPlayerId()
        {
            if (_client == null || _client.PlayerId == 0)
                return 0;

            var min = ushort.MaxValue;
            var snapshot = _client.LatestSnapshot;
            if (snapshot?.Players != null && snapshot.Players.Length > 0)
            {
                foreach (var player in snapshot.Players)
                    if (player.PlayerId != 0 && player.PlayerId < min)
                        min = player.PlayerId;
            }
            else
            {
                foreach (var playerId in _client.PlayerStateSamples.Keys)
                    if (playerId != 0 && playerId < min)
                        min = playerId;
            }

            // The snapshot can lag the join by one tick; the local player is already joined.
            if (_client.PlayerId != 0 && _client.PlayerId < min)
                min = _client.PlayerId;

            if (min != ushort.MaxValue)
                return min;

            // Before the first snapshot the server has already assigned ids in join order.
            return _client.PlayerId == 1 ? (ushort)1 : (ushort)0;
        }

        private bool IsLocalHost()
        {
            if (_client == null || _client.PlayerId == 0)
                return false;
            var hostId = HostPlayerId();
            return hostId != 0 && _client.PlayerId == hostId;
        }

        private List<ushort> GetKnownPlayerIds()
        {
            var ids = new HashSet<ushort>();
            var snapshot = _client?.LatestSnapshot;
            if (snapshot?.Players != null && snapshot.Players.Length > 0)
            {
                // Authoritative roster: departed players are gone from the snapshot, so a
                // stale ready flag or state sample cannot block the next raid start.
                foreach (var player in snapshot.Players)
                    if (player.PlayerId != 0)
                        ids.Add(player.PlayerId);
                if (_client != null && _client.PlayerId != 0)
                    ids.Add(_client.PlayerId);
            }
            else if (_client != null)
            {
                foreach (var playerId in _client.PlayerStateSamples.Keys)
                    if (playerId != 0)
                        ids.Add(playerId);
                if (_client.PlayerId != 0)
                    ids.Add(_client.PlayerId);
            }

            var result = ids.ToList();
            result.Sort();
            return result;
        }

        private bool AllNonHostReady()
        {
            var hostId = HostPlayerId();
            if (hostId == 0)
                return false;

            foreach (var playerId in GetKnownPlayerIds())
            {
                if (playerId == hostId)
                    continue;
                if (!_client.PlayerReady.TryGetValue(playerId, out var ready) || !ready)
                    return false;
            }
            return true;
        }

        private void ToggleLocalReady()
        {
            if (_client == null || _client.State != ClientState.InGame)
                return;
            if (_inRaidScene || _raidSpawnAllowed)
            {
                _interaction = "局内不能切换准备状态";
                return;
            }
            if (IsLocalHost())
            {
                _interaction = "host 无需准备";
                return;
            }
            if (LocalMainCharacter() == null)
            {
                // Lobby-only connection: do not let a menu client block the host's start gate.
                _interaction = "请先读取存档/进入基地，再准备";
                return;
            }

            _client.PlayerReady.TryGetValue(_client.PlayerId, out var ready);
            var next = !ready;
            if (_client.SendPlayerReady(next))
                _interaction = next ? "已准备，等待 host 开始" : "已取消准备";
        }

        private void HandlePanelInput()
        {
            if (UnityEngine.Input.GetKeyDown(KeyCode.F1))
                ToggleConsolePanel();
            if (!_consoleInputBlocked && UnityEngine.Input.GetKeyDown(KeyCode.F7))
                ToggleLocalReady();
            if (!_consoleInputBlocked && UnityEngine.Input.GetKeyDown(KeyCode.F4))
                DebugRequestExtract();
        }
    }
}
