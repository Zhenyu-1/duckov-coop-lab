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
        private const float SendInterval = 1f / 20f;
        private const uint WorldContainerId = 1;

        // N2: version facts come from the C# protocol library (CORE); never hardcode
        // the contract string here. Protocol.ModClientVersion = "mod/" + ClientVersion.
        private static string ClientVersionText => Protocol.ModClientVersion;

        // BUG-007: a dead UDP socket can leave the client in LoggingIn forever.
        // After this timeout the mod recreates the client/socket and retries.
        private const float LoginTimeoutSeconds = 10f;
        private float _loginSince;

        private CoopClient _client;
        private string _host = "127.0.0.1";
        private string _portText = "9050";
        private string _name = "player";

        private string _status = "not connected";
        private string _interaction = string.Empty;
        private float _nextSend;
        private uint _seq;
        private uint _correlation;
        private int _sentInputs;
        private ClientState _lastState = ClientState.Idle;
        private float _nextStatusLog;
        private bool _loggedFirstSnapshot;
        private int _lastExtractCode = int.MinValue;
        private ulong _lastExtractCorrelation;
        private int _lastResumeCount;
        private byte _lastRaidPhase;
        private uint _raidEpoch;
        private bool _raidSpawnAllowed;
        private int _lastPlayerEventCount;
        private ushort _lastHostId;
        private int _lastRaidControlResultCount;
        private int _lastForceResetCount;

        private static string LogPath =>
            Path.Combine(Application.persistentDataPath, "duckov_coop_client.log");

        internal static void Log(string message)
        {
            try
            {
                File.AppendAllText(LogPath, $"[{DateTime.Now:HH:mm:ss.fff}] {message}{Environment.NewLine}", Encoding.UTF8);
            }
            catch
            {
                // never break gameplay because of logging
            }

            Debug.Log("[DuckovCoopClient] " + message);
        }

        private void Start()
        {
            LoadConfig();
            LoadSessionToken();
            LoadScenes();
            LoadMonsterCatalog();
            LoadBaseStatsHash();
            CombatCapture.AttachRuntime(this);
            CombatCapture.Transport = SendHitEventIfPlaying;
            Log($"client version: {ClientVersionText}");
            Log($"config: host={_host} port={_portText} name={_name} suppressSpawns={_suppressSpawns} yawOffset={_yawOffset}");
            Log($"[console] enabled={_consoleEnabled}");

            try
            {
                HarmonySpike.Init();
            }
            catch (Exception ex)
            {
                Log("[harmony] spike unavailable: " + ex.GetType().Name + ": " + ex.Message);
            }

            RaidFlowPatches.Init();

            AiReplicaGuard.Init();
            V5Patches.Init();
            SpawnGuard.Init();
            WorldItemPickupGuard.Init();
            InventoryBridgeGuard.Init();
            InventoryTransferGuard.Init();
            AmmoSwitchGuard.Init();
            ContainerUiGuard.Init();
            ProjectileSyncGuard.Init();
            ImpactFxGuard.Init();
            SubscribeV5Facts();
        }

        private void LoadConfig()
        {
            try
            {
                var directory = Path.GetDirectoryName(typeof(CoopRuntime).Assembly.Location);
                var path = Path.Combine(directory ?? string.Empty, "coop.ini");
                if (!File.Exists(path))
                {
                    _name = SystemInfo.deviceName;
                    return;
                }

                var section = string.Empty;
                foreach (var line in File.ReadAllLines(path))
                {
                    var trimmed = line.Trim();
                    if (trimmed.Length == 0 || trimmed.StartsWith("#"))
                        continue;

                    if (trimmed.StartsWith("[") && trimmed.EndsWith("]"))
                    {
                        section = trimmed.Substring(1, trimmed.Length - 2).Trim().ToLowerInvariant();
                        continue;
                    }

                    var split = trimmed.IndexOf('=');
                    if (split <= 0)
                        continue;

                    var key = trimmed.Substring(0, split).Trim().ToLowerInvariant();
                    var value = trimmed.Substring(split + 1).Trim();

                    switch (key)
                    {
                    case "host": _host = value; break;
                    case "port": _portText = value; break;
                    case "name": _name = value; break;
                    case "console":
                        if (section == "debug")
                            _consoleEnabled = value == "1" || value.Equals("true", StringComparison.OrdinalIgnoreCase);
                        break;
                    case "suppressspawns":
                        _suppressSpawns = value == "1" || value.Equals("true", StringComparison.OrdinalIgnoreCase);
                        break;
                    case "clearai":
                        _clearAi = value == "1" || value.Equals("true", StringComparison.OrdinalIgnoreCase);
                        break;
                    case "yawoffset":
                        if (float.TryParse(value, out var yawOffset))
                            _yawOffset = yawOffset;
                        break;
                    case "worlditemslog":
                        if (section == "debug")
                            _worldItemsLog = value == "1" ||
                                             value.Equals("true", StringComparison.OrdinalIgnoreCase);
                        break;
                    }
                }

                if (string.IsNullOrEmpty(_name))
                    _name = SystemInfo.deviceName;
            }
            catch (Exception ex)
            {
                Log("config error: " + ex.Message);
                _name = SystemInfo.deviceName;
            }
        }

        private void SaveConfig()
        {
            try
            {
                var directory = Path.GetDirectoryName(typeof(CoopRuntime).Assembly.Location);
                var path = Path.Combine(directory ?? string.Empty, "coop.ini");
                var lines = new System.Text.StringBuilder();
                lines.AppendLine($"host = {_host}");
                lines.AppendLine($"port = {_portText}");
                lines.AppendLine($"name = {_name}");

                // keep user switches (suppressSpawns/clearAi/...) out of the rewrite
                try
                {
                    if (File.Exists(path))
                    {
                        foreach (var line in File.ReadAllLines(path))
                        {
                            var trimmed = line.Trim();
                            if (trimmed.Length == 0 || trimmed.StartsWith("#"))
                                continue;

                            if (trimmed.StartsWith("[") && trimmed.EndsWith("]"))
                            {
                                lines.AppendLine(trimmed);
                                continue;
                            }

                            var split = trimmed.IndexOf('=');
                            if (split <= 0)
                                continue;

                            var key = trimmed.Substring(0, split).Trim();
                            if (key.Equals("host", StringComparison.OrdinalIgnoreCase) ||
                                key.Equals("port", StringComparison.OrdinalIgnoreCase) ||
                                key.Equals("name", StringComparison.OrdinalIgnoreCase))
                                continue;

                            lines.AppendLine(trimmed);
                        }
                    }
                }
                catch
                {
                    // ignore and write the essentials only
                }

                File.WriteAllText(path, lines.ToString(), Encoding.UTF8);
                Log($"config saved: host={_host} port={_portText} name={_name} suppressSpawns={_suppressSpawns} clearAi={_clearAi}");
            }
            catch (Exception ex)
            {
                Log("config save failed: " + ex.Message);
            }
        }

        private void ResetConfig()
        {
            _host = "127.0.0.1";
            _portText = "9050";
            _name = SystemInfo.deviceName;
            SaveConfig();
            _status = "config reset";
            Log("config reset to defaults");
        }

        private void Connect(bool showPanel = false)
        {
            if (string.IsNullOrWhiteSpace(_host))
                _host = "127.0.0.1";

            if (!int.TryParse(_portText, out var port) || port <= 0 || port > 65535)
            {
                _portText = "9050";
                port = 9050;
            }

            if (string.IsNullOrWhiteSpace(_name))
                _name = "player";

            SaveConfig();

            ClearRemotePlayers("connect");
            ResetReconnectState();
            _panelVisible = showPanel;
            _consoleActivated = false;
            SetConsoleInputBlocked(false);

            try
            {
                _client?.Dispose();
                _client = new CoopClient(_host, port);
                _client.ContentHash = _monsterCatalogHash ?? new byte[32];
                _client.SetResumeToken(_savedToken);
                _client.Login(string.IsNullOrEmpty(_name) ? "player" : _name, ClientVersionText);
                _status = "connecting...";
                _loginSince = Time.unscaledTime;

                // a new connection must not inherit the previous session's raid gating
                _raidSpawnAllowed = false;
                _raidEpoch = 0;
                _inRaidScene = false;
                _resumePendingTeleport = false;
                _lastMirrorCount = 0;
                _lastLoadout = null;
                _lastLoadoutCount = 0;
                _nextMirrorRetry = 0f;
                _nextLoadoutSend = 0f;
                _appliedAiTargets.Clear();

                Log($"login sent as '{_name}' -> {_host}:{port}");
            }
            catch (Exception ex)
            {
                _status = "connect failed: " + ex.Message;
                Log("connect failed: " + ex);
                _client = null;
                _loginSince = 0f;
            }
        }

        // BUG-004: a new session must not inherit high-water marks from the previous
        // one, otherwise the first resume/scene-load/spawn/event is swallowed by the
        // old counter and remote clones are never rebuilt.
        private void ResetReconnectState()
        {
            _lastResumeCount = 0;
            _lastSceneLoadCount = 0;
            _lastPlayerEventCount = 0;
            _lastRaidControlResultCount = 0;
            _lastForceResetCount = 0;
            _lastAiSpawnCount = 0;
            _lastSpawnAssign = 0;
            _lastLootCode = int.MinValue;
            _lastCombatEventId = 0;
            _lastActiveScene = string.Empty;

            // lobby resume carries (0,0,0); never reuse an old teleport target for it
            _teleportUntil = 0f;
            _teleportTarget = Vector3.zero;
            _resumeTarget = Vector3.zero;
            _spawnReported = false;
            _lastState = ClientState.Idle;
            _loggedFirstSnapshot = false;
        }

        private void Disconnect()
        {
            _client?.Dispose();
            _client = null;
            _loginSince = 0f;
            ClearRemotePlayers("disconnect");
            ResetReconnectState();
            _status = "disconnected";
            _panelVisible = true;
            _consoleActivated = false;
            SetConsoleInputBlocked(false);
            _raidSpawnAllowed = false;
            _raidEpoch = 0;
            _inRaidScene = false;
            _resumePendingTeleport = false;
            _lastMirrorCount = 0;
            _lastLoadout = null;
            _lastLoadoutCount = 0;
            _appliedAiTargets.Clear();
            ClearWorldItemVisuals("disconnect");
            ClearInventoryMirror("disconnect");
            Log("disconnected by user");
        }

        private void SendLocalInput()
        {
            if (_localDead)
                return;
            // CharacterMainControl.Main can be null after a scene load while the live
            // character is LevelManager.MainCharacter. Returning here silently stopped the
            // position stream for everyone else, so use the same resolution as the V8
            // sampler and the mirror code.
            var main = LocalMainCharacter();
            if (main == null)
                return;

            var position = main.transform.position;

            // the character root does not rotate in this game; the aim direction does
            var aim = main.CurrentAimDirection;
            var yawDegrees = main.transform.eulerAngles.y;
            if (aim.sqrMagnitude > 0.01f)
                yawDegrees = Mathf.Atan2(aim.x, aim.z) * Mathf.Rad2Deg;

            _client.SendInput(
                ++_seq,
                (int)(position.x * 100f),
                (int)(position.y * 100f),
                (int)(position.z * 100f),
                (ushort)(Mathf.Repeat(yawDegrees, 360f) / 360f * 65535f),
                0,
                1);

            _sentInputs++;
        }

        private static Type FindTypeByName(string name)
        {
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                try
                {
                    var direct = assembly.GetType(name, false);
                    if (direct != null)
                    {
                        Log($"type {name} found in {assembly.GetName().Name}");
                        return direct;
                    }
                }
                catch
                {
                    // ignore broken assemblies
                }

                try
                {
                    foreach (var candidate in assembly.GetTypes())
                    {
                        if (candidate.Name != name)
                            continue;

                        Log($"type {name} found in {assembly.GetName().Name} ({candidate.FullName})");
                        return candidate;
                    }
                }
                catch
                {
                    // dynamic assemblies / type load errors are fine
                }
            }

            return null;
        }

        private static float ReadHealth(object health)
        {
            if (health == null)
                return -1f;

            var field = health.GetType().GetField("_currentHealth",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            var value = field?.GetValue(health);
            return value is float f ? f : -1f;
        }

        private void CleanupRaidState()
        {
            var wasRaidContext = _inRaidScene || _raidSpawnAllowed;
            foreach (var pair in _realAi)
                if (pair.Value != null)
                    UnityEngine.Object.Destroy(pair.Value);
            _realAi.Clear();
            _realAiHealth.Clear();
            _realAiLastHp.Clear();

            foreach (var pair in _aiGhosts)
                if (pair.Value != null)
                    UnityEngine.Object.Destroy(pair.Value);
            _aiGhosts.Clear();

            _processedAiSpawns.Clear();
            _processedAiDespawns.Clear();
            _aiSpawnEpoch = 0;
            _localDead = false;
            _leaveSent = false;
            _autoExtractSent = false;
            _resumePendingTeleport = false;
            _raidSpawnAllowed = false;
            _raidEpoch = 0;
            _appliedAiTargets.Clear();
            ClearWorldItemVisuals("raid cleanup");
            ClearInventoryMirror("raid cleanup");
            if (!wasRaidContext)
                ResetExtractionState();
            Log("raid state cleaned");
        }

        private void OnDestroy()
        {
            RestorePerkMax();
            UnsubscribeV5Facts();
            Log("runtime destroyed");

            SetConsoleInputBlocked(false);
            try
            {
                HarmonySpike.Dispose();
            }
            catch (Exception ex)
            {
                Log("[harmony] dispose unavailable: " + ex.GetType().Name + ": " + ex.Message);
            }
            RaidFlowPatches.Dispose();
            GateAProbe.Dispose();
            ClearRemotePlayers("runtime destroyed");
            ClearWorldItemVisuals("runtime destroyed");
            ClearInventoryMirror("runtime destroyed");

            foreach (var pair in _aiGhosts)
                if (pair.Value != null)
                    UnityEngine.Object.Destroy(pair.Value);
            _aiGhosts.Clear();

            _client?.Dispose();
            _client = null;
        }
    }
}
