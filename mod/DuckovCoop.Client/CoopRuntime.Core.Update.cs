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
        private float _nextHeartbeat;

        private void UpdateHeartbeat()
        {
            if (Time.unscaledTime < _nextHeartbeat)
                return;
            _nextHeartbeat = Time.unscaledTime + 2f;

            var states = _client != null ? _client.AiStates.Count : 0;
            var missing = 0;
            if (_client != null)
            {
                foreach (var pair in _aiGhosts)
                    if (!_client.AiStates.ContainsKey(pair.Key))
                        missing++;
            }

            Log($"[hb] frameMs={Time.unscaledDeltaTime * 1000f:F1} ghosts={_aiGhosts.Count} " +
                $"states={states} miss={missing} realAi={_realAi.Count} scene={SceneManager.GetActiveScene().name}");
        }

        private void Update()
        {
            EnsureFriendlyHealthBars();

            UpdateHeartbeat();

            UpdateContentDump();

            UpdatePerkMax();

            UpdateCombatD5();

            UpdateSceneObjects();

            UpdateImpactFx();

            UpdateV5();

            UpdateAiAnimSampling();

            UpdateAiV7Sampling();

            UpdatePlayerV8Sampling();

            UpdateWorldGating();

            UpdateSpawnEvents();

            HandlePanelInput();

            UpdateSceneProbe();

            UpdatePlaySceneProbe();

            UpdateMapVariant();

            UpdateLocationMark();

            UpdateGateAProbe();

            SyncConsolePanelState();

            if (_client == null)
            {
                ClearRemotePlayers("no client");
                ClearWorldItemVisuals("no client");
                ClearInventoryMirror("no client");
                return;
            }

            if (ConsoleNetDropActive())
                return;

            UpdateInput();

            try
            {
                _client.Tick();
            }
            catch (Exception ex)
            {
                _status = "tick failed: " + ex.Message;
                Log("tick failed: " + ex);
                _client.Dispose();
                _client = null;
                _loginSince = 0f;
                _panelVisible = true;
                ClearRemotePlayers("tick failed");
                ClearWorldItemVisuals("tick failed");
                ClearInventoryMirror("tick failed");
                return;
            }

            if (_client.State == ClientState.Failed)
            {
                var error = _client.LastError;
                var loginCode = _client.LastLoginCode;
                if (loginCode == (byte)ErrorCode.SessionReplaced)
                {
                    _status = "账号已在别处登录（code=9）";
                    Log("session replaced by another login (code=9); no auto reconnect");
                }
                else if (loginCode == (byte)ErrorCode.VersionMismatch)
                {
                    _status = $"版本不匹配：本端 {ClientVersionText}，服务端拒绝（code=3）";
                    Log($"version mismatch: local={ClientVersionText} code=3");
                }
                else if (loginCode == (byte)ErrorCode.ContentMismatch)
                {
                    _status = "内容版本不一致：monster catalog hash 不匹配（code=10）";
                    Log("content mismatch: monster catalog hash rejected by server (code=10)");
                }
                else
                {
                    _status = "failed: " + error;
                    Log("login failed: " + error);
                }

                // N2/A1: surface the reason and stop; never auto reconnect.
                _panelVisible = true;
                _raidSpawnAllowed = false;
                _inRaidScene = false;
                _resumePendingTeleport = false;
                ClearRemotePlayers("login failed");
                ClearWorldItemVisuals("login failed");
                ClearInventoryMirror("login failed");
                _client.Dispose();
                _client = null;
                return;
            }

            if (_client.State == ClientState.LoggingIn || _client.State == ClientState.Joining)
            {
                if (_loginSince <= 0f)
                    _loginSince = Time.unscaledTime;
                else if (Time.unscaledTime - _loginSince > LoginTimeoutSeconds)
                {
                    Log($"login timeout: state={_client.State} elapsed={Time.unscaledTime - _loginSince:F1}s; " +
                        "recreating client socket");
                    Connect(showPanel: true);
                    if (_client != null)
                        _status = "login timeout - reconnecting...";
                    return;
                }
            }
            else
            {
                _loginSince = 0f;
            }

            ProcessPlayerEvents();
            UpdateHostWatch();

            ProcessRaidControl();

            if (_client.State != ClientState.InGame)
            {
                ClearRemotePlayers("state " + _client.State);
                ClearWorldItemVisuals("state " + _client.State);
                ClearInventoryMirror("state " + _client.State);
                return;
            }

            UpdateMirror();
            UpdateLoadout();
            CheckLoadoutResult();
            UpdateAiTargets();

            HandleSceneLoadRequests();

            UpdateViews();
            UpdateContainerViews();

            UpdateWorldItems();
            UpdateCorpses();

            UpdateInventoryBridge();
            UpdateItemDetails();
            UpdateAmmoReload();
            EnforceServerMagazine(Time.unscaledTime);
            UpdateRespawn();
            UpdateQuickBarClear();

            UpdateTeleport();

            UpdateNativeAi();

            UpdateLocalHealth();

            UpdateAiGhosts();

            if (_client.State == ClientState.InGame && Time.unscaledTime >= _nextSend)
            {
                _nextSend = Time.unscaledTime + SendInterval;
                SendLocalInput();
            }

            UpdateSpawnAnchor();
            UpdateLoadGate();

            var snapshot = _client.LatestSnapshot;
            var local = CharacterMainControl.Main;
            var localPos = local != null
                ? $"{local.transform.position.x:F1},{local.transform.position.y:F1},{local.transform.position.z:F1}"
                : "null";
            var selfPos = "na";
            if (snapshot != null)
            {
                for (var i = 0; i < snapshot.Players.Length; i++)
                {
                    var p = snapshot.Players[i];
                    if (p.PlayerId != _client.PlayerId)
                        continue;
                    selfPos = $"{p.X / 100f:F1},{p.Y / 100f:F1},{p.Z / 100f:F1}";
                    break;
                }
            }
            _status = $"state={_client.State} id={_client.PlayerId} players={snapshot?.Players.Length ?? 0} " +
                      $"snapshots={_client.SnapshotsReceived} sent={_sentInputs} seq={_seq} " +
                      $"local={localPos} snapSelf={selfPos} phase={_client.RaidPhase}";

            if (_client.State != _lastState)
            {
                _lastState = _client.State;
                Log($"state -> {_client.State} id={_client.PlayerId}");
                if (_client.State == ClientState.InGame)
                {
                    SaveSessionToken();
                    // The host opens controls with F1; non-hosts get the permission panel.
                    // Starting hidden makes the first F1 press meaningful for both roles.
                    _panelVisible = false;
                    _consoleActivated = false;
                    SetConsoleInputBlocked(false);
                }
            }

            if (_client.RaidPhase != _lastRaidPhase)
            {
                _lastRaidPhase = _client.RaidPhase;
                Log($"raid phase -> {_client.RaidPhase}");
                if (_client.RaidPhase == 3)
                    CleanupRaidState();
            }

            if (_client.ResumeCount != _lastResumeCount)
            {
                _lastResumeCount = _client.ResumeCount;
                ClearRemotePlayers("resume");
                var resume = _client.Resume;
                _raidEpoch = resume.RaidEpoch;
                Log($"session resumed: id={resume.PlayerId} scene={resume.SceneName} epoch={resume.RaidEpoch}");

                if (!string.IsNullOrEmpty(resume.SceneName))
                {
                    _resumeTarget = new Vector3(resume.X / 100f, resume.Y / 100f, resume.Z / 100f);
                    _raidSpawnAllowed = true;
                    _localDead = false;
                    _leaveSent = false;

                    var resumeScene = NormalizeRaidScene(resume.SceneName);
                    var active = SceneManager.GetActiveScene().name;
                    Log($"resume target: {_resumeTarget} scene={resumeScene}");
                    if (!string.IsNullOrEmpty(resumeScene) && active != resumeScene)
                    {
                        _resumePendingTeleport = true;
                        ExtendNativeLoadingCurtain();
                        TryLoadSceneViaGame(resumeScene);
                    }
                    else
                    {
                        _teleportTarget = _resumeTarget;
                        _teleportUntil = Time.unscaledTime + 3f;
                        _spawnReported = false;

                        if (active.StartsWith("Level_"))
                        {
                            _inRaidScene = true;
                            _raidSceneName = active;
                        }
                    }
                }
                else
                {
                    // the session itself is restored, but the player is not in a raid:
                    // lobby resume carries (0,0,0); never use it as a teleport target.
                    _resumeTarget = Vector3.zero;
                    _teleportTarget = Vector3.zero;
                    _teleportUntil = 0f;
                    _spawnReported = false;
                    _raidSpawnAllowed = false;
                    _resumePendingTeleport = false;
                    Log("session resumed in the lobby (no raid teleport; position ignored)");
                }
            }

            // BUG-004: rebuild the remote view from the current snapshot after resume.
            UpdateRemotePlayers(_client.LatestSnapshot);

            if (!_loggedFirstSnapshot && _client.SnapshotsReceived > 0)
            {
                _loggedFirstSnapshot = true;
                Log($"first snapshot: players={snapshot?.Players.Length ?? 0} tick={snapshot?.Tick ?? 0}");
            }

            UpdateResult();

            UpdateEvents();

            ProcessExtractResult();
            UpdateExtractReturn();

            if (Time.unscaledTime >= _nextStatusLog)
            {
                _nextStatusLog = Time.unscaledTime + 10f;
                Log(_status);

                if (_remotes.Count > 0 && CharacterMainControl.Main != null)
                {
                    foreach (var pair in _remotes)
                    {
                        if (pair.Value == null)
                            continue;
                        var distance = Vector3.Distance(pair.Value.transform.position, CharacterMainControl.Main.transform.position);
                        var renderers = pair.Value.GetComponentsInChildren<Renderer>(true);
                        Log($"remote id={pair.Key} pos={pair.Value.transform.position} dist={distance:F2} " +
                            $"yawRaw={_lastRemoteYaw} rotY={pair.Value.transform.eulerAngles.y:F1} renderers={renderers.Length} " +
                            $"enabled={System.Linq.Enumerable.Count(renderers, r => r.enabled && r.gameObject.activeInHierarchy)}");                    }
                }
            }
        }

        private void ProcessRaidControl()
        {
            if (_client == null)
                return;

            if (_client.RaidControlResultCount != _lastRaidControlResultCount)
            {
                _lastRaidControlResultCount = _client.RaidControlResultCount;
                var result = _client.LastRaidControlResult;
                var action = result.Action == (byte)RaidControlAction.StartRaid
                    ? "开始游戏"
                    : "强制重置";
                if (result.Code == (byte)ErrorCode.Ok)
                {
                    _interaction = $"{action} 已受理";
                    Log($"[control] {action} accepted by player={result.PlayerId}");
                }
                else
                {
                    var reason = ErrorCodeText(result.Code);
                    _interaction = $"{action}被拒绝：{reason}";
                    Log($"[control] {action} rejected: code={result.Code} ({reason}) player={result.PlayerId}");
                }
            }

            if (_client.ForceResetCount != _lastForceResetCount)
            {
                _lastForceResetCount = _client.ForceResetCount;
                var reset = _client.LastForceReset;
                Log($"[control] force reset by host player={reset.ByPlayerId}");
                _interaction = "host 强制重置：正在返回基地";
                _panelVisible = false;
                BeginForcedBaseReturn("force reset");
            }
        }

        private static string ErrorCodeText(byte code)
        {
            switch ((ErrorCode)code)
            {
            case ErrorCode.Ok: return "ok";
            case ErrorCode.InvalidArgument: return "参数无效";
            case ErrorCode.Unauthorized: return "无权限";
            case ErrorCode.VersionMismatch: return "版本不匹配";
            case ErrorCode.RoomFull: return "房间已满";
            case ErrorCode.NotInRaid: return "不在局内";
            case ErrorCode.Conflict: return "状态冲突";
            case ErrorCode.IdempotentReplay: return "重复请求";
            case ErrorCode.ServerBusy: return "服务器繁忙";
            case ErrorCode.SessionReplaced: return "会话被替换";
            case ErrorCode.ContentMismatch: return "内容版本不一致";
            default: return "code " + code;
            }
        }

        // BUG-004: consume SPlayerEvent(left) even when the local client is not InGame
        // (link loss / rejoining); otherwise a remote clone outlives its player.
        private void ProcessPlayerEvents()
        {
            if (_client == null || _client.PlayerEventCount == _lastPlayerEventCount ||
                _client.LastPlayerEvent == null)
                return;

            _lastPlayerEventCount = _client.PlayerEventCount;
            var playerEvent = _client.LastPlayerEvent;
            Log($"player event: id={playerEvent.PlayerId} type={playerEvent.Type}");

            if (playerEvent.PlayerId == _client.PlayerId)
            {
                if (playerEvent.Type == 2 || playerEvent.Type == 3)
                {
                    _raidSpawnAllowed = false;
                    Log("local player is out of the raid");
                }
            }
            else
            {
                // `left` is permanent. Drop the per-player caches too, otherwise a departed
                // host stays the smallest id in PlayerStateSamples and the UI never elects a
                // successor (and a stale non-host ready flag can block the next start).
                if (playerEvent.Type == 1)
                    ForgetRemotePlayerState(playerEvent.PlayerId);
                RemoveRemotePlayer(playerEvent.PlayerId);
            }
        }

        /// <summary>
        /// The server elects the host as the smallest joined player id. Track that roster and
        /// surface a migration to the player, so a host crash is not a silent dead-end.
        /// </summary>
        private void UpdateHostWatch()
        {
            if (_client == null || _client.State != ClientState.InGame)
            {
                _lastHostId = 0;
                return;
            }

            var hostId = HostPlayerId();
            if (hostId == 0)
                return;

            if (_lastHostId == 0)
            {
                _lastHostId = hostId;
                Log($"[host] current host={hostId} local={_client.PlayerId}");
                return;
            }

            if (hostId == _lastHostId)
                return;

            var previous = _lastHostId;
            _lastHostId = hostId;
            Log($"[host] failover: {previous} -> {hostId} local={_client.PlayerId}");
            _interaction = hostId == _client.PlayerId
                ? "原 host 已离开，你现在是 host（F1 可开始/重置）"
                : $"host 已切换为玩家 {hostId}";
        }

        /// <summary>Drop every cached view of a player that left the session for good.</summary>
        private void ForgetRemotePlayerState(ushort playerId)
        {
            var client = _client;
            if (client == null || playerId == 0)
                return;

            client.PlayerStateSamples.Remove(playerId);
            client.PlayerReady.Remove(playerId);
            client.Loadouts.Remove(playerId);
            client.PlayerFaces.Remove(playerId);
            client.PlayerActiveSlots.Remove(playerId);
            client.PlayerShots.Remove(playerId);
            foreach (var key in client.PlayerLoadoutsV8.Keys.Where(k => (ushort)(k >> 8) == playerId).ToList())
                client.PlayerLoadoutsV8.Remove(key);
            foreach (var key in client.PlayerBuffs.Keys.Where(k => (ushort)(k >> 32) == playerId).ToList())
                client.PlayerBuffs.Remove(key);
        }

    }
}
