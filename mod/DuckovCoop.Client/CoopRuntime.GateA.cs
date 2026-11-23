using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEngine;

namespace DuckovCoopClient
{
    public sealed partial class CoopRuntime : MonoBehaviour
    {
        private const string GateAConfigSection = "debug";
        private const string GateAConfigKey = "gatea";
        private const string GateASentinelName = "gate_a.on";

        private bool _gateAConfigLoaded;
        private bool _gateAEnabled;
        private float _nextGateAStatusLog;
        private float _nextGateAErrorLog;
        private float _headshotScanUntil;
        private float _nextHeadshotScan;
        private bool _headshotAssetsLogged;
        private readonly HashSet<ushort> _gateARemoteProxyLogged = new HashSet<ushort>();
        private readonly Dictionary<int, bool> _headshotFxState = new Dictionary<int, bool>();
        private readonly Dictionary<uint, Vector3> _gateAFreezePositions = new Dictionary<uint, Vector3>();
        private readonly Dictionary<uint, Quaternion> _gateAFreezeRotations = new Dictionary<uint, Quaternion>();

        /// <summary>
        /// Gate A diagnostic driver. Disabled by default; opt in with
        /// [debug] gatea = 1 or a file named gate_a.on next to the mod DLL.
        /// F7 status, F8 synthetic apply, F9 synthetic skip.
        /// </summary>
        private void UpdateGateAProbe()
        {
            if (!_gateAConfigLoaded)
                LoadGateAConfig();

            if (!_gateAEnabled)
                return;

            try
            {
                GateAProbe.Tick(this);
                EnsureGateARemoteProxies();
                EnsureGateAAiSensors();
                UpdateHeadshotEffectScan();

                if (Time.unscaledTime >= _nextGateAStatusLog)
                {
                    _nextGateAStatusLog = Time.unscaledTime + 15f;
                    GateALogStatus();
                }
            }
            catch (Exception ex)
            {
                if (Time.unscaledTime >= _nextGateAErrorLog)
                {
                    _nextGateAErrorLog = Time.unscaledTime + 5f;
                    Log("[gatea] update failed: " + ex.GetType().Name + ": " + ex.Message);
                }
            }
        }

        private void LoadGateAConfig()
        {
            _gateAConfigLoaded = true;
            try
            {
                var directory = Path.GetDirectoryName(typeof(CoopRuntime).Assembly.Location);
                var configPath = Path.Combine(directory ?? string.Empty, "coop.ini");
                if (File.Exists(configPath))
                {
                    var section = string.Empty;
                    foreach (var line in File.ReadAllLines(configPath))
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
                        if (split <= 0 || section != GateAConfigSection)
                            continue;

                        var key = trimmed.Substring(0, split).Trim().ToLowerInvariant();
                        if (key != GateAConfigKey)
                            continue;

                        var value = trimmed.Substring(split + 1).Trim();
                        _gateAEnabled = value == "1" || value.Equals("true", StringComparison.OrdinalIgnoreCase);
                    }
                }

                if (File.Exists(Path.Combine(directory ?? string.Empty, GateASentinelName)))
                    _gateAEnabled = true;

                GateAProbe.Enabled = _gateAEnabled;
                GateAProbe.FreezeAiGhosts = false;
                if (_gateAEnabled)
                    GateAProbe.Init();

                Log($"[gatea] enabled={_gateAEnabled} sentinel={GateASentinelName} " +
                    $"freezeAi=False " +
                    $"log={LogPath} controls=F7 status F8 apply F9 skip F12 dump");
            }
            catch (Exception ex)
            {
                _gateAEnabled = false;
                GateAProbe.Enabled = false;
                Log("[gatea] config error: " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        private void EnsureGateARemoteProxies()
        {
            foreach (var pair in _remotes)
            {
                var remote = pair.Value;
                if (remote == null)
                    continue;

                if (_remoteAiProxies.TryGetValue(pair.Key, out var existing))
                {
                    if (existing != null && existing.RemoteRoot == remote &&
                        existing.Health != null && existing.Receiver != null)
                        continue;

                    _remoteAiProxies.Remove(pair.Key);
                }

                RemoteAiProxy proxy = null;
                try
                {
                    proxy = CreateMinimalRemoteAiProxy(pair.Key, remote);
                }
                catch (Exception ex)
                {
                    Log($"[gatea] A4 proxy failed id={pair.Key}: {ex.GetType().Name}: {ex.Message}");
                    continue;
                }

                if (proxy == null)
                    continue;
                if (_gateARemoteProxyLogged.Add(pair.Key))
                {
                    Log($"[gatea] A4 remote proxy ready id={pair.Key} root={remote.name} " +
                        $"health={(proxy.Health != null)} receiver={(proxy.Receiver != null)} " +
                        $"layer={remote.layer} hp={(proxy.Health != null ? proxy.Health.MaxHealth : -1f):F0}");
                }
            }
        }

        private void EnsureGateAAiSensors()
        {
            foreach (var pair in _aiGhosts)
            {
                var ghost = pair.Value;
                if (ghost == null)
                    continue;

                GateAProbe.ConfigureAiGhost(ghost, pair.Key);
            }
        }

        internal void GateADumpAiComponents()
        {
            Log("[gatea] A11 component dump start");
            foreach (var pair in _realAi)
                DumpAiComponents(pair.Value, "real", pair.Key);
            foreach (var pair in _aiGhosts)
                DumpAiComponents(pair.Value, "ghost", pair.Key);
            Log("[gatea] A11 component dump done");
        }

        private void DumpAiComponents(GameObject root, string kind, uint id)
        {
            if (root == null)
                return;

            try
            {
                var character = root.GetComponentInParent<CharacterMainControl>();
                var dumpRoot = character != null ? character.gameObject : root;
                var all = dumpRoot.GetComponentsInChildren<Component>(true);
                var counts = new Dictionary<string, int>();
                var enabledCounts = new Dictionary<string, int>();
                var paths = new Dictionary<string, string>();

                foreach (var component in all)
                {
                    if (component == null)
                        continue;

                    var type = component.GetType().FullName ?? component.GetType().Name;
                    counts[type] = counts.TryGetValue(type, out var count) ? count + 1 : 1;
                    var enabled = !(component is Behaviour behaviour) || behaviour.enabled;
                    if (component is Collider collider)
                        enabled = collider.enabled;
                    if (enabled)
                        enabledCounts[type] = enabledCounts.TryGetValue(type, out var enabledCount)
                            ? enabledCount + 1
                            : 1;
                    if (!paths.ContainsKey(type))
                        paths[type] = GateAFxPath(component.transform);
                }

                Log($"[gatea] A11 comp {kind} id={id} source={root.name} dump={dumpRoot.name} " +
                    $"path={GateAFxPath(dumpRoot.transform)} total={all.Length} types={counts.Count}");

                foreach (var pair in counts.OrderByDescending(item => item.Value).ThenBy(item => item.Key))
                {
                    enabledCounts.TryGetValue(pair.Key, out var enabled);
                    paths.TryGetValue(pair.Key, out var path);
                    Log($"[gatea] A11 comp {kind} id={id} type={pair.Key} " +
                        $"count={pair.Value} enabled={enabled} path={path}");
                }

                foreach (var collider in dumpRoot.GetComponentsInChildren<Collider>(true))
                {
                    if (collider == null)
                        continue;

                    Log($"[gatea] A11 collider {kind} id={id} " +
                        $"type={collider.GetType().Name} path={GateAFxPath(collider.transform)} " +
                        $"enabled={collider.enabled} trigger={collider.isTrigger} " +
                        $"layer={collider.gameObject.layer} " +
                        $"bounds={GateAVector(collider.bounds.center)}/{GateAVector(collider.bounds.size)}");
                }
            }
            catch (Exception ex)
            {
                Log($"[gatea] A11 comp {kind} id={id} failed: {ex.GetType().Name}: {ex.Message}");
            }
        }

        internal void GateAToggleFreezeAi()
        {
            GateAProbe.FreezeAiGhosts = !GateAProbe.FreezeAiGhosts;
            var frozen = GateAProbe.FreezeAiGhosts;
            var count = 0;

            if (frozen)
            {
                _gateAFreezePositions.Clear();
                _gateAFreezeRotations.Clear();
                foreach (var pair in _realAi)
                {
                    if (pair.Value == null)
                        continue;

                    _gateAFreezePositions[pair.Key] = pair.Value.transform.position;
                    _gateAFreezeRotations[pair.Key] = pair.Value.transform.rotation;
                    var controller = pair.Value.GetComponent<AICharacterController>();
                    if (controller != null)
                        controller.enabled = false;
                    count++;
                }
            }
            else
            {
                foreach (var pair in _realAi)
                {
                    if (pair.Value == null)
                        continue;

                    var controller = pair.Value.GetComponent<AICharacterController>();
                    if (controller != null)
                        controller.enabled = true;
                    count++;
                }
                _gateAFreezePositions.Clear();
                _gateAFreezeRotations.Clear();
            }

            Log($"[gatea] AI freeze={(frozen ? "ON" : "OFF")} " +
                $"hostControllers={count} ghostFreeze={GateAProbe.FreezeAiGhosts} " +
                "press F11 on BOTH host and non-host for a stable target");
        }

        internal void GateAApplyFreezeToAi()
        {
            if (!GateAProbe.FreezeAiGhosts || _realAi.Count == 0)
                return;

            foreach (var pair in _realAi)
            {
                if (pair.Value == null)
                    continue;

                if (!_gateAFreezePositions.TryGetValue(pair.Key, out var position))
                {
                    position = pair.Value.transform.position;
                    _gateAFreezePositions[pair.Key] = position;
                    _gateAFreezeRotations[pair.Key] = pair.Value.transform.rotation;
                }

                pair.Value.transform.position = position;
                if (_gateAFreezeRotations.TryGetValue(pair.Key, out var rotation))
                    pair.Value.transform.rotation = rotation;

                var controller = pair.Value.GetComponent<AICharacterController>();
                if (controller != null && controller.enabled)
                    controller.enabled = false;
            }
        }

        internal void GateAStartHeadshotScan()
        {
            _headshotScanUntil = Time.unscaledTime + 15f;
            _nextHeadshotScan = 0f;
            _headshotAssetsLogged = false;
            _headshotFxState.Clear();
            Log("[gatea] A2fx scan start 15s; aim/fire at AI head, then inspect FocusedHeadshot/GunCrit lines");
        }

        private void UpdateHeadshotEffectScan()
        {
            if (_headshotScanUntil <= 0f)
                return;

            if (Time.unscaledTime > _headshotScanUntil)
            {
                _headshotScanUntil = 0f;
                Log("[gatea] A2fx scan done");
                return;
            }

            if (Time.unscaledTime < _nextHeadshotScan)
                return;

            _nextHeadshotScan = Time.unscaledTime + 0.1f;
            try
            {
                if (!_headshotAssetsLogged)
                {
                    _headshotAssetsLogged = true;
                    foreach (var asset in Resources.FindObjectsOfTypeAll<GameObject>())
                    {
                        if (asset == null || asset.scene.IsValid() || !GateAHeadshotName(asset.name))
                            continue;
                        Log($"[gatea] A2fx asset gameobject name={asset.name} active={asset.activeInHierarchy}");
                    }

                    foreach (var asset in Resources.FindObjectsOfTypeAll<ScriptableObject>())
                    {
                        if (asset == null || !GateAHeadshotName(asset.name))
                            continue;
                        Log($"[gatea] A2fx asset scriptable name={asset.name} type={asset.GetType().Name}");
                    }
                }

                foreach (var go in Resources.FindObjectsOfTypeAll<GameObject>())
                {
                    if (go == null || !go.scene.IsValid() || !GateAHeadshotName(go.name))
                        continue;

                    var id = go.GetInstanceID();
                    var active = go.activeInHierarchy;
                    if (_headshotFxState.TryGetValue(id, out var previous) && previous == active)
                        continue;

                    _headshotFxState[id] = active;
                    Log($"[gatea] A2fx t={Time.unscaledTime:F3} frame={Time.frameCount} " +
                        $"name={go.name} active={active} path={GateAFxPath(go.transform)} " +
                        $"pos={GateAVector(go.transform.position)} components={GateAFxComponents(go)}");
                }
            }
            catch (Exception ex)
            {
                Log("[gatea] A2fx scan failed: " + ex.GetType().Name + ": " + ex.Message);
                _headshotScanUntil = 0f;
            }
        }

        private static bool GateAHeadshotName(string name)
        {
            if (string.IsNullOrEmpty(name))
                return false;

            var lower = name.ToLowerInvariant();
            return lower.Contains("focusedheadshot") || lower.Contains("headshot") ||
                   lower.Contains("guncrit") || lower.Contains("gun_crit");
        }

        private static string GateAFxComponents(GameObject go)
        {
            var parts = new List<string>();
            if (go.GetComponent("ParticleSystem") != null) parts.Add("ParticleSystem");
            if (go.GetComponent<Renderer>() != null) parts.Add("Renderer");
            if (go.GetComponent<AudioSource>() != null) parts.Add("AudioSource");
            if (go.GetComponent<Animator>() != null) parts.Add("Animator");
            if (go.GetComponent<Light>() != null) parts.Add("Light");
            return parts.Count > 0 ? string.Join(",", parts) : "none";
        }

        private static string GateAFxPath(Transform transform)
        {
            if (transform == null)
                return "<null>";

            var parts = new List<string>();
            var current = transform;
            for (var i = 0; i < 6 && current != null; i++)
            {
                parts.Add(current.name);
                current = current.parent;
            }
            parts.Reverse();
            return string.Join("/", parts);
        }

        private static string GateAVector(Vector3 value)
        {
            return $"({value.x:F2},{value.y:F2},{value.z:F2})";
        }

        internal void GateALogStatus()
        {
            try
            {
                var rtt = ReadClientRttMs();
                var proxyCount = 0;
                foreach (var pair in _remoteAiProxies)
                {
                    if (pair.Value != null && pair.Value.Health != null && pair.Value.Receiver != null)
                        proxyCount++;
                }

                var sensorCount = 0;
                foreach (var pair in _aiGhosts)
                {
                    if (pair.Value != null && pair.Value.GetComponent<GateAAiSensorMarker>() != null)
                        sensorCount++;
                }

                Log($"[gatea] status enabled={_gateAEnabled} player={(_client != null ? _client.PlayerId : 0)} " +
                    $"remotes={_remotes.Count} proxies={proxyCount} ghosts={_aiGhosts.Count} " +
                    $"sensors={sensorCount} realAi={_realAi.Count} " +
                    $"rtt={(rtt >= 0f ? rtt.ToString("F0") + "ms" : "n/a")}");
            }
            catch (Exception ex)
            {
                Log("[gatea] status failed: " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        internal void GateATestApply(bool skipNative)
        {
            try
            {
                var main = CharacterMainControl.Main;
                if (main == null)
                {
                    Log("[gatea] A5/A6 test skipped: no local CharacterMainControl");
                    return;
                }

                var hostMode = _client == null || IsConsoleHost();
                if (hostMode)
                {
                    foreach (var pair in _realAi)
                    {
                        if (pair.Value == null || !_realAiHealth.TryGetValue(pair.Key, out var aiHealth))
                            continue;

                        var before = ReadHealth(aiHealth);
                        if (before < 0f)
                            continue;

                        var after = Mathf.Max(1f, before - 1f);
                        (aiHealth as Health)?.SetHealth(after);
                        _realAiLastHp[pair.Key] = after;
                        Log($"[gatea] A12 host writeback ai={pair.Key} before={before:F0} after={after:F0} " +
                            "path=Health.SetHealth no-native-Hurt");
                        return;
                    }

                    // Single-player/host-without-coop-AI: use the nearest native AI
                    // as the A12 writeback target. This is still the same capability:
                    // set authoritative HP without calling native Health.Hurt.
                    var nativeHealth = FindNearestNativeAiHealth(main, out var nativeAi);
                    if (nativeHealth != null)
                    {
                        var before = nativeHealth.CurrentHealth;
                        var after = Mathf.Max(1f, before - 1f);
                        nativeHealth.SetHealth(after);
                        Log($"[gatea] A12 native AI writeback ai={(nativeAi != null ? nativeAi.name : "<null>")} " +
                            $"before={before:F0} after={after:F0} path=Health.SetHealth no-native-Hurt " +
                            $"client={(_client != null ? "connected" : "single")}");
                        return;
                    }

                    Log("[gatea] A12 host writeback skipped: no real/native AI health");
                    return;
                }

                var receiver = main.GetComponent<DamageReceiver>()
                               ?? main.GetComponentInChildren<DamageReceiver>(true)
                               ?? main.GetComponentInParent<DamageReceiver>();
                var health = receiver != null ? receiver.health : null;
                if (health == null)
                    health = main.GetComponent<Health>() ?? main.GetComponentInChildren<Health>(true);

                if (health == null)
                {
                    Log("[gatea] A5/A6 test skipped: no local Health");
                    return;
                }

                GateAProbe.ApplySynthetic(health, receiver, main, skipNative);
            }
            catch (Exception ex)
            {
                Log("[gatea] synthetic apply failed: " + ex.GetType().Name + ": " + ex.Message);
                Log("[gatea] synthetic apply stack: " + (ex.StackTrace ?? "<no stack>"));
            }
        }

        private static Health FindNearestNativeAiHealth(CharacterMainControl main, out AICharacterController ai)
        {
            ai = null;
            if (main == null)
                return null;

            var bestDistance = float.MaxValue;
            Health bestHealth = null;
            foreach (var candidate in UnityEngine.Object.FindObjectsOfType<AICharacterController>())
            {
                if (candidate == null)
                    continue;

                var distance = Vector3.Distance(candidate.transform.position, main.transform.position);
                if (distance >= bestDistance)
                    continue;

                var receiver = candidate.GetComponent<DamageReceiver>()
                               ?? candidate.GetComponentInChildren<DamageReceiver>(true);
                if (receiver == null || receiver.health == null || receiver.health.IsDead)
                    continue;

                bestDistance = distance;
                ai = candidate;
                bestHealth = receiver.health;
            }

            if (bestHealth != null)
                return bestHealth;

            // Some native AI objects do not expose an enabled AICharacterController
            // (e.g. a dead/passive character or a prefab variant). Fall back to any
            // living non-player DamageReceiver so F8 still exercises SetHealth.
            foreach (var receiver in UnityEngine.Object.FindObjectsOfType<DamageReceiver>())
            {
                if (receiver == null || receiver.health == null || receiver.health.IsDead)
                    continue;
                if (receiver.transform == main.transform || receiver.transform.IsChildOf(main.transform))
                    continue;
                if (receiver.health.team == main.Team)
                    continue;

                var distance = Vector3.Distance(receiver.transform.position, main.transform.position);
                if (distance >= bestDistance)
                    continue;

                bestDistance = distance;
                bestHealth = receiver.health;
                ai = receiver.GetComponentInParent<AICharacterController>();
            }

            return bestHealth;
        }
    }
}
