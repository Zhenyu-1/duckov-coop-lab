using System;
using System.Collections.Generic;
using Duckov.Utilities;
using DuckovCoop.Client;
using ItemStatsSystem;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace DuckovCoopClient
{
    /// <summary>Links a native corpse lootbox actor to its server corpse container id.</summary>
    public sealed class CoopCorpseMarker : MonoBehaviour
    {
        public uint ContainerId;
    }

    /// <summary>
    /// T7/D6: mirrors SCorpseSpawn/SCorpseDespawn into native InteractableLootbox actors. The
    /// actor is only a visual/interaction anchor; contents come from ContainerMirrorState when
    /// the player opens it (LootBoxLoader local roll is suppressed for coop).
    /// </summary>
    public sealed partial class CoopRuntime
    {
        private sealed class CorpseVisual
        {
            public uint ContainerId;
            public CorpseSpawn Info;
            public InteractableLootbox Box;
        }

        private const int MaxCorpseCreatesPerUpdate = 2;
        private readonly Dictionary<uint, CorpseVisual> _corpseVisuals =
            new Dictionary<uint, CorpseVisual>();
        private readonly Dictionary<uint, float> _aiCorpseReadyAt =
            new Dictionary<uint, float>();
        private float _nextCorpseReconcile;
        private int _lastCorpseSpawnCount;

        private void UpdateCorpses()
        {
            if (_client == null || _client.State != ClientState.InGame || !_inRaidScene)
            {
                ClearCorpseVisuals("not in raid");
                _lastCorpseSpawnCount = _client != null ? _client.CorpseSpawnCount : 0;
                return;
            }

            if (_client.CorpseSpawnCount == _lastCorpseSpawnCount &&
                Time.unscaledTime < _nextCorpseReconcile)
                return;
            _lastCorpseSpawnCount = _client.CorpseSpawnCount;
            _nextCorpseReconcile = Time.unscaledTime + 0.25f;
            ReconcileCorpses();
        }

        private void ReconcileCorpses()
        {
            var activeScene = SceneManager.GetActiveScene().name;
            var removed = new List<uint>();
            foreach (var pair in _corpseVisuals)
            {
                if (!_client.Corpses.TryGetValue(pair.Key, out var info) ||
                    !IsWorldItemSceneVisible(info.Scene, activeScene))
                    removed.Add(pair.Key);
            }
            foreach (var containerId in removed)
            {
                if (_corpseVisuals.TryGetValue(containerId, out var visual))
                    DestroyCorpseVisual(visual);
            }

            var created = 0;
            foreach (var pair in _client.Corpses)
            {
                if (_corpseVisuals.ContainsKey(pair.Key))
                    continue;
                if (!IsWorldItemSceneVisible(pair.Value.Scene, activeScene))
                    continue;
                // Native OnDead creates the dead-lootbox visual a few frames after the
                // server SCorpseSpawn. Give it a short window so we can reuse that native
                // box instead of creating a generic duplicate (which would leave the
                // native AI loot box unmapped and play its local drop logic). Keep this
                // longer than the 0.55s AI fall so the box does not appear while the
                // boss model is still standing.
                if (pair.Value.PlayerId == 0)
                {
                    if (!_aiCorpseReadyAt.TryGetValue(pair.Key, out var readyAt))
                    {
                        _aiCorpseReadyAt[pair.Key] = Time.unscaledTime + 1.0f;
                        continue;
                    }
                    if (Time.unscaledTime < readyAt)
                        continue;
                    _aiCorpseReadyAt.Remove(pair.Key);
                }
                if (created >= MaxCorpseCreatesPerUpdate)
                    break;
                CreateCorpseVisual(pair.Value);
                created++;
            }
        }

        private void CreateCorpseVisual(CorpseSpawn info)
        {
            var isPlayerCorpse = info.PlayerId != 0;
            var position = new Vector3(info.X / 100f, info.Y / 100f, info.Z / 100f);
            InteractableLootbox nativePrefab = null;
            InteractableLootbox nativeBox = null;
            if (!isPlayerCorpse)
            {
                var candidates = UnityEngine.Object.FindObjectsOfType<InteractableLootbox>();
                var bestDistance = 2.5f;
                foreach (var candidate in candidates)
                {
                    if (candidate == null || candidate.GetComponent<CoopCorpseMarker>() != null)
                        continue;
                    var distance = Vector3.Distance(candidate.transform.position, position);
                    if (distance < bestDistance)
                    {
                        bestDistance = distance;
                        nativeBox = candidate;
                    }
                }
            }
            if (nativeBox != null)
            {
                nativeBox.needInspect = true;
                nativeBox.hideIfEmpty = null;
                try
                {
                    nativeBox.Inventory?.DestroyAllContent();
                }
                catch (Exception ex)
                {
                    Log($"[corpse] native box clear failed id={info.ContainerId}: " +
                        ex.GetType().Name);
                }
                var nativeMarker = nativeBox.gameObject.AddComponent<CoopCorpseMarker>();
                nativeMarker.ContainerId = info.ContainerId;
                _corpseVisuals[info.ContainerId] = new CorpseVisual
                {
                    ContainerId = info.ContainerId,
                    Info = info,
                    Box = nativeBox,
                };
                Log($"[corpse] visual reused native dead box id={info.ContainerId} " +
                    $"player={info.PlayerId} scene={info.Scene} pos={nativeBox.transform.position}");
                return;
            }
            if (!isPlayerCorpse && _pendingAiDeadPrefabs.Count > 0)
            {
                var best = -1;
                var bestDistance = 2.0f;
                for (var i = 0; i < _pendingAiDeadPrefabs.Count; i++)
                {
                    var distance = Vector3.Distance(_pendingAiDeadPrefabs[i].Position, position);
                    if (distance < bestDistance)
                    {
                        bestDistance = distance;
                        best = i;
                    }
                }
                if (best >= 0)
                {
                    nativePrefab = _pendingAiDeadPrefabs[best].Prefab;
                    _pendingAiDeadPrefabs.RemoveAt(best);
                }
            }

            var prefab = nativePrefab ??
                         (GameplayDataSettings.Prefabs != null
                             ? (isPlayerCorpse
                                 ? GameplayDataSettings.Prefabs.LootBoxPrefab_Tomb
                                 : GameplayDataSettings.Prefabs.LootBoxPrefab)
                             : null);
            if (prefab == null && GameplayDataSettings.Prefabs != null)
                prefab = GameplayDataSettings.Prefabs.LootBoxPrefab;
            if (prefab == null)
            {
                Log($"[corpse] lootbox prefab missing id={info.ContainerId}");
                return;
            }

            try
            {
                var rotation = Quaternion.Euler(0f, info.Yaw, 0f);
                var box = UnityEngine.Object.Instantiate(prefab, position, rotation);
                if (box == null)
                    return;
                box.needInspect = !isPlayerCorpse;
                box.hideIfEmpty = null;
                var marker = box.gameObject.AddComponent<CoopCorpseMarker>();
                marker.ContainerId = info.ContainerId;
                _corpseVisuals[info.ContainerId] = new CorpseVisual
                {
                    ContainerId = info.ContainerId,
                    Info = info,
                    Box = box,
                };
                Log($"[corpse] visual created id={info.ContainerId} player={info.PlayerId} " +
                    $"kind={(isPlayerCorpse ? "player" : "ai")} native={nativePrefab != null} " +
                    $"scene={info.Scene} pos={position}");
            }
            catch (Exception ex)
            {
                Log($"[corpse] visual failed id={info.ContainerId}: " +
                    ex.GetType().Name + ": " + ex.Message);
            }
        }

        private void DestroyCorpseVisual(CorpseVisual visual)
        {
            if (visual == null)
                return;
            _corpseVisuals.Remove(visual.ContainerId);
            if (visual.Box != null)
                UnityEngine.Object.Destroy(visual.Box.gameObject);
            Log($"[corpse] visual removed id={visual.ContainerId}");
        }

        private void ClearCorpseVisuals(string reason)
        {
            _aiCorpseReadyAt.Clear();
            _pendingAiDeadPrefabs.Clear();
            if (_corpseVisuals.Count == 0)
                return;
            foreach (var visual in _corpseVisuals.Values)
            {
                if (visual.Box != null)
                    UnityEngine.Object.Destroy(visual.Box.gameObject);
            }
            _corpseVisuals.Clear();
            _pendingAiDeadPrefabs.Clear();
            Log($"[corpse] visuals cleared ({reason})");
        }
    }
}
