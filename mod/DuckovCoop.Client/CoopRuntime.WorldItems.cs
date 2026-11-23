using System;
using System.Collections.Generic;
using DuckovCoop.Client;
using HarmonyLib;
using ItemStatsSystem;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace DuckovCoopClient
{
    /// <summary>
    /// Marker attached to a mirrored native ground item. It is the only handle the pickup
    /// hooks use to distinguish server-owned items from native world objects; absent marker
    /// means "not ours" and the original interaction runs unchanged.
    /// </summary>
    public sealed class CoopWorldItemMarker : MonoBehaviour
    {
        public uint WorldItemId;
    }

    /// <summary>
    /// T6: mirrors server WorldItem messages into native InteractablePickup objects and turns
    /// native pickup into a CWorldItemPickup intent (no local inventory write until T4).
    /// </summary>
    public sealed partial class CoopRuntime
    {
        private sealed class WorldItemVisual
        {
            public uint WorldItemId;
            public WorldItemInfo Info;
            public Item Item;
            public DuckovItemAgent Agent;
            public InteractablePickup Pickup;
        }

        private const int MaxWorldItemCreatesPerUpdate = 4;
        private const float WorldItemReconcileInterval = 0.25f;
        private const float WorldItemStatusInterval = 0.5f;

        private readonly Dictionary<uint, WorldItemVisual> _worldItemVisuals =
            new Dictionary<uint, WorldItemVisual>();
        private readonly Dictionary<Item, uint> _worldItemByInstance =
            new Dictionary<Item, uint>();
        private readonly Queue<WorldItemInfo> _worldItemCreateQueue = new Queue<WorldItemInfo>();
        private readonly HashSet<uint> _worldItemQueued = new HashSet<uint>();
        private readonly HashSet<uint> _worldItemDeferredLogged = new HashSet<uint>();
        private readonly HashSet<uint> _deferredWorldPickups = new HashSet<uint>();
        private ulong _inventorySyncCorrelation;
        private float _nextBackpackSync;

        private bool _worldItemsLog;
        private float _nextWorldItemReconcile;
        private float _nextWorldItemStatus;
        private string _worldItemsLine = "[worlditems] mirror=0 chunks=0/0 pending=0 bpRev=0";
        private int _lastWorldItemPickupCount;
        private uint _worldItemPickupCorrelation;
        private int _lastWorldItemDropCount;
        private ulong _worldItemDropCorrelation;
        private ulong _pendingWorldItemDrop;
        private float _pendingWorldItemDropSince;

        private void UpdateWorldItems()
        {
            if (_client == null || _client.State != ClientState.InGame || !_inRaidScene)
            {
                ClearWorldItemVisuals("not in raid");
                UpdateWorldItemsStatus(force: true);
                return;
            }

            ProcessWorldItemPickupResults();
            ProcessWorldItemDropResults();
            RetryDeferredWorldPickups();

            if (_pendingWorldItemDrop != 0 &&
                Time.unscaledTime - _pendingWorldItemDropSince > 3f)
            {
                Log("[worlditems] drop result timeout; unblocking");
                _pendingWorldItemDrop = 0;
            }

            if (Time.unscaledTime >= _nextWorldItemReconcile)
            {
                _nextWorldItemReconcile = Time.unscaledTime + WorldItemReconcileInterval;
                ReconcileWorldItems();
            }

            ProcessWorldItemCreateQueue();
            UpdateWorldItemsStatus(force: false);
        }

        private void ReconcileWorldItems()
        {
            var activeScene = SceneManager.GetActiveScene().name;
            var removed = new List<uint>();
            foreach (var pair in _worldItemVisuals)
            {
                var info = pair.Value.Info;
                if (!_client.WorldItems.TryGetValue(pair.Key, out var current) ||
                    !IsWorldItemSceneVisible(info.Scene, activeScene) ||
                    !SameWorldItem(current, info))
                {
                    removed.Add(pair.Key);
                }
            }
            foreach (var worldItemId in removed)
            {
                if (_worldItemVisuals.TryGetValue(worldItemId, out var visual))
                    DestroyWorldItemVisual(visual);
            }

            foreach (var pair in _client.WorldItems)
            {
                if (_worldItemVisuals.ContainsKey(pair.Key) || _worldItemQueued.Contains(pair.Key))
                    continue;
                if (!IsWorldItemSceneVisible(pair.Value.Scene, activeScene))
                    continue;
                _worldItemCreateQueue.Enqueue(pair.Value);
                _worldItemQueued.Add(pair.Key);
            }
        }

        private void ProcessWorldItemCreateQueue()
        {
            var budget = MaxWorldItemCreatesPerUpdate;
            while (budget-- > 0 && _worldItemCreateQueue.Count > 0)
            {
                var queued = _worldItemCreateQueue.Dequeue();
                _worldItemQueued.Remove(queued.WorldItemId);

                if (_worldItemVisuals.ContainsKey(queued.WorldItemId))
                    continue;
                // The snapshot/spawn may have replaced this id while it sat in the queue.
                if (!_client.WorldItems.TryGetValue(queued.WorldItemId, out var current) ||
                    !SameWorldItem(current, queued))
                    continue;
                CreateWorldItemVisual(current);
            }
        }

        private void CreateWorldItemVisual(WorldItemInfo info)
        {
            try
            {
                var item = ItemAssetsCollection.InstantiateSync(unchecked((int)info.ItemId));
                if (item == null)
                {
                    Log($"[worlditems] item prefab missing itemId={info.ItemId} worldItemId={info.WorldItemId}");
                    return;
                }

                item.StackCount = info.Count > int.MaxValue ? int.MaxValue : (int)info.Count;
                item.FromInfoKey = "Ground";
                // Ground pickups use Item.Icon for the sprite graphic. The native ammo
                // prefab carries the white "cross" placeholder, so resolve the real family
                // icon before Drop() creates the pickup graphic.
                ApplyInventoryItemIcon(item, info.ItemUid);

                var position = new Vector3(info.X / 100f, info.Y / 100f, info.Z / 100f);
                // Match the native drop path: let the rigidbody settle the pickup on the
                // ground instead of placing its center at the player's feet (which buried it).
                position.y += 0.1f;
                if (Physics.Raycast(position + Vector3.up * 3f, Vector3.down, out var ground,
                                    6f, ~0, QueryTriggerInteraction.Ignore))
                    position.y = Mathf.Max(position.y, ground.point.y + 0.08f);
                var agent = item.Drop(position, true, Vector3.forward, 360f);
                if (agent == null)
                {
                    Log($"[worlditems] drop failed itemId={info.ItemId} worldItemId={info.WorldItemId}");
                    UnityEngine.Object.Destroy(item.gameObject);
                    return;
                }

                agent.transform.rotation = Quaternion.Euler(0f, info.Yaw, 0f);
                var pickup = agent.GetComponent<InteractablePickup>();
                if (pickup == null)
                {
                    Log($"[worlditems] pickup missing itemId={info.ItemId} worldItemId={info.WorldItemId}");
                    UnityEngine.Object.Destroy(agent.gameObject);
                    UnityEngine.Object.Destroy(item.gameObject);
                    return;
                }

                var marker = agent.gameObject.AddComponent<CoopWorldItemMarker>();
                marker.WorldItemId = info.WorldItemId;

                // Native item-specific pickup prefabs do not always build their graphic
                // until the item is dropped through the inventory path. A server-created
                // ground item therefore gets the interaction collider but no visible model.
                // Create the graphic explicitly when the pickup has none.
                var graphicRoot = pickup.graphicRoot != null ? pickup.graphicRoot : pickup.transform;
                var renderers = graphicRoot.GetComponentsInChildren<Renderer>(true);
                var hasSprite = graphicRoot.GetComponentInChildren<SpriteRenderer>(true) != null;
                if (renderers.Length == 0 || (item.useSpriteForPickup && !hasSprite))
                {
                    pickup.CreateGraphic();
                    Log($"[worlditems] graphic created id={info.WorldItemId} item={info.ItemId} " +
                        $"sprite={item.useSpriteForPickup}");
                }

                _worldItemVisuals[info.WorldItemId] = new WorldItemVisual
                {
                    WorldItemId = info.WorldItemId,
                    Info = info,
                    Item = item,
                    Agent = agent,
                    Pickup = pickup,
                };
                _worldItemByInstance[item] = info.WorldItemId;

                if (_worldItemsLog)
                {
                    Log($"[worlditems] mirror+ id={info.WorldItemId} item={info.ItemId} " +
                        $"count={info.Count} pos={position} scene={info.Scene}");
                }
            }
            catch (Exception ex)
            {
                Log($"[worlditems] create failed id={info.WorldItemId} item={info.ItemId}: " +
                    ex.GetType().Name + ": " + ex.Message);
            }
        }

        private void DestroyWorldItemVisual(WorldItemVisual visual)
        {
            if (visual == null)
                return;

            _worldItemVisuals.Remove(visual.WorldItemId);
            if (visual.Item != null)
                _worldItemByInstance.Remove(visual.Item);

            if (visual.Agent != null)
                UnityEngine.Object.Destroy(visual.Agent.gameObject);
            if (visual.Item != null &&
                (visual.Agent == null || visual.Item.gameObject != visual.Agent.gameObject))
                UnityEngine.Object.Destroy(visual.Item.gameObject);

            _worldItemDeferredLogged.Remove(visual.WorldItemId);
            if (_worldItemsLog)
                Log($"[worlditems] mirror- id={visual.WorldItemId} item={visual.Info?.ItemId}");
        }

        private void ClearWorldItemVisuals(string reason)
        {
            if (_worldItemVisuals.Count == 0 && _worldItemCreateQueue.Count == 0 &&
                _pendingWorldItemDrop == 0)
                return;

            foreach (var visual in new List<WorldItemVisual>(_worldItemVisuals.Values))
                DestroyWorldItemVisual(visual);
            _worldItemVisuals.Clear();
            _worldItemByInstance.Clear();
            _worldItemCreateQueue.Clear();
            _worldItemQueued.Clear();
            _worldItemDeferredLogged.Clear();
            _deferredWorldPickups.Clear();
            _pendingWorldItemDrop = 0;
            Log($"[worlditems] cleared ({reason})");
        }

        private void RetryDeferredWorldPickups()
        {
            if (_deferredWorldPickups.Count == 0 || _client.BackpackRevision == 0)
                return;
            var ids = new List<uint>(_deferredWorldPickups);
            _deferredWorldPickups.Clear();
            foreach (var id in ids)
                RequestWorldItemPickup(id);
        }

        private void ProcessWorldItemPickupResults()
        {
            if (_client.WorldItemPickupCount == _lastWorldItemPickupCount)
                return;

            _lastWorldItemPickupCount = _client.WorldItemPickupCount;
            var result = _client.LastWorldItemPickup;
            if (result == null)
                return;

            var success = result.Code == WorldItemMirrorState.PickupOk ||
                          result.Code == WorldItemMirrorState.PickupIdempotentReplay;
            var worldItemId = _client.WorldItemMirror.LastPickupWorldItemId;
            Log($"[worlditems] pickup result: code={result.Code} uid={result.ItemUid} " +
                $"bpRev={result.BackpackRevision} worldItemId={worldItemId}");

            // Success already removed the item from the mirror; destroy now rather than
            // waiting for the next reconciliation so the visual disappears immediately.
            if (success && _worldItemVisuals.TryGetValue(worldItemId, out var visual))
                DestroyWorldItemVisual(visual);
        }

        private void ProcessWorldItemDropResults()
        {
            if (_client == null || _client.WorldItemDropCount == _lastWorldItemDropCount)
                return;

            _lastWorldItemDropCount = _client.WorldItemDropCount;
            var result = _client.LastWorldItemDrop;
            if (result == null)
                return;

            var ours = result.CorrelationId == _pendingWorldItemDrop;
            if (ours)
                _pendingWorldItemDrop = 0;

            Log($"[worlditems] drop result: corr={result.CorrelationId} code={result.Code} " +
                $"worldItemId={result.WorldItemId} srcRev={result.SourceRevision}");

            // Success requires no local mutation: the server broadcast SWorldItemSpawn and
            // sent the source SInventorySnapshot before this result. The T6 visual reconciler
            // and the per-kind inventory apply loop consume both.
        }

        private void UpdateWorldItemsStatus(bool force)
        {
            if (!force && Time.unscaledTime < _nextWorldItemStatus)
                return;
            _nextWorldItemStatus = Time.unscaledTime + WorldItemStatusInterval;

            var mirror = _client != null ? _client.WorldItemMirror : null;
            _worldItemsLine = mirror == null
                ? "[worlditems] mirror=0 chunks=0/0 pending=0 bpRev=0"
                : $"[worlditems] mirror={_worldItemVisuals.Count} " +
                  $"chunks={mirror.ReceivedChunkCount}/{mirror.SnapshotChunkCount} " +
                  $"pending={mirror.PendingPickupCount} bpRev={mirror.BackpackRevision}";
        }

        private static bool SameWorldItem(WorldItemInfo a, WorldItemInfo b)
        {
            return a != null && b != null &&
                   a.WorldItemId == b.WorldItemId &&
                   a.ItemUid == b.ItemUid &&
                   a.ItemId == b.ItemId &&
                   a.Count == b.Count &&
                   a.X == b.X && a.Y == b.Y && a.Z == b.Z &&
                   a.Yaw == b.Yaw &&
                   string.Equals(a.Scene, b.Scene, StringComparison.Ordinal);
        }

        private static bool IsWorldItemSceneVisible(string scene, string activeScene)
        {
            if (string.IsNullOrEmpty(scene))
                return false;

            var loaded = SceneManager.GetSceneByName(scene);
            if (loaded.IsValid() && loaded.isLoaded)
                return true;

            // scenes.xml aliases primary/main scenes to the same level (GroundZero Main/_1).
            return !string.IsNullOrEmpty(activeScene) &&
                   SceneGroup(scene) == SceneGroup(activeScene);
        }

        private static string SceneGroup(string scene)
        {
            if (string.IsNullOrEmpty(scene))
                return string.Empty;
            if (scene.EndsWith("_Main", StringComparison.Ordinal))
                return scene.Substring(0, scene.Length - 5);

            var index = scene.LastIndexOf('_');
            if (index > 0 && int.TryParse(scene.Substring(index + 1), out _))
                return scene.Substring(0, index);
            return scene;
        }

        /// <summary>True for an Item owned by the T6 ground-item mirror (pickup stays T6's job).</summary>
        internal bool IsWorldItemMirrorItem(Item item)
        {
            if (item == null)
                return false;
            if (_worldItemByInstance.ContainsKey(item))
                return true;
            return item.GetComponentInParent<CoopWorldItemMarker>() != null;
        }

        /// <summary>Harmony prefix for InteractablePickup.OnInteractStart (true = intercept).</summary>
        internal bool TryInterceptWorldItemPickup(InteractablePickup pickup)
        {
            var marker = pickup != null ? pickup.GetComponent<CoopWorldItemMarker>() : null;
            if (marker == null)
                return false;

            RequestWorldItemPickup(marker.WorldItemId);
            return true;
        }

        /// <summary>Harmony prefix for CharacterMainControl.PickupItem (true = intercept).</summary>
        internal bool TryInterceptWorldItemPickup(Item item)
        {
            if (item == null)
                return false;

            if (!_worldItemByInstance.TryGetValue(item, out var worldItemId))
            {
                var marker = item.GetComponentInParent<CoopWorldItemMarker>();
                if (marker == null)
                    return false;
                worldItemId = marker.WorldItemId;
            }

            RequestWorldItemPickup(worldItemId);
            return true;
        }

        /// <summary>
        /// T6b: Harmony prefix for the 3-argument <see cref="ItemExtensions.Drop"/> used by the
        /// inventory drop button and the equipment fast-drop button. Server-owned items are
        /// removed by the server and re-created as ground WorldItems; the native Drop (Detach +
        /// local pickup agent) must be skipped, otherwise the client would both duplicate the
        /// item and hit the T4 RemoveItem guard. Native items keep the vanilla path.
        /// Returns true when the native drop must be skipped.
        /// </summary>
        internal bool TryInterceptItemDrop(Item item, CharacterMainControl character)
        {
            if (item == null || !_inventoryMirrorActive || _applyingServerInventory)
                return false;
            if (_client == null || _client.State != ClientState.InGame || !_inRaidScene)
                return false;

            // Native item (no serverUid): keep the vanilla Drop.
            if (!_serverUidByItem.TryGetValue(item, out var itemUid))
                return false;
            // A mirrored ground item is not a player-inventory item; never intercept it.
            if (IsWorldItemMirrorItem(item))
                return false;

            if (_pendingWorldItemDrop != 0)
            {
                LogBlockedNativeMutation("drop pending; native drop skipped");
                return true;
            }

            if (!TryMapItemLocation(item, out var sourceKind, out var sourceSlot) || sourceKind > 2)
            {
                LogBlockedNativeMutation($"drop unmapped server item={DescribeItem(item)}");
                return true;
            }

            var revision = _client.PlayerInventoryRevision(sourceKind);
            if (revision == 0)
            {
                LogBlockedNativeMutation($"drop missing revision kind={sourceKind}");
                return true;
            }

            var scene = SceneManager.GetActiveScene().name;
            if (string.IsNullOrEmpty(scene))
            {
                LogBlockedNativeMutation("drop missing active scene");
                return true;
            }

            var main = CharacterMainControl.Main;
            var source = character != null ? character : main;
            if (source == null)
            {
                LogBlockedNativeMutation("drop missing character");
                return true;
            }

            var count = item.StackCount > 0 ? (uint)item.StackCount : 0u;
            if (count == 0)
            {
                LogBlockedNativeMutation($"drop zero-count item={DescribeItem(item)}");
                return true;
            }

            // Native drops at the character position; put the world item slightly in front so
            // the shared pickup visual does not sit inside the body. Keep the offset horizontal
            // even when the character is looking up/down.
            var forward = source.transform.forward;
            forward.y = 0f;
            if (forward.sqrMagnitude < 0.0001f)
                forward = Vector3.forward;
            else
                forward.Normalize();
            var position = source.transform.position + forward * 0.5f;
            var yaw = Mathf.Repeat(source.transform.eulerAngles.y, 360f);
            var correlation = ++_worldItemDropCorrelation;
            var sent = _client.SendWorldItemDrop(correlation, sourceKind, _client.PlayerId,
                (ushort)sourceSlot, itemUid, count, scene,
                Mathf.RoundToInt(position.x * 100f), Mathf.RoundToInt(position.y * 100f),
                Mathf.RoundToInt(position.z * 100f), (ushort)Mathf.RoundToInt(yaw), revision);
            if (!sent)
            {
                LogBlockedNativeMutation("drop send failed");
                return true;
            }

            _pendingWorldItemDrop = correlation;
            _pendingWorldItemDropSince = Time.unscaledTime;
            Log($"[worlditems] drop intent: corr={correlation} src={sourceKind}:{sourceSlot} " +
                $"uid={itemUid} item={item.TypeID} count={count} scene={scene}");
            return true;
        }

        private void RequestWorldItemPickup(uint worldItemId)
        {
            if (_client == null || _client.State != ClientState.InGame)
                return;
            if (!_worldItemVisuals.TryGetValue(worldItemId, out var visual))
                return;

            if (_client.BackpackRevision == 0)
            {
                _deferredWorldPickups.Add(worldItemId);
                if (_worldItemDeferredLogged.Add(worldItemId))
                    Log($"[worlditems] pickup deferred: backpack revision unknown id={worldItemId}");
                if (Time.unscaledTime >= _nextBackpackSync)
                {
                    _nextBackpackSync = Time.unscaledTime + 1.0f;
                    if (_client.SendInventorySync(++_inventorySyncCorrelation, 1,
                            _client.PlayerId))
                    {
                        Log("[worlditems] requested backpack snapshot recovery");
                    }
                }
                return;
            }

            if (_client.WorldItemMirror.IsPickupPending(worldItemId))
                return;

            var info = _client.WorldItems.TryGetValue(worldItemId, out var current)
                ? current
                : visual.Info;
            var correlation = ++_worldItemPickupCorrelation;
            var sent = _client.SendWorldItemPickup(correlation, worldItemId, info.ItemUid,
                info.ItemId, info.Count);
            if (sent)
            {
                Log($"[worlditems] pickup intent: corr={correlation} id={worldItemId} " +
                    $"uid={info.ItemUid} item={info.ItemId} count={info.Count}");
            }
            else
            {
                Log($"[worlditems] pickup intent rejected locally: id={worldItemId} " +
                    $"bpRev={_client.BackpackRevision} pending={_client.WorldItemMirror.PendingPickupCount}");
            }
        }
    }

    /// <summary>
    /// Manual Harmony patches (same reason as SpawnGuard: other probes PatchAll the assembly).
    /// Both prefixes only intercept items carrying a <see cref="CoopWorldItemMarker"/>;
    /// every other interaction/pickup returns true and keeps native behaviour.
    /// </summary>
    internal static class WorldItemPickupGuard
    {
        private const string HarmonyId = "duckovcoop.worlditems";
        private static Harmony _harmony;

        internal static void Init()
        {
            if (_harmony != null)
                return;
            try
            {
                _harmony = new Harmony(HarmonyId);

                var interact = AccessTools.Method(typeof(InteractablePickup), "OnInteractStart");
                if (interact != null)
                {
                    _harmony.Patch(interact,
                        prefix: new HarmonyMethod(typeof(WorldItemPickupGuard),
                            nameof(BlockInteractPickup)));
                }
                else
                {
                    CoopRuntime.Log("[worlditems] InteractablePickup.OnInteractStart not found");
                }

                var nativePickup = AccessTools.Method(typeof(CharacterMainControl), "PickupItem");
                if (nativePickup != null)
                {
                    _harmony.Patch(nativePickup,
                        prefix: new HarmonyMethod(typeof(WorldItemPickupGuard),
                            nameof(BlockNativePickup)));
                }
                else
                {
                    CoopRuntime.Log("[worlditems] CharacterMainControl.PickupItem not found");
                }

                var drop = AccessTools.Method(typeof(ItemExtensions), "Drop",
                    new[] { typeof(Item), typeof(CharacterMainControl), typeof(bool) });
                if (drop != null)
                {
                    _harmony.Patch(drop,
                        prefix: new HarmonyMethod(typeof(WorldItemPickupGuard),
                            nameof(BlockItemDrop)));
                }
                else
                {
                    CoopRuntime.Log(
                        "[worlditems] ItemExtensions.Drop(Item, CharacterMainControl, bool) not found");
                }

                CoopRuntime.Log("[worlditems] pickup guards installed");
            }
            catch (Exception ex)
            {
                CoopRuntime.Log($"[worlditems] guard failed: {ex.GetType().Name}: {ex.Message}");
            }
        }

        private static bool BlockInteractPickup(InteractablePickup __instance)
        {
            var runtime = CoopRuntime.Active;
            return runtime == null || !runtime.TryInterceptWorldItemPickup(__instance);
        }

        private static bool BlockNativePickup(Item item)
        {
            var runtime = CoopRuntime.Active;
            return runtime == null || !runtime.TryInterceptWorldItemPickup(item);
        }

        private static bool BlockItemDrop(Item item, CharacterMainControl character,
                                          bool createRigidbody)
        {
            var runtime = CoopRuntime.Active;
            return runtime == null || !runtime.TryInterceptItemDrop(item, character);
        }
    }
}
