using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Text;
using DuckovCoop.Client;
using UnityEngine;

namespace DuckovCoopClient
{
    public sealed partial class CoopRuntime : MonoBehaviour
    {
        private const string ConsoleInputControl = "DuckovCoopConsoleInput";
        private const int ConsoleMaxOutput = 100;
        private const int ConsoleMaxInput = 200;

        private bool _consoleEnabled;
        private bool _consoleActivated;
        private bool _consoleInputBlocked;
        private bool _consoleFocusPending;
        private bool _consoleScrollToBottom;
        private string _consoleInput = string.Empty;
        private Vector2 _consoleScroll;
        private readonly List<string> _consoleOutput = new List<string>();
        private readonly List<string> _consoleHistory = new List<string>();
        private int _consoleHistoryIndex = -1;

        private float _netDropUntil;
        private uint _debugAiNextId = 900000;
        private readonly Dictionary<int, float> _activationDefaults = new Dictionary<int, float>();

        private void ToggleConsolePanel()
        {
            // M2-v12: only the host may open the control/console surface. A non-host still
            // gets a small "no permission" panel so the F1 key has visible feedback.
            if (_client != null && _client.State == ClientState.InGame && !IsLocalHost())
            {
                _panelVisible = !_panelVisible;
                _consoleActivated = false;
                SetConsoleInputBlocked(false);
                if (_panelVisible)
                    _interaction = "无相关权限";
                return;
            }

            if (!_consoleEnabled)
            {
                _panelVisible = !_panelVisible;
                return;
            }

            if (!_consoleActivated)
            {
                _panelVisible = true;
                _consoleActivated = true;
                _consoleFocusPending = true;
                SetConsoleInputBlocked(true);
                if (_consoleOutput.Count == 0)
                    AddConsoleOutput("console ready; type help");
                return;
            }

            _panelVisible = !_panelVisible;
            if (_panelVisible)
            {
                _consoleFocusPending = true;
                SetConsoleInputBlocked(true);
            }
            else
            {
                _consoleActivated = false;
                SetConsoleInputBlocked(false);
            }
        }

        private void CloseConsolePanel()
        {
            _panelVisible = false;
            _consoleActivated = false;
            SetConsoleInputBlocked(false);
        }

        private void SyncConsolePanelState()
        {
            if (_consoleInputBlocked && (!_panelVisible || !_consoleEnabled || !_consoleActivated))
            {
                SetConsoleInputBlocked(false);
                return;
            }

            // InputManager may not exist yet when the panel is opened in the
            // main menu; retry so entering a level cannot leak keys to gameplay.
            if (_consoleInputBlocked)
            {
                try
                {
                    InputManager.DisableInput(gameObject);
                }
                catch
                {
                    // keep the console usable even if the game input manager is absent
                }
            }
        }

        private void SetConsoleInputBlocked(bool blocked)
        {
            if (_consoleInputBlocked == blocked)
                return;

            _consoleInputBlocked = blocked;
            try
            {
                if (blocked)
                    InputManager.DisableInput(gameObject);
                else
                    InputManager.ActiveInput(gameObject);
            }
            catch (Exception ex)
            {
                Log("[console] input block failed: " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        private void AddConsoleOutput(string line)
        {
            if (string.IsNullOrEmpty(line))
                return;

            var text = $"[{DateTime.Now:HH:mm:ss}] {line.Replace("\r", " ").Replace("\n", " ")}";
            _consoleOutput.Add(text);
            if (_consoleOutput.Count > ConsoleMaxOutput)
                _consoleOutput.RemoveRange(0, _consoleOutput.Count - ConsoleMaxOutput);

            _consoleScrollToBottom = true;
        }

        private void DrawConsoleWindow(float panelX, float y, float panelWidth, float h)
        {
            if (!_consoleEnabled || !_consoleActivated || !_panelVisible)
                return;

            var height = 220f;
            var top = y + 420f;
            if (top + height > Screen.height - 8f)
                top = Mathf.Max(8f, Screen.height - height - 8f);

            GUI.Box(new Rect(panelX, top, panelWidth, height), GUIContent.none);
            GUI.Label(new Rect(panelX + 10, top + 6, panelWidth - 20, h),
                "Console (host-only)   help / Esc closes", _style);

            var viewTop = top + 34f;
            var viewHeight = height - 34f - h - 18f;
            var viewWidth = panelWidth - 20f;
            var contentHeight = Mathf.Max(viewHeight, _consoleOutput.Count * (h + 1f));
            if (_consoleScrollToBottom)
            {
                _consoleScroll.y = Mathf.Max(0f, contentHeight - viewHeight);
                _consoleScrollToBottom = false;
            }

            _consoleScroll = GUI.BeginScrollView(
                new Rect(panelX + 10, viewTop, viewWidth, viewHeight),
                _consoleScroll,
                new Rect(0f, 0f, viewWidth - 18f, contentHeight));

            for (var i = 0; i < _consoleOutput.Count; i++)
                GUI.Label(new Rect(0f, i * (h + 1f), viewWidth - 20f, h), _consoleOutput[i], _small);

            GUI.EndScrollView();

            var inputY = top + height - h - 6f;
            GUI.Label(new Rect(panelX + 10, inputY, 16, h), ">", _small);
            GUI.SetNextControlName(ConsoleInputControl);
            _consoleInput = GUI.TextField(
                new Rect(panelX + 30, inputY, panelWidth - 40, h + 2), _consoleInput ?? string.Empty, ConsoleMaxInput);

            if (_consoleFocusPending)
            {
                GUI.FocusControl(ConsoleInputControl);
                _consoleFocusPending = false;
            }

            if (Event.current != null && Event.current.type == EventType.KeyDown)
            {
                if (Event.current.keyCode == KeyCode.Escape)
                {
                    Event.current.Use();
                    CloseConsolePanel();
                    return;
                }

                var focused = GUI.GetNameOfFocusedControl() == ConsoleInputControl;
                if (focused && (Event.current.keyCode == KeyCode.Return || Event.current.keyCode == KeyCode.KeypadEnter))
                {
                    Event.current.Use();
                    ExecuteConsoleCommand(_consoleInput);
                    _consoleInput = string.Empty;
                    _consoleFocusPending = true;
                }
                else if (focused && Event.current.keyCode == KeyCode.UpArrow)
                {
                    Event.current.Use();
                    NavigateConsoleHistory(-1);
                }
                else if (focused && Event.current.keyCode == KeyCode.DownArrow)
                {
                    Event.current.Use();
                    NavigateConsoleHistory(1);
                }
            }
        }

        private void NavigateConsoleHistory(int direction)
        {
            if (_consoleHistory.Count == 0)
                return;

            if (_consoleHistoryIndex < 0)
                _consoleHistoryIndex = _consoleHistory.Count;
            _consoleHistoryIndex = Mathf.Clamp(_consoleHistoryIndex + direction, 0, _consoleHistory.Count);
            _consoleInput = _consoleHistoryIndex >= _consoleHistory.Count
                ? string.Empty
                : _consoleHistory[_consoleHistoryIndex];
            _consoleFocusPending = true;
        }

        private void ExecuteConsoleCommand(string raw)
        {
            raw = (raw ?? string.Empty).Trim();
            if (raw.Length == 0)
                return;

            if (raw.Length > ConsoleMaxInput)
                raw = raw.Substring(0, ConsoleMaxInput);

            AddConsoleOutput("> " + raw);
            string result;
            try
            {
                result = RunConsoleCommand(raw);
            }
            catch (Exception ex)
            {
                result = "error: " + ex.GetType().Name + ": " + ex.Message;
                AddConsoleOutput(result);
            }

            var logCommand = raw.Replace("\r", " ").Replace("\n", " ");
            if (logCommand.Length > 160)
                logCommand = logCommand.Substring(0, 160) + "...";
            Log($"[console] cmd={logCommand} result={result}");

            if (_consoleHistory.Count == 0 || _consoleHistory[_consoleHistory.Count - 1] != raw)
                _consoleHistory.Add(raw);
            if (_consoleHistory.Count > 50)
                _consoleHistory.RemoveAt(0);
            _consoleHistoryIndex = -1;
        }

        private string RunConsoleCommand(string raw)
        {
            var parts = raw.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0)
                return "empty command";

            var command = parts[0].ToLowerInvariant();

            if (!IsConsoleHost())
            {
                AddConsoleOutput("仅 host 可用");
                return "host-only rejected";
            }

            switch (command)
            {
            case "help":
                AddConsoleOutput("help: " + string.Join(" | ", ConsoleHelp));
                return "ok";
            case "status":
                return ConsoleStatus();
            case "tp":
                return ConsoleTeleport(parts);
            case "ai":
                return ConsoleAi(parts);
            case "mark":
            {
                var text = raw.Length > 4 ? raw.Substring(4).Trim() : string.Empty;
                AddConsoleOutput("MARK " + text);
                Log("[console] MARK " + text);
                return "marked";
            }
            case "net":
                return ConsoleNet(parts);
            default:
                AddConsoleOutput("unknown command: " + command + " (help)");
                return "unknown command";
            }
        }

        private static readonly string[] ConsoleHelp =
        {
            "help",
            "status",
            "tp <x> <y> <z>",
            "ai list",
            "ai spawn <monsterId> [count] [x y z]",
            "ai kill <aiId>",
            "ai awake <radius> (0=default)",
            "mark <text>",
            "net drop [seconds] (1-60)"
        };

        private bool IsConsoleHost()
        {
            return IsLocalHost();
        }

        private string ConsoleStatus()
        {
            if (_client == null)
            {
                AddConsoleOutput("status: not connected");
                return "not connected";
            }

            var snapshot = _client.LatestSnapshot;
            var rtt = ReadClientRttMs();
            var pending = ReadClientPending();
            var text = $"state={_client.State} id={_client.PlayerId} version={ClientVersionText} " +
                       $"phase={_client.RaidPhase} epoch={_client.RaidEpoch} " +
                       $"rtt={(rtt >= 0f ? rtt.ToString("F0", CultureInfo.InvariantCulture) + "ms" : "n/a")} " +
                       $"pending={pending} snapshots={_client.SnapshotsReceived} sent={_sentInputs} " +
                       $"players={snapshot?.Players.Length ?? 0}";
            AddConsoleOutput("status: " + text);
            return "ok";
        }

        private float ReadClientRttMs()
        {
            try
            {
                var controlField = typeof(CoopClient).GetField("_control",
                    BindingFlags.Instance | BindingFlags.NonPublic);
                var channel = controlField?.GetValue(_client);
                if (channel == null)
                    return -1f;

                var has = channel.GetType().GetField("_hasRtt",
                    BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(channel);
                if (!(has is bool hasRtt) || !hasRtt)
                    return -1f;

                var srtt = channel.GetType().GetField("_srtt",
                    BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(channel);
                return srtt is double d ? (float)d : -1f;
            }
            catch
            {
                return -1f;
            }
        }

        private int ReadClientPending()
        {
            try
            {
                var controlField = typeof(CoopClient).GetField("_control",
                    BindingFlags.Instance | BindingFlags.NonPublic);
                var channel = controlField?.GetValue(_client);
                var pending = channel?.GetType().GetProperty("PendingCount",
                    BindingFlags.Instance | BindingFlags.Public)?.GetValue(channel);
                return pending is int i ? i : -1;
            }
            catch
            {
                return -1;
            }
        }

        private string ConsoleTeleport(string[] parts)
        {
            if (parts.Length != 4 ||
                !TryParseFloat(parts[1], out var x) ||
                !TryParseFloat(parts[2], out var y) ||
                !TryParseFloat(parts[3], out var z))
            {
                AddConsoleOutput("usage: tp <x> <y> <z>");
                return "bad arguments";
            }

            var main = CharacterMainControl.Main;
            if (main == null)
            {
                AddConsoleOutput("tp: local character not ready");
                return "no character";
            }

            main.transform.position = new Vector3(x, y, z);
            AddConsoleOutput($"teleported to {x:F1},{y:F1},{z:F1}");
            return "ok";
        }

        private string ConsoleAi(string[] parts)
        {
            if (parts.Length < 2)
            {
                AddConsoleOutput("usage: ai list | ai spawn ... | ai kill <id> | ai awake <radius>");
                return "bad arguments";
            }

            switch (parts[1].ToLowerInvariant())
            {
            case "list":
                return ConsoleAiList();
            case "spawn":
                return ConsoleAiSpawn(parts);
            case "kill":
                return ConsoleAiKill(parts);
            case "awake":
                return ConsoleAiAwake(parts);
            default:
                AddConsoleOutput("unknown ai command: " + parts[1]);
                return "unknown ai command";
            }
        }

        private string ConsoleAiList()
        {
            var count = 0;
            foreach (var pair in _realAi)
            {
                if (pair.Value == null)
                    continue;

                var hp = "?";
                if (_realAiHealth.TryGetValue(pair.Key, out var health))
                {
                    var value = ReadHealth(health);
                    if (value >= 0f)
                        hp = value.ToString("F0", CultureInfo.InvariantCulture);
                }

                AddConsoleOutput($"ai id={pair.Key} owner={_client?.PlayerId ?? 0} real=true " +
                                 $"awake={pair.Value.activeInHierarchy} pos={FormatVector(pair.Value.transform.position)} hp={hp}");
                count++;
            }

            foreach (var pair in _aiGhosts)
            {
                if (pair.Value == null || _realAi.ContainsKey(pair.Key))
                    continue;

                var owner = _client?.PlayerId ?? 0;
                var hp = "?";
                if (_client != null && _client.AiSpawns.TryGetValue(pair.Key, out var spawn))
                {
                    owner = spawn.OwnerPlayerId;
                    hp = spawn.Hp.ToString(CultureInfo.InvariantCulture);
                }

                AddConsoleOutput($"ai id={pair.Key} owner={owner} real=false " +
                                 $"awake={pair.Value.activeInHierarchy} pos={FormatVector(pair.Value.transform.position)} hp={hp}");
                count++;
            }

            if (count == 0)
            {
                AddConsoleOutput("ai list: no local AI (host-spawned only)");
                return "empty";
            }

            AddConsoleOutput($"ai list: {count} local AI");
            return "ok";
        }

        private string ConsoleAiSpawn(string[] parts)
        {
            if (parts.Length < 3 || !int.TryParse(parts[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out var monsterId))
            {
                AddConsoleOutput("usage: ai spawn <monsterId> [count] [x y z]");
                return "bad arguments";
            }

            var count = 1;
            var position = CharacterMainControl.Main != null
                ? CharacterMainControl.Main.transform.position + CharacterMainControl.Main.transform.forward * 3f
                : new Vector3(0f, 0f, 3f);

            var rest = parts.Skip(3).ToArray();
            if (rest.Length == 1)
            {
                if (!int.TryParse(rest[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out count))
                {
                    AddConsoleOutput("usage: ai spawn <monsterId> [count] [x y z]");
                    return "bad arguments";
                }
            }
            else if (rest.Length == 3)
            {
                if (!TryParseFloat(rest[0], out var x) ||
                    !TryParseFloat(rest[1], out var y) ||
                    !TryParseFloat(rest[2], out var z))
                {
                    AddConsoleOutput("ai spawn: invalid coordinates");
                    return "bad arguments";
                }
                position = new Vector3(x, y, z);
            }
            else if (rest.Length == 4)
            {
                if (!int.TryParse(rest[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out count) ||
                    !TryParseFloat(rest[1], out var x) ||
                    !TryParseFloat(rest[2], out var y) ||
                    !TryParseFloat(rest[3], out var z))
                {
                    AddConsoleOutput("usage: ai spawn <monsterId> [count] [x y z]");
                    return "bad arguments";
                }
                position = new Vector3(x, y, z);
            }
            else if (rest.Length != 0)
            {
                AddConsoleOutput("usage: ai spawn <monsterId> [count] [x y z]");
                return "bad arguments";
            }

            count = Mathf.Clamp(count, 1, 10);
            for (var i = 0; i < count; i++)
            {
                var pos = position + new Vector3(i * 1.5f, 0f, 0f);
                var spawn = new AiSpawn
                {
                    AiId = _debugAiNextId++,
                    OwnerPlayerId = _client != null ? _client.PlayerId : (ushort)1,
                    X = Mathf.RoundToInt(pos.x * 100f),
                    Y = Mathf.RoundToInt(pos.y * 100f),
                    Z = Mathf.RoundToInt(pos.z * 100f),
                    Yaw = 0,
                    Hp = 100
                };

                // PLAY owns the real spawn path; this console only invokes it. The
                // monsterId is logged until SAiSpawn carries a preset id (A3).
                var real = TrySpawnRealAi(spawn);
                if (!real)
                    CreateAiGhost(spawn);
                AddConsoleOutput($"ai spawn id={spawn.AiId} monster={monsterId} count={count} " +
                                 $"real={real} pos={FormatVector(pos)}");
            }

            AddConsoleOutput("ai spawn: wait ~3s for the preset, then run ai list");
            return "ok";
        }

        private string ConsoleAiKill(string[] parts)
        {
            if (parts.Length != 3 || !uint.TryParse(parts[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out var id))
            {
                AddConsoleOutput("usage: ai kill <aiId>");
                return "bad arguments";
            }

            if (!_realAi.ContainsKey(id) && !_aiGhosts.ContainsKey(id))
            {
                AddConsoleOutput("ai kill: id not found: " + id);
                return "not found";
            }

            RemoveAi(id);
            AddConsoleOutput("ai kill: id=" + id);
            return "ok";
        }

        private string ConsoleAiAwake(string[] parts)
        {
            if (parts.Length != 3 || !TryParseFloat(parts[2], out var radius))
            {
                AddConsoleOutput("usage: ai awake <radius> (0=default)");
                return "bad arguments";
            }

            radius = Mathf.Clamp(radius, 0f, 500f);
            var type = FindTypeByName("SetActiveByPlayerDistance");
            if (type == null)
            {
                AddConsoleOutput("ai awake: SetActiveByPlayerDistance not found");
                return "not found";
            }

            var field = type.GetField("distance", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (field == null)
            {
                AddConsoleOutput("ai awake: distance field not found");
                return "not found";
            }

            var objects = UnityEngine.Object.FindObjectsOfType(type);
            var changed = 0;
            foreach (var obj in objects)
            {
                if (obj == null)
                    continue;

                var key = obj.GetInstanceID();
                if (!_activationDefaults.ContainsKey(key))
                    _activationDefaults[key] = (float)field.GetValue(obj);
                field.SetValue(obj, radius > 0f ? radius : _activationDefaults[key]);
                changed++;
            }

            AddConsoleOutput($"ai awake: radius={radius:F0} instances={changed}");
            return changed > 0 ? "ok" : "no instance";
        }

        private string ConsoleNet(string[] parts)
        {
            if (parts.Length < 2 || !parts[1].Equals("drop", StringComparison.OrdinalIgnoreCase))
            {
                AddConsoleOutput("usage: net drop [seconds] (1-60)");
                return "bad arguments";
            }

            var seconds = 10f;
            if (parts.Length >= 3 && !TryParseFloat(parts[2], out seconds))
            {
                AddConsoleOutput("usage: net drop [seconds] (1-60)");
                return "bad arguments";
            }

            seconds = Mathf.Clamp(seconds, 1f, 60f);
            _netDropUntil = Time.unscaledTime + seconds;
            AddConsoleOutput($"net drop: paused local send/receive for {seconds:F0}s");
            return "ok";
        }

        private bool ConsoleNetDropActive()
        {
            if (_netDropUntil <= 0f)
                return false;

            if (Time.unscaledTime < _netDropUntil)
                return true;

            _netDropUntil = 0f;
            AddConsoleOutput("net drop: resumed local send/receive");
            return false;
        }

        private static bool TryParseFloat(string text, out float value)
        {
            if (!float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value))
                return false;

            return !float.IsNaN(value) && !float.IsInfinity(value);
        }

        private static string FormatVector(Vector3 position)
        {
            return $"{position.x:F1},{position.y:F1},{position.z:F1}";
        }
    }
}
