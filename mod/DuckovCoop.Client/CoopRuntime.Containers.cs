using System;
using System.Collections.Generic;
using Duckov.UI;
using Duckov.Utilities;
using DuckovCoop.Client;
using HarmonyLib;
using ItemStatsSystem;
using UnityEngine;
using UnityEngine.EventSystems;
using UIInventoryEntry = Duckov.UI.InventoryEntry;

namespace DuckovCoopClient
{
    /// <summary>
    /// T5 phase 2: use the native InteractableLootbox/LootView as a display proxy for the
    /// server-owned container. The native LootBoxLoader roll is suppressed, the proxy inventory
    /// is rebuilt from ContainerMirrorState, and the native take/put double-click paths are
    /// routed through CItemTransfer instead of mutating the proxy/player inventories locally.
    /// </summary>
    public sealed partial class CoopRuntime
    {
        private InteractableLootbox _openContainerLootbox;
        private uint _openContainerId;
        private byte _openContainerKind = ContainerMirrorState.MapContainerKind;
        private uint _openContainerRevision;
        private bool _openContainerPopulated;
        private ulong _containerOpenCorrelation;
        private readonly Dictionary<uint, Item> _containerProxyItems =
            new Dictionary<uint, Item>();
        private readonly Dictionary<Item, uint> _containerProxyUids =
            new Dictionary<Item, uint>();
        private readonly HashSet<uint> _unmappedLootboxLogged = new HashSet<uint>();
        private readonly Dictionary<uint, HashSet<uint>> _inspectedByContainer =
            new Dictionary<uint, HashSet<uint>>();
        private HashSet<uint> _locallyInspected = new HashSet<uint>();
        private bool _populatingProxy;
        private bool _openContainerNeedsInspect;

        private sealed class PickAllEntry
        {
            public ushort SourceSlot;
            public ushort TargetSlot;
            public uint ItemUid;
            public uint ItemId;
            public uint Count;
        }

        private readonly List<PickAllEntry> _pickAllQueue = new List<PickAllEntry>();
        private bool _pickAllActive;
        private bool _pickAllAwaitingResult;
        private uint _pickAllContainerId;
        private byte _pickAllKind;
        private uint _pickAllLastRevision;
        private float _pickAllSince;

        private void UpdateContainerViews()
        {
            UpdateContainerPickAll();
            if (_client == null || !_inRaidScene || _openContainerId == 0)
                return;
            if (_openContainerLootbox == null)
            {
                OnContainerViewClosed();
                return;
            }
            if (!_client.ContainerMirror.TryGet(_openContainerId, out var view))
                return;
            if (_openContainerPopulated && view.Revision == _openContainerRevision)
                return;

            PopulateContainerProxy(_openContainerLootbox, _openContainerId, view);
            _openContainerRevision = view.Revision;
            _openContainerPopulated = true;
        }

        /// <summary>Called from LootView.OnStartLoot before the native view opens.</summary>
        internal bool TryPrepareContainerView(InteractableLootbox lootbox)
        {
            if (_client == null || !_inRaidScene || lootbox == null)
                return true;

            var marker = lootbox.GetComponent<CoopCorpseMarker>();
            var containerId = marker != null && marker.ContainerId != 0
                ? marker.ContainerId
                : FindContainerIdForLootbox(lootbox);
            if (containerId == 0)
            {
                var key = lootbox.GetInstanceID();
                if (_unmappedLootboxLogged.Add(unchecked((uint)key)))
                    Log($"[container] unmapped native lootbox '{lootbox.name}' at " +
                        $"{lootbox.transform.position}; native contents left as-is");
                return true;
            }

            _openContainerKind = marker != null
                ? ContainerMirrorState.CorpseContainerKind
                : ContainerMirrorState.MapContainerKind;
            _openContainerNeedsInspect = lootbox.needInspect;
            if (!_inspectedByContainer.TryGetValue(containerId, out var inspected))
            {
                inspected = new HashSet<uint>();
                _inspectedByContainer[containerId] = inspected;
            }
            _locallyInspected = inspected;
            _openContainerLootbox = lootbox;
            _openContainerId = containerId;
            _openContainerPopulated = false;
            _openContainerRevision = 0;

            _client.SendContainerOpen(++_containerOpenCorrelation, containerId);

            if (_client.ContainerMirror.TryGet(containerId, out var view))
            {
                PopulateContainerProxy(lootbox, containerId, view);
                _openContainerRevision = view.Revision;
                _openContainerPopulated = true;
            }
            else
            {
                ClearContainerProxy(lootbox);
            }
            Log($"[container] open id={containerId} name='{lootbox.name}' " +
                $"entries={view?.Entries.Count ?? 0}");
            return true;
        }

        internal void OnContainerViewClosed()
        {
            CancelContainerPickAll("container closed");
            if (_openContainerId != 0)
                Log($"[container] close id={_openContainerId}");
            _openContainerLootbox = null;
            _openContainerId = 0;
            _openContainerKind = ContainerMirrorState.MapContainerKind;
            _openContainerRevision = 0;
            _openContainerPopulated = false;
            _openContainerNeedsInspect = false;
            _locallyInspected = new HashSet<uint>();
        }

        private void ClearContainerInspectionState()
        {
            _inspectedByContainer.Clear();
            _locallyInspected = new HashSet<uint>();
        }

        internal bool ContainerViewActive => _openContainerId != 0;

        /// <summary>
        /// Native "pick all" -> a serial queue of CItemTransfer intents. Target slots are
        /// reserved up front so the native backpack snapshot can lag without duplicating a slot.
        /// </summary>
        internal void BeginContainerPickAll()
        {
            if (_client == null || _openContainerId == 0)
                return;
            CancelContainerPickAll("restart");
            if (!_client.ContainerMirror.TryGet(_openContainerId, out var view))
            {
                LogBlockedNativeMutation("pick-all: container view missing");
                return;
            }
            if (_client.PlayerInventoryRevision(1) == 0)
            {
                LogBlockedNativeMutation("pick-all: backpack revision not ready");
                return;
            }

            var freeSlots = new List<ushort>();
            var mirror = _client.InventoryMirror;
            for (var slot = 0; slot < _client.InventoryMirror.CapacityOfKind(1); slot++)
            {
                if (!SlotOccupied(mirror, 1, slot))
                    freeSlots.Add((ushort)slot);
            }

            var freeIndex = 0;
            var skipped = 0;
            foreach (var entry in view.Entries)
            {
                if (entry.Count == 0 || entry.ItemUid == 0)
                {
                    skipped++;
                    continue;
                }
                if (_openContainerNeedsInspect && !_locallyInspected.Contains(entry.ItemUid))
                {
                    skipped++;
                    continue;
                }
                if (freeIndex >= freeSlots.Count)
                    break;
                _pickAllQueue.Add(new PickAllEntry
                {
                    SourceSlot = (ushort)entry.Slot,
                    TargetSlot = freeSlots[freeIndex++],
                    ItemUid = entry.ItemUid,
                    ItemId = entry.ItemId,
                    Count = entry.Count,
                });
            }

            _pickAllActive = _pickAllQueue.Count > 0;
            _pickAllAwaitingResult = false;
            _pickAllContainerId = _openContainerId;
            _pickAllKind = _openContainerKind;
            _pickAllLastRevision = view.Revision;
            _pickAllSince = Time.unscaledTime;
            _interaction = $"container: pick all {_pickAllQueue.Count}";
            Log($"[container] pick-all queued={_pickAllQueue.Count} skipped={skipped} " +
                $"freeSlots={freeSlots.Count} id={_openContainerId} kind={_openContainerKind}");
        }

        private void UpdateContainerPickAll()
        {
            if (!_pickAllActive)
                return;
            if (_client == null || _openContainerId == 0 ||
                _openContainerId != _pickAllContainerId || _openContainerKind != _pickAllKind)
            {
                CancelContainerPickAll("container changed");
                return;
            }
            if (_pickAllAwaitingResult || _pendingItemTransfer != 0 || _awaitingInventorySnapshot)
                return;
            if (!_client.ContainerMirror.TryGet(_pickAllContainerId, out var view))
                return;
            if (view.Revision == _pickAllLastRevision)
            {
                if (Time.unscaledTime - _pickAllSince > 1.5f)
                    CancelContainerPickAll("source snapshot timeout");
                return;
            }
            if (_pickAllQueue.Count == 0)
            {
                Log("[container] pick-all complete");
                CancelContainerPickAll("complete");
                return;
            }

            var entry = _pickAllQueue[0];
            if (!ContainsEntry(view, entry))
            {
                LogBlockedNativeMutation(
                    $"pick-all: source vanished slot={entry.SourceSlot} uid={entry.ItemUid}");
                _pickAllQueue.RemoveAt(0);
                _pickAllLastRevision = view.Revision;
                return;
            }

            var correlation = ++_itemTransferCorrelation;
            var sent = _client.SendItemTransfer(correlation, _pickAllKind, _pickAllContainerId,
                entry.SourceSlot, 1, _client.PlayerId, entry.TargetSlot, entry.ItemUid,
                entry.Count, entry.ItemId, view.Revision);
            if (!sent)
            {
                CancelContainerPickAll("send failed");
                return;
            }

            _pickAllQueue.RemoveAt(0);
            _pendingItemTransfer = correlation;
            _pendingTransferSince = Time.unscaledTime;
            _pickAllAwaitingResult = true;
            _pickAllLastRevision = view.Revision;
            _pickAllSince = Time.unscaledTime;
            Log($"[container] pick-all take: corr={correlation} slot={entry.SourceSlot} " +
                $"dst={entry.TargetSlot} uid={entry.ItemUid} left={_pickAllQueue.Count}");
        }

        internal void CancelContainerPickAll(string reason)
        {
            if (_pickAllActive)
                Log($"[container] pick-all cancelled ({reason})");
            _pickAllActive = false;
            _pickAllAwaitingResult = false;
            _pickAllQueue.Clear();
        }

        private static bool SlotOccupied(InventoryMirrorState mirror, byte kind, int slot)
        {
            foreach (var entry in mirror.Entries)
            {
                if (entry.Kind == kind && entry.Slot == slot)
                    return true;
            }
            return false;
        }

        private static bool ContainsEntry(ContainerMirrorState.View view, PickAllEntry entry)
        {
            foreach (var candidate in view.Entries)
            {
                if (candidate.Slot == entry.SourceSlot && candidate.ItemUid == entry.ItemUid)
                    return true;
            }
            return false;
        }

        private void PopulateContainerProxy(InteractableLootbox lootbox, uint containerId,
                                            ContainerMirrorState.View view)
        {
            if (lootbox == null || view == null)
                return;
            var inventory = lootbox.Inventory;
            if (inventory == null)
            {
                Log($"[container] proxy inventory missing id={containerId}");
                return;
            }

            _applyingServerInventory = true;
            _populatingProxy = true;
            try
            {
                ClearContainerProxyItems(inventory);
                var capacity = Math.Max((int)view.Capacity, view.Entries.Count);
                if (capacity > 0 && inventory.Capacity != capacity)
                    inventory.SetCapacity(capacity);
                // D5: search/inspection is local presentation. Native LootView refuses to move
                // an uninspected item when the lootbox needs inspection; the proxy keeps that
                // flag and remembers which uids this player has already inspected.
                inventory.NeedInspection = _openContainerNeedsInspect;
                // Forcing this true made the native loot view believe the box was already
                // searched, so the "unsearched" rows could never be inspected on the first open
                // (they only cleared after reopening the box). Leave it false while the box
                // still needs a search so the native search flow runs.
                inventory.hasBeenInspectedInLootBox = !_openContainerNeedsInspect;
                Log($"[container] inspect state id={containerId} needInspect={_openContainerNeedsInspect} " +
                    $"locallyInspected={_locallyInspected.Count} entries={view.Entries.Count}");

                foreach (var entry in view.Entries)
                {
                    var item = ItemAssetsCollection.InstantiateSync(unchecked((int)entry.ItemId));
                    if (item == null)
                    {
                        Log($"[container] item prefab missing item={entry.ItemId} uid={entry.ItemUid}");
                        continue;
                    }
                    item.StackCount = entry.Count > int.MaxValue ? int.MaxValue : (int)entry.Count;
                    ApplyInventoryItemIcon(item, entry.ItemUid);
                    if (!inventory.AddAt(item, entry.Slot))
                    {
                        item.DestroyTree();
                        Log($"[container] proxy AddAt failed slot={entry.Slot} " +
                            $"item={entry.ItemId} uid={entry.ItemUid}");
                        continue;
                    }

                    item.Inspected = !_openContainerNeedsInspect ||
                                     _locallyInspected.Contains(entry.ItemUid);
                    item.onInspectionStateChanged += OnProxyItemInspectionChanged;
                    ApplyItemDetailFromServer(item, entry.ItemUid);
                    _containerProxyItems[entry.ItemUid] = item;
                    _containerProxyUids[item] = entry.ItemUid;
                    _serverUidByItem[item] = entry.ItemUid;
                }
            }
            catch (Exception ex)
            {
                Log($"[container] populate failed id={containerId}: " +
                    ex.GetType().Name + ": " + ex.Message);
            }
            finally
            {
                _populatingProxy = false;
                _applyingServerInventory = false;
            }
        }

        private void OnProxyItemInspectionChanged(Item item)
        {
            if (_populatingProxy || item == null || !item.Inspected)
                return;
            if (_containerProxyUids.TryGetValue(item, out var uid))
                _locallyInspected.Add(uid);
        }

        private void ClearContainerProxy(InteractableLootbox lootbox)
        {
            var inventory = lootbox != null ? lootbox.Inventory : null;
            if (inventory == null)
                return;
            var wasApplying = _applyingServerInventory;
            _applyingServerInventory = true;
            try
            {
                ClearContainerProxyItems(inventory);
            }
            finally
            {
                _applyingServerInventory = wasApplying;
            }
        }

        private void ClearContainerProxyItems(Inventory inventory)
        {
            if (inventory == null)
                return;
            var items = new List<Item>();
            foreach (var item in inventory)
            {
                if (item != null)
                    items.Add(item);
            }
            foreach (var item in items)
            {
                inventory.RemoveItem(item);
                if (item != null)
                {
                    if (_containerProxyUids.TryGetValue(item, out var uid))
                    {
                        _containerProxyUids.Remove(item);
                        _containerProxyItems.Remove(uid);
                        if (_serverUidByItem.TryGetValue(item, out var mapped) && mapped == uid)
                            _serverUidByItem.Remove(item);
                    }
                    item.DestroyTree();
                }
            }
            _containerProxyItems.Clear();
            _containerProxyUids.Clear();
        }

        /// <summary>Native double-click on a container row: route a take to the server.</summary>
        internal bool TryRouteContainerTake(UIInventoryEntry entry)
        {
            if (_openContainerId == 0 || entry == null || _client == null)
                return false;
            var item = entry.Content;
            if (item == null)
                return false;
            if (!_serverUidByItem.TryGetValue(item, out var uid))
            {
                // The proxy should only contain server-owned items; never move a local-only
                // item into the authoritative player inventory.
                LogBlockedNativeMutation($"container take: non-server item={DescribeItem(item)}");
                return true;
            }
            if (item.NeedInspection)
            {
                // D5: let native LootView handle the inspect click; the item becomes movable
                // once the local inspection flag flips.
                Log($"[container] take deferred: uninspected uid={uid}");
                return false;
            }
            _locallyInspected.Add(uid);
            if (_pendingItemTransfer != 0 || _awaitingInventorySnapshot)
            {
                LogBlockedNativeMutation("container take: transfer pending");
                return true;
            }

            var targetSlot = _client.InventoryMirror.FindFirstFreeSlot(1);
            if (targetSlot < 0)
            {
                _interaction = "inventory: backpack full";
                LogBlockedNativeMutation($"container take: backpack full item={DescribeItem(item)}");
                return true;
            }
            if (!_client.ContainerMirror.TryGet(_openContainerId, out var view))
            {
                LogBlockedNativeMutation("container take: view missing");
                return true;
            }

            var correlation = ++_itemTransferCorrelation;
            var sent = _client.SendItemTransfer(correlation,
                _openContainerKind, _openContainerId, (ushort)entry.Index,
                1, _client.PlayerId, (ushort)targetSlot, uid,
                item.StackCount > 0 ? (uint)item.StackCount : 1u,
                item.TypeID > 0 ? (uint)item.TypeID : 0u, view.Revision);
            if (!sent)
            {
                LogBlockedNativeMutation("container take: send failed");
                return true;
            }

            _pendingItemTransfer = correlation;
            _pendingTransferSince = Time.unscaledTime;
            Log($"[container] take intent: corr={correlation} id={_openContainerId} " +
                $"slot={entry.Index} uid={uid} item={item.TypeID}");
            return true;
        }

        /// <summary>Native double-click on a player backpack/pet row: route a put.</summary>
        internal bool TryRouteContainerPut(UIInventoryEntry entry)
            => entry != null && TryRouteContainerPutItem(entry.Content);

        /// <summary>Native double-click on an equipment slot: route a put.</summary>
        internal bool TryRouteContainerSlotPut(SlotDisplay display)
            => display != null && display.Target != null &&
               TryRouteContainerPutItem(display.Target.Content);

        private bool TryRouteContainerPutItem(Item item)
        {
            if (_openContainerId == 0 || item == null || _client == null)
                return false;
            if (!_serverUidByItem.TryGetValue(item, out var uid))
            {
                if (TryMapItemLocation(item, out _, out _))
                {
                    LogBlockedNativeMutation(
                        $"container put: native item={DescribeItem(item)}");
                    return true;
                }
                return false;
            }
            if (_pendingItemTransfer != 0 || _awaitingInventorySnapshot)
            {
                LogBlockedNativeMutation("container put: transfer pending");
                return true;
            }
            if (_openContainerKind != ContainerMirrorState.MapContainerKind)
            {
                LogBlockedNativeMutation("container put: corpse containers are take-only");
                return true;
            }
            if (!TryMapItemLocation(item, out var sourceKind, out var sourceSlot))
                return false;
            // The item was visible/searchable in this container before; remember it so the
            // rebuilt proxy marks it inspected instead of showing "unsearched item" again.
            _locallyInspected.Add(uid);
            if (!_client.ContainerMirror.TryGet(_openContainerId, out var view))
            {
                LogBlockedNativeMutation("container put: view missing");
                return true;
            }

            var targetSlot = FindContainerPutSlot(view, item, out var count);
            if (targetSlot < 0)
            {
                _interaction = "container: full";
                LogBlockedNativeMutation($"container put: full item={DescribeItem(item)}");
                return true;
            }

            var correlation = ++_itemTransferCorrelation;
            var sent = _client.SendItemTransfer(correlation, sourceKind, _client.PlayerId,
                (ushort)sourceSlot, _openContainerKind, _openContainerId,
                (ushort)targetSlot, uid, count, item.TypeID > 0 ? (uint)item.TypeID : 0u,
                _client.InventoryMirror.GetRevision(sourceKind));
            if (!sent)
            {
                LogBlockedNativeMutation("container put: send failed");
                return true;
            }

            _pendingItemTransfer = correlation;
            _pendingTransferSince = Time.unscaledTime;
            Log($"[container] put intent: corr={correlation} id={_openContainerId} " +
                $"src={sourceKind}:{sourceSlot} dst={targetSlot} uid={uid} " +
                $"count={count} item={item.TypeID}");
            return true;
        }

        private static int FindContainerPutSlot(ContainerMirrorState.View view, Item item,
                                                out uint count)
        {
            count = item != null && item.StackCount > 0 ? (uint)item.StackCount : 1u;
            if (view == null)
                return -1;

            var used = new HashSet<int>();
            foreach (var entry in view.Entries)
            {
                used.Add(entry.Slot);
                if (entry.ItemId != (uint)item.TypeID || item.MaxStackCount <= 1)
                    continue;
                var existing = item.MaxStackCount - (int)Math.Min(entry.Count, (uint)int.MaxValue);
                if (existing <= 0)
                    continue;
                count = (uint)Math.Min((int)count, existing);
                return entry.Slot;
            }

            for (var slot = 0; slot < view.Capacity; slot++)
            {
                if (!used.Contains(slot))
                    return slot;
            }
            return -1;
        }
    }

    /// <summary>
    /// T5 phase 2 Harmony hooks: native LootView opens on the proxy inventory, and the
    /// double-click take/put paths become CItemTransfer intents. Drag/drop keeps the T4B guard
    /// (server items are blocked from native mutation).
    /// </summary>
    internal static class ContainerUiGuard
    {
        private const string HarmonyId = "duckovcoop.containers";
        private static Harmony _harmony;

        internal static void Init()
        {
            if (_harmony != null)
                return;
            try
            {
                _harmony = new Harmony(HarmonyId);
                Patch(typeof(LootView), "OnStartLoot", nameof(PrefixStartLoot));
                Patch(typeof(LootView), "OnClose", nameof(PrefixClose));
                Patch(typeof(LootView), "OnDisable", nameof(PrefixClose));
                Patch(typeof(LootView), "OnLootTargetItemDoubleClicked", nameof(PrefixTake));
                Patch(typeof(LootView), "OnCharacterInventoryItemDoubleClicked", nameof(PrefixPut));
                Patch(typeof(LootView), "OnCharacterSlotItemDoubleClicked", nameof(PrefixSlotPut));
                Patch(typeof(LootView), "OnPickAllButtonClicked", nameof(PrefixPickAll));
                CoopRuntime.Log("[container] LootView hooks installed");
            }
            catch (Exception ex)
            {
                CoopRuntime.Log($"[container] LootView hook failed: {ex.GetType().Name}: " +
                                ex.Message);
            }
        }

        private static void Patch(Type type, string method, string prefix)
        {
            var target = AccessTools.Method(type, method);
            if (target == null)
            {
                CoopRuntime.Log($"[container] {type.Name}.{method} not found");
                return;
            }
            _harmony.Patch(target,
                prefix: new HarmonyMethod(typeof(ContainerUiGuard), prefix));
        }

        private static bool PrefixStartLoot(InteractableLootbox lootbox)
        {
            var runtime = CoopRuntime.Active;
            return runtime == null || runtime.TryPrepareContainerView(lootbox);
        }

        private static void PrefixClose()
        {
            CoopRuntime.Active?.OnContainerViewClosed();
        }

        private static bool PrefixTake(UIInventoryEntry entry, PointerEventData data)
        {
            var runtime = CoopRuntime.Active;
            if (runtime == null)
                return true;
            var handled = runtime.TryRouteContainerTake(entry);
            if (handled)
                data?.Use();
            return !handled;
        }

        private static bool PrefixPut(UIInventoryEntry entry, PointerEventData data)
        {
            var runtime = CoopRuntime.Active;
            if (runtime == null)
                return true;
            var handled = runtime.TryRouteContainerPut(entry);
            if (handled)
                data?.Use();
            return !handled;
        }

        private static bool PrefixSlotPut(SlotDisplay slotDisplay)
        {
            var runtime = CoopRuntime.Active;
            return runtime == null || !runtime.TryRouteContainerSlotPut(slotDisplay);
        }

        private static bool PrefixPickAll()
        {
            var runtime = CoopRuntime.Active;
            if (runtime == null || !runtime.ContainerViewActive)
                return true;
            runtime.BeginContainerPickAll();
            return false;
        }
    }
}
