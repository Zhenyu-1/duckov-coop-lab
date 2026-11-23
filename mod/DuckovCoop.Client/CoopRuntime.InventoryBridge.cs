using System;
using System.Collections.Generic;
using System.Reflection;
using Cysharp.Threading.Tasks;
using Duckov.UI;
using Duckov.Utilities;
using DuckovCoop.Client;
using HarmonyLib;
using ItemStatsSystem;
using ItemStatsSystem.Items;
using UnityEngine;
using UnityEngine.EventSystems;
using UIInventoryEntry = Duckov.UI.InventoryEntry;

namespace DuckovCoopClient
{
    /// <summary>
    /// T4A: applies the server's SInitialInventory to the native CharacterItem slots/inventory.
    /// Read-only mirror: native inventory mutations and capacity recalculation are blocked while
    /// the mirror is active, so the client cannot diverge from the server before T4B.
    /// </summary>
    public sealed partial class CoopRuntime : MonoBehaviour
    {
        private bool _inventoryMirrorActive;
        private bool _applyingServerInventory;
        private byte _pendingApplyMask;
        private float _nextInventoryBlockedLog;
        private float _nextEquipmentReapplyLogAt;
        // 2026-11-15: a client that joins someone else's raid without ever starting one
        // never gets a CharacterItem (LevelManager.CreateMainCharacterAsync only runs for
        // the raid starter). Nothing can be applied to the character until it exists, so
        // bootstrap it from the game's own default item type.
        private float _nextCharacterItemBootstrapAt;
        private int _characterItemBootstrapAttempts;
        // The native CharacterItem can disappear on death (the corpse owns the gear). Keep the
        // type id while it is still valid so the respawn bootstrap can rebuild the same item
        // instead of failing on GameplayDataSettings.ItemAssets being unavailable in-raid.
        private int _localCharacterItemTypeId;
        private int _lastInitialInventoryCount;
        private readonly uint[] _lastAppliedKindRevision = new uint[3];
        private int _appliedInventoryEquip;
        private int _appliedInventoryBag;
        private int _appliedInventoryPet;
        private uint _appliedInventoryRevision;
        private Inventory _petInventory;
        private GameObject _petInventoryObject;
        private bool _ownsPetInventory;
        private readonly Dictionary<uint, Item> _serverUidToItem = new Dictionary<uint, Item>();
        private readonly Dictionary<Item, uint> _serverUidByItem = new Dictionary<Item, uint>();
        private readonly Dictionary<uint, Item>[] _kindItems =
        {
            new Dictionary<uint, Item>(),
            new Dictionary<uint, Item>(),
            new Dictionary<uint, Item>(),
        };
        private string _inventoryLine = "[inventory] equip=0 bag=0 pet=0 rev=0 uidMap=0";
        private float _nextInventoryStatus;
        private float _nextInventoryBlockLog;
        private ulong _itemTransferCorrelation;
        private ulong _pendingItemTransfer;
        private float _pendingTransferSince;
        private bool _awaitingInventorySnapshot;
        private float _awaitingSnapshotSince;

        /// <summary>
        /// A drag onto an occupied equipment slot runs as two server transfers: the old item
        /// moves to a free bag slot, then the dragged item plugs into the equipment slot.
        /// The second step waits for the first snapshot so it carries fresh revisions.
        /// </summary>
        private struct PendingEquipmentSwap
        {
            public Item Item;
            public byte SourceKind;
            public ushort SourceSlot;
            public ushort TargetSlot;
        }

        private PendingEquipmentSwap? _pendingSwap;

        /// <summary>True while the apply/reconcile path drives native Plug/Unplug itself.</summary>
        private bool _applyingServerMods;

        /// <summary>Attachments this client plugged, keyed "uid|slotKey".</summary>
        private readonly Dictionary<string, Item> _appliedMods = new Dictionary<string, Item>();

        private ulong _itemModDetachCorrelation;
        private ulong _pendingModDetach;
        private int _lastItemModDetachCount;
        /// <summary>Contract 2.17.0: plug a backpack item into an attachment slot.</summary>
        private ulong _itemModAttachCorrelation;
        private ulong _pendingModAttach;
        private int _lastItemModAttachCount;
        private int _lastItemTransferCount;
        private ulong _itemArrangeCorrelation;
        private ulong _pendingItemArrange;
        private float _pendingArrangeSince;
        private int _lastItemArrangeCount;
        private ulong _itemConsumeCorrelation;
        private ulong _pendingItemConsume;
        private float _pendingConsumeSince;
        private int _lastItemConsumeCount;
        /// <summary>Contract 2.16.0: native heal amount per item type (0 = not a healing item).</summary>
        private static readonly Dictionary<int, int> _healAmountByType = new Dictionary<int, int>();

        internal bool InventoryMirrorActive => _inventoryMirrorActive;
        internal bool ApplyingServerInventory => _applyingServerInventory;

        private void UpdateInventoryBridge()
        {
            if (_client == null)
            {
                ClearInventoryMirror("no client");
                UpdateInventoryStatus(force: true);
                return;
            }

            if (_client.InitialInventoryCount != _lastInitialInventoryCount)
            {
                _lastInitialInventoryCount = _client.InitialInventoryCount;
                var initial = _client.InventoryMirror;
                Log($"[inventory] initial snapshot: equipment={initial.EquipmentCount} " +
                    $"backpack={initial.BackpackCount} petBag={initial.PetBagCount} " +
                    $"rev={initial.Revision}");
                _pendingApplyMask = 0x7;
                InvalidateLocalLoadout();
            }

            PollItemTransferResults();
            PollItemModDetachResults();
            PollItemModAttachResults();
            PollItemArrangeResults();
            PollItemConsumeResults();

            if (_pendingItemTransfer != 0 &&
                Time.unscaledTime - _pendingTransferSince > 3f)
            {
                Log("[inventory] transfer result timeout; unblocking");
                _pendingItemTransfer = 0;
            }
            if (_pendingItemArrange != 0 &&
                Time.unscaledTime - _pendingArrangeSince > 3f)
            {
                Log("[inventory] arrange result timeout; unblocking");
                _pendingItemArrange = 0;
            }
            if (_pendingItemConsume != 0 &&
                Time.unscaledTime - _pendingConsumeSince > 3f)
            {
                Log("[inventory] consume result timeout; unblocking");
                _pendingItemConsume = 0;
            }

            // T4C: apply only the kinds whose server revision changed. A backpack snapshot
            // must not rebuild equipment (which would reset the native MP5 magazine).
            var mirror = _client.InventoryMirror;
            for (byte kind = 0; kind < 3; kind++)
            {
                var revision = _client.PlayerInventoryRevision(kind);
                if (revision == 0 || revision == _lastAppliedKindRevision[kind])
                    continue;

                _pendingApplyMask |= (byte)(1 << kind);
                if (_awaitingInventorySnapshot)
                {
                    _awaitingInventorySnapshot = false;
                    Log($"[inventory] transfer snapshot received kind={kind} rev={revision}");
                }
            }

            if (_awaitingInventorySnapshot &&
                Time.unscaledTime - _awaitingSnapshotSince > 2f)
            {
                _awaitingInventorySnapshot = false;
                _pendingApplyMask |= 0x7;
                Log("[inventory] transfer snapshot wait timed out; reconciling all kinds");
            }

            if (_client.State != ClientState.InGame || !_inRaidScene)
            {
                if (_inventoryMirrorActive || _petInventory != null)
                    ClearInventoryMirror("left raid");
                UpdateInventoryStatus(force: false);
                return;
            }

            if (_pendingApplyMask != 0)
            {
                if (TryGetNativeInventory(out var main))
                {
                    var equipmentChanged = (_pendingApplyMask & 0x1) != 0;
                    ApplyServerInventory(main, mirror, _pendingApplyMask);
                    if (equipmentChanged)
                        InvalidateLocalLoadout();
                }
                else
                {
                    var bootstrapTarget = CharacterMainControl.Main;
                    if (bootstrapTarget == null || bootstrapTarget.CharacterItem != null ||
                        !TryBootstrapCharacterItem(bootstrapTarget, _localCharacterItemTypeId))
                        LogInventoryApplyBlocked();
                }
            }

            UpdatePendingSwap();
            UpdateItemDetails();
            UpdateItemMods();
            UpdateInventoryStatus(force: false);
        }

        // ItemDetail can arrive after the item was rebuilt (pickup/transfer ordering), so the
        // count-only gate missed newly created equipment copies. Track the item instance and
        // detail signature instead and re-apply when either changes.
        private readonly Dictionary<uint, Item> _detailAppliedItem = new Dictionary<uint, Item>();
        private readonly Dictionary<uint, ulong> _detailAppliedSignature =
            new Dictionary<uint, ulong>();

        private void PollItemTransferResults()
        {
            if (_client == null || _client.ItemTransferCount == _lastItemTransferCount)
                return;

            _lastItemTransferCount = _client.ItemTransferCount;
            var result = _client.LastItemTransfer;
            if (result == null)
                return;

            var ours = result.CorrelationId == _pendingItemTransfer;
            if (ours)
                _pendingItemTransfer = 0;

            Log($"[inventory] transfer result: corr={result.CorrelationId} code={result.Code} " +
                $"uid={result.ItemUid} srcRev={result.SourceRevision} " +
                $"dstRev={result.TargetRevision}");

            if (!ours)
                return;

            if (_pendingSwap != null && result.Code != 0)
            {
                Log($"[inventory] swap aborted: first transfer code={result.Code}");
                _pendingSwap = null;
            }

            if (_pickAllAwaitingResult)
            {
                _pickAllAwaitingResult = false;
                if (result.Code != 0 && result.Code != 7)
                    CancelContainerPickAll($"transfer code={result.Code}");
            }

            if (result.Code == 0 || result.Code == 7)
            {
                // The server emits SItemTransfer before the source/target SInventorySnapshot
                // pushes; the revision loop in UpdateInventoryBridge clears this once they land.
                _awaitingInventorySnapshot = true;
                _awaitingSnapshotSince = Time.unscaledTime;
            }
            else
            {
                _awaitingInventorySnapshot = false;
            }
        }

        private void PollItemArrangeResults()
        {
            if (_client == null || _client.ItemArrangeCount == _lastItemArrangeCount)
                return;

            _lastItemArrangeCount = _client.ItemArrangeCount;
            var result = _client.LastItemArrange;
            if (result == null)
                return;

            var ours = result.CorrelationId == _pendingItemArrange;
            if (ours)
                _pendingItemArrange = 0;

            Log($"[inventory] arrange result: corr={result.CorrelationId} code={result.Code} " +
                $"rev={result.Revision}");

            if (!ours)
                return;

            if (result.Code == 0)
            {
                // The server emits SItemArrange before the container snapshot. The revision
                // loop applies it; keep transfers/drops blocked until that snapshot lands.
                _awaitingInventorySnapshot = true;
                _awaitingSnapshotSince = Time.unscaledTime;
            }
            else
            {
                _awaitingInventorySnapshot = false;
            }
        }

        private void PollItemConsumeResults()
        {
            if (_client == null || _client.ItemConsumeCount == _lastItemConsumeCount)
                return;

            _lastItemConsumeCount = _client.ItemConsumeCount;
            var result = _client.LastItemConsume;
            if (result == null)
                return;

            var ours = result.CorrelationId == _pendingItemConsume;
            if (ours)
                _pendingItemConsume = 0;

            Log($"[inventory] consume result: corr={result.CorrelationId} code={result.Code} " +
                $"uid={result.ItemUid} remaining={result.Remaining} rev={result.Revision}");

            if (!ours)
                return;

            if (result.Code == 0)
            {
                _awaitingInventorySnapshot = true;
                _awaitingSnapshotSince = Time.unscaledTime;
            }
            else
            {
                _awaitingInventorySnapshot = false;
            }
        }

        private bool TryGetNativeInventory(out CharacterMainControl main)
        {
            main = CharacterMainControl.Main;
            if (main == null)
                return false;
            var characterItem = main.CharacterItem;
            if (characterItem != null)
            {
                var typeId = ReadCharacterItemTypeId(main);
                if (typeId > 0)
                    _localCharacterItemTypeId = typeId;
            }
            return characterItem != null &&
                   characterItem.Inventory != null &&
                   characterItem.Slots != null;
        }

        /// <summary>
        /// 2026-11-15 T0: a pending initial-inventory apply silently did nothing for a whole
        /// raid because the native inventory was not ready. Name the missing piece (rate
        /// limited) so the next log answers "why is the mirror still empty?".
        /// </summary>
        private void LogInventoryApplyBlocked()
        {
            if (Time.unscaledTime < _nextInventoryBlockedLog)
                return;
            _nextInventoryBlockedLog = Time.unscaledTime + 2f;
            var main = CharacterMainControl.Main;
            var characterItem = main != null ? main.CharacterItem : null;
            Log($"[inventory] apply pending, native inventory not ready: main={main != null} " +
                $"characterItem={characterItem != null} " +
                $"inventory={(characterItem != null && characterItem.Inventory != null)} " +
                $"slots={(characterItem != null && characterItem.Slots != null)} " +
                $"mask=0x{_pendingApplyMask:X2} rev={(_client != null && _client.InventoryMirror != null ? _client.InventoryMirror.Revision : 0u)}");
        }

        /// <summary>
        /// 2026-11-15: LevelManager.CreateMainCharacterAsync is the only caller of
        /// LoadOrCreateCharacterItemInstance(), and it only runs for the player who starts the
        /// raid. A client joining an existing raid therefore keeps a CharacterMainControl with
        /// CharacterItem == null forever: SInitialInventory has nowhere to apply and the V8
        /// appearance sender can only emit empty slots. Rebuild the item the game would have
        /// created and hand it to CharacterMainControl.SetItem (public, same call the spawn
        /// path makes), then let the pending inventory apply on the next frame.
        /// </summary>
        private bool TryBootstrapCharacterItem(CharacterMainControl main, int preferredTypeId = 0)
        {
            if (main == null || main.CharacterItem != null)
                return false;
            if (Time.unscaledTime < _nextCharacterItemBootstrapAt)
                return false;
            _nextCharacterItemBootstrapAt = Time.unscaledTime + 2f;
            _characterItemBootstrapAttempts++;
            try
            {
                var assets = GameplayDataSettings.ItemAssets;
                // In-raid GameplayDataSettings.ItemAssets can be unavailable; the mirror
                // caller passes the local player's character item type as a fallback.
                var typeId = preferredTypeId > 0
                    ? preferredTypeId
                    : assets != null ? assets.DefaultCharacterItemTypeID : 0;
                if (typeId <= 0)
                {
                    Log("[inventory] bootstrap: default character item type unavailable " +
                        $"(cached={_localCharacterItemTypeId})");
                    return false;
                }

                var item = ItemAssetsCollection.InstantiateSync(typeId);
                if (item == null)
                {
                    Log($"[inventory] bootstrap: instantiate failed type={typeId}");
                    return false;
                }

                main.SetItem(item);
                var ok = main.CharacterItem != null;
                Log($"[inventory] bootstrap: character item type={typeId} " +
                    $"attempt={_characterItemBootstrapAttempts} ok={ok}");
                return ok;
            }
            catch (Exception ex)
            {
                Log($"[inventory] bootstrap failed: {ex.GetType().Name}: {ex.Message}");
                return false;
            }
        }

        private void ApplyServerInventory(CharacterMainControl main, InventoryMirrorState mirror,
                                          byte mask)
        {
            if (main == null || mirror == null || mask == 0)
                return;

            var characterItem = main.CharacterItem;
            if (characterItem == null || characterItem.Inventory == null ||
                characterItem.Slots == null)
                return;

            var inventory = characterItem.Inventory;
            _applyingServerInventory = true;
            _inventoryMirrorActive = true;
            try
            {
                inventory.SetCapacity(Math.Max(1, mirror.CapacityOfKind(1)));
                var pet = EnsurePetInventory();

                var equip = _appliedInventoryEquip;
                var bag = _appliedInventoryBag;
                var petCount = _appliedInventoryPet;
                if ((mask & 0x1) != 0)
                    equip = ApplyInventoryKind(characterItem, mirror, 0, pet);
                if ((mask & 0x2) != 0)
                    bag = ApplyInventoryKind(characterItem, mirror, 1, pet);
                if ((mask & 0x4) != 0)
                    petCount = ApplyInventoryKind(characterItem, mirror, 2, pet);
                if ((mask & 0x1) != 0)
                    TryRequestBackpackCapacity(main);
                if ((mask & 0x1) != 0)
                    RefreshHeldItem(main);

                _appliedInventoryEquip = equip;
                _appliedInventoryBag = bag;
                _appliedInventoryPet = petCount;
                _appliedInventoryRevision = mirror.Revision;

                for (byte kind = 0; kind < 3; kind++)
                {
                    if ((mask & (1 << kind)) != 0)
                        _lastAppliedKindRevision[kind] = _client.PlayerInventoryRevision(kind);
                }
                _pendingApplyMask &= (byte)~mask;

                Log($"[inventory] apply: equipment={equip} backpack={bag} " +
                    $"petBag={petCount} mask=0x{mask:X2}");
            }
            catch (Exception ex)
            {
                for (byte kind = 0; kind < 3; kind++)
                {
                    if ((mask & (1 << kind)) != 0)
                        _lastAppliedKindRevision[kind] = _client.PlayerInventoryRevision(kind);
                }
                _pendingApplyMask &= (byte)~mask;
                Log("[inventory] apply failed: " + ex.GetType().Name + ": " + ex.Message);
            }
            finally
            {
                _applyingServerInventory = false;
            }
        }

        private void RefreshHeldItem(CharacterMainControl main)
        {
            var holder = main != null ? main.agentHolder : null;
            if (holder == null)
                return;

            var held = holder.CurrentHoldItemAgent;
            var heldItem = held != null ? held.Item : null;
            var stillEquipped = heldItem != null &&
                                _serverUidByItem.ContainsKey(heldItem) &&
                                _kindItems[0].ContainsValue(heldItem);
            if (stillEquipped)
                return;

            var hadHeld = held != null || holder.CurrentHoldItemAgent != null;
            // The native drop destroyed the held item object; clear the stale holder before
            // selecting another weapon, otherwise the character keeps the old model.
            holder.ChangeHoldItem(null);
            var switched = main.SwitchToFirstAvailableWeapon();
            if (hadHeld || switched)
            {
                Log($"[inventory] held item refresh: cleared=true switched={switched}");
            }
        }

        /// <summary>
        /// Apply only one kind. Native Items are preserved when the server still has the same
        /// (kind, uid, itemId); the slot is only a location, so a moved item is detached and
        /// re-placed instead of rebuilt. This is what keeps the MP5 magazine (and any other
        /// native-only state) across a backpack snapshot or an inventory move.
        /// </summary>
        private int ApplyInventoryKind(Item characterItem, InventoryMirrorState mirror,
                                       byte kind, Inventory pet)
        {
            var desired = new List<InventoryMirrorState.Entry>();
            var desiredUids = new HashSet<uint>();
            foreach (var entry in mirror.Entries)
            {
                if (entry.Kind != kind)
                    continue;
                desired.Add(entry);
                desiredUids.Add(entry.ItemUid);
            }

            var old = _kindItems[kind];
            var preserved = new HashSet<uint>();
            var moving = new List<PreservedMove>();
            foreach (var entry in desired)
            {
                if (!old.TryGetValue(entry.ItemUid, out var item) || item == null)
                    continue;

                var currentSlot = CurrentItemSlot(kind, item, characterItem, pet);
                var current = new InventoryMirrorState.Entry
                {
                    Kind = kind,
                    Slot = currentSlot,
                    ItemUid = entry.ItemUid,
                    ItemId = item.TypeID > 0 ? (uint)item.TypeID : 0u,
                    Count = item.StackCount > 0 ? (uint)item.StackCount : 0u,
                };
                if (!InventoryMirrorDiff.CanKeep(current, entry))
                    continue; // same UID but a different item prefab: rebuild

                preserved.Add(entry.ItemUid);
                if (currentSlot == entry.Slot)
                {
                    UpdateStackCount(item, entry.Count);
                    continue;
                }

                moving.Add(new PreservedMove
                {
                    Entry = entry,
                    Item = item,
                    OldSlot = currentSlot,
                });
            }

            // Phase 1: detach every preserved item that changes slot first. Two items may
            // swap slots (possible after two server transfers through a free slot), so no
            // item may still occupy a target slot while another one is being placed.
            foreach (var move in moving)
                DetachKindItem(kind, move.Item);

            // Phase 2: destroy items the server no longer has, or whose itemId changed.
            // A changed itemId is rebuilt later; a vanished UID is only removed.
            var rebuildLogged = new HashSet<uint>();
            foreach (var pair in new List<KeyValuePair<uint, Item>>(old))
            {
                if (preserved.Contains(pair.Key))
                    continue;

                if (desiredUids.Contains(pair.Key))
                {
                    Log($"[inventory] rebuild uid={pair.Key} item={ItemIdOf(pair.Value)} " +
                        "reason=itemId-changed");
                    rebuildLogged.Add(pair.Key);
                }
                else
                {
                    Log($"[inventory] remove uid={pair.Key} item={ItemIdOf(pair.Value)}");
                }
                DestroyKindItem(kind, pair.Key, pair.Value);
            }

            // Phase 3: server-authoritative apply. Native items without a serverUid cannot
            // keep a desired slot occupied; they are only swept in the kinds being applied.
            PurgeNativeKindItems(kind, characterItem, pet);

            // Phase 4: re-place the moved preserved items, then create the new ones.
            var movedByUid = new Dictionary<uint, PreservedMove>();
            foreach (var move in moving)
                movedByUid[move.Entry.ItemUid] = move;

            var applied = 0;
            foreach (var entry in desired)
            {
                if (preserved.Contains(entry.ItemUid))
                {
                    if (!movedByUid.TryGetValue(entry.ItemUid, out var move))
                    {
                        applied++; // same slot, StackCount already updated
                        continue;
                    }

                    if (PlaceInventoryItem(kind, move.Item, entry, characterItem, pet))
                    {
                        UpdateStackCount(move.Item, entry.Count);
                        Log($"[inventory] preserve uid={entry.ItemUid} item={entry.ItemId} " +
                            $"slot={move.OldSlot}->{entry.Slot}");
                        applied++;
                        continue;
                    }

                    // The native move failed (e.g. the target slot cannot plug this item).
                    // Fall back to the old rebuild path so the mirror still matches the server.
                    Log($"[inventory] rebuild uid={entry.ItemUid} item={entry.ItemId} " +
                        "reason=relocate-failed");
                    DestroyKindItem(kind, entry.ItemUid, move.Item);
                    rebuildLogged.Add(entry.ItemUid);
                    if (RebuildInventoryItem(characterItem, kind, entry, pet))
                        applied++;
                    continue;
                }

                if (!rebuildLogged.Contains(entry.ItemUid))
                {
                    Log($"[inventory] rebuild uid={entry.ItemUid} item={entry.ItemId} reason=new");
                }
                if (RebuildInventoryItem(characterItem, kind, entry, pet))
                    applied++;
            }

            return applied;
        }

        /// <summary>One preserved item whose server slot differs from its native slot.</summary>
        private sealed class PreservedMove
        {
            public InventoryMirrorState.Entry Entry;
            public Item Item;
            public int OldSlot;
        }

        private bool RebuildInventoryItem(Item characterItem, byte kind,
                                          InventoryMirrorState.Entry entry, Inventory pet)
        {
            if (kind == 2 && pet != null && entry.Slot >= pet.Capacity)
            {
                Log($"[inventory] pet slot={entry.Slot} exceeds native capacity={pet.Capacity} " +
                    $"uid={entry.ItemUid}; skipped (server capacity={InventoryMirrorState.PetBagCapacity})");
                return false;
            }

            var item = CreateInventoryItem(entry);
            if (item == null)
                return false;

            if (!PlaceInventoryItem(kind, item, entry, characterItem, pet))
            {
                item.DestroyTree();
                Log($"[inventory] apply skipped kind={kind} slot={entry.Slot} " +
                    $"item={entry.ItemId} uid={entry.ItemUid}");
                return false;
            }

            RegisterKindItem(kind, entry.ItemUid, item);
            return true;
        }

        /// <summary>Removes an item from its native container/slot without destroying it.</summary>
        private void DetachKindItem(byte kind, Item item)
        {
            if (item == null)
                return;

            switch (kind)
            {
                case 0:
                {
                    var slot = item.PluggedIntoSlot;
                    if (slot != null)
                        slot.Unplug();
                    break;
                }
                case 1:
                case 2:
                {
                    var inventory = item.InInventory;
                    if (inventory != null)
                        inventory.RemoveItem(item);
                    break;
                }
            }
        }

        private static uint ItemIdOf(Item item)
            => item != null && item.TypeID > 0 ? (uint)item.TypeID : 0u;

        private Item CreateInventoryItem(InventoryMirrorState.Entry entry)
        {
            Item item = null;
            try
            {
                item = ItemAssetsCollection.InstantiateSync(unchecked((int)entry.ItemId));
            }
            catch (Exception ex)
            {
                Log($"[inventory] instantiate failed item={entry.ItemId} uid={entry.ItemUid}: " +
                    ex.GetType().Name + ": " + ex.Message);
            }
            if (item == null)
            {
                Log($"[inventory] missing item prefab item={entry.ItemId} uid={entry.ItemUid}");
                return null;
            }

            item.StackCount = entry.Count > int.MaxValue ? int.MaxValue : (int)entry.Count;
            ApplyItemDetailFromServer(item, entry.ItemUid);
            ApplyInventoryItemIcon(item, entry.ItemUid);
            return item;
        }

        private bool _applyingServerDetail;

        private static ulong DetailSignature(ItemDetail detail)
        {
            unchecked
            {
                var hash = 14695981039346656037UL;
                hash = (hash ^ detail.Durability) * 1099511628211UL;
                hash = (hash ^ detail.MaxDurability) * 1099511628211UL;
                hash = (hash ^ detail.LoadedAmmoItemId) * 1099511628211UL;
                hash = (hash ^ detail.LoadedAmmoCount) * 1099511628211UL;
                return hash;
            }
        }

        private void UpdateItemDetails()
        {
            if (_client == null)
                return;
            foreach (var pair in _client.ItemDetails)
            {
                Item item = null;
                if (!_serverUidToItem.TryGetValue(pair.Key, out item) &&
                    !_containerProxyItems.TryGetValue(pair.Key, out item))
                    continue;
                if (item == null)
                    continue;

                var detail = pair.Value;
                var signature = DetailSignature(detail);
                if (_detailAppliedItem.TryGetValue(pair.Key, out var appliedItem) &&
                    appliedItem == item &&
                    _detailAppliedSignature.TryGetValue(pair.Key, out var appliedSignature) &&
                    appliedSignature == signature)
                    continue;

                ApplyItemDetailFromServer(item, pair.Key);
                _detailAppliedItem[pair.Key] = item;
                _detailAppliedSignature[pair.Key] = signature;
            }
        }

        private void ApplyItemDetailFromServer(Item item, uint itemUid)
        {
            if (_client == null || item == null)
                return;
            if (!_client.ItemDetails.TryGetValue(itemUid, out var detail))
                return;
            if (detail.MaxDurability == 0)
            {
                // MaxDurability 0 can still carry a valid loaded-ammo record (and some guns
                // legitimately have no durability). Restore the magazine even in that case.
                RestoreLoadedAmmo(item, detail);
                return;
            }
            var max = detail.MaxDurability / 1000f;
            var current = Mathf.Min(max, detail.Durability / 1000f);
            _applyingServerDetail = true;
            try
            {
                if (Mathf.Abs(item.MaxDurability - max) > 0.0001f)
                    item.MaxDurability = max;
                item.Durability = current;
                RestoreLoadedAmmo(item, detail);
            }
            finally
            {
                _applyingServerDetail = false;
            }
        }

        // Contract 2.14.0: server-owned attachments (loadout tickets). SItemMods can arrive
        // after the item was rebuilt, so apply by uid + signature and re-apply when the
        // native item instance changes.
        private readonly Dictionary<uint, Item> _modsAppliedItem = new Dictionary<uint, Item>();
        private readonly Dictionary<uint, ulong> _modsAppliedSignature =
            new Dictionary<uint, ulong>();

        private void UpdateItemMods()
        {
            if (_client == null)
                return;
            foreach (var pair in _client.ItemModsByUid)
            {
                Item item = null;
                if (!_serverUidToItem.TryGetValue(pair.Key, out item) &&
                    !_containerProxyItems.TryGetValue(pair.Key, out item))
                    continue;
                if (item == null)
                    continue;

                var signature = ModsSignature(pair.Value);
                if (_modsAppliedItem.TryGetValue(pair.Key, out var appliedItem) &&
                    appliedItem == item &&
                    _modsAppliedSignature.TryGetValue(pair.Key, out var appliedSignature) &&
                    appliedSignature == signature)
                    continue;

                ApplyItemModsFromServer(item, pair.Key, pair.Value);
                _modsAppliedItem[pair.Key] = item;
                _modsAppliedSignature[pair.Key] = signature;
            }
        }

        private static ulong ModsSignature(ItemMods mods)
        {
            var hash = 1469598103934665603UL;
            if (mods == null || mods.Mods == null)
                return hash;
            foreach (var mod in mods.Mods)
            {
                if (mod.SlotKey != null)
                {
                    foreach (var c in mod.SlotKey)
                        hash = (hash ^ c) * 1099511628211UL;
                }
                hash = (hash ^ mod.ItemId) * 1099511628211UL;
            }
            return hash;
        }

        private void ApplyItemModsFromServer(Item item, uint itemUid, ItemMods message)
        {
            if (item == null || message == null)
                return;
            try
            {
                var slots = item.Slots;
                if (slots == null)
                    return;
                var mods = message.Mods ?? Array.Empty<ItemMod>();
                _applyingServerMods = true;

                foreach (var mod in mods)
                {
                    if (mod.ItemId == 0 || string.IsNullOrEmpty(mod.SlotKey))
                        continue;
                    var slot = slots.GetSlot(mod.SlotKey);
                    if (slot == null)
                    {
                        // Log every miss (once per apply) plus the keys this item actually has:
                        // weapons do not all expose the same slot set (AK47 Lv2 has no Stock/Tec).
                        Log($"[inventory] attachment slot missing uid={itemUid} " +
                            $"item={item.TypeID} slot={mod.SlotKey} " +
                            $"available={DescribeSlots(slots)}");
                        continue;
                    }
                    if (slot.Content != null && slot.Content.TypeID == unchecked((int)mod.ItemId))
                    {
                        _appliedMods[itemUid + "|" + mod.SlotKey] = slot.Content;
                        continue;
                    }
                    var child = ItemAssetsCollection.InstantiateSync(unchecked((int)mod.ItemId));
                    if (child == null)
                    {
                        Log($"[inventory] attachment item missing uid={itemUid} " +
                            $"type={mod.ItemId}");
                        continue;
                    }
                    // Attachments are built outside the inventory rebuild path, so resolve their
                    // icon here too (otherwise the mod-slot UI shows the white "cross" placeholder).
                    ApplyInventoryItemIcon(child, mod.ItemId, mod.SlotKey);
                    if (!slot.Plug(child, out var displaced))
                    {
                        Log($"[inventory] attachment plug rejected uid={itemUid} " +
                            $"slot={mod.SlotKey} type={mod.ItemId}");
                        UnityEngine.Object.Destroy(child.gameObject);
                        continue;
                    }
                    // Plug returns whatever it unplugged (a prefab default or our previous mod).
                    // The server's list is authoritative, so that copy is not an item any more.
                    if (displaced != null && !ReferenceEquals(displaced, child))
                        UnityEngine.Object.Destroy(displaced.gameObject);
                    _appliedMods[itemUid + "|" + mod.SlotKey] = child;
                    Log($"[inventory] attachment applied uid={itemUid} item={item.TypeID} " +
                        $"slot={mod.SlotKey} type={mod.ItemId}");
                }

                // Contract 2.15.0: the message is the authoritative set for this uid, so a slot
                // that is no longer listed was detached server-side. Drop the native copy; the
                // server already created a backpack item for it.
                var prefix = itemUid + "|";
                var stale = new List<string>();
                foreach (var key in _appliedMods.Keys)
                {
                    if (key.StartsWith(prefix, StringComparison.Ordinal))
                        stale.Add(key);
                }
                foreach (var key in stale)
                {
                    var slotKey = key.Substring(prefix.Length);
                    if (Array.Exists(mods, m => m.SlotKey == slotKey))
                        continue;
                    var tracked = _appliedMods[key];
                    _appliedMods.Remove(key);
                    var slot = slots.GetSlot(slotKey);
                    if (slot == null || slot.Content == null)
                        continue;
                    if (tracked != null && !ReferenceEquals(slot.Content, tracked))
                        continue;
                    var removed = slot.Unplug();
                    if (removed != null)
                        UnityEngine.Object.Destroy(removed.gameObject);
                    Log($"[inventory] attachment removed uid={itemUid} slot={slotKey}");
                    // Contract 2.16.0: unplugging a magazine makes the native game dump the
                    // rounds that were loaded into the character's inventory. The server has
                    // already returned them to the backpack, so ask for an authoritative
                    // backpack snapshot after the unplug settles: if the dump lands after the
                    // detach snapshot, this resync overwrites the local count instead of
                    // leaving the player with duplicated rounds.
                    if (IsMagazineSlot(slotKey))
                        RequestAmmoResync("magazine detach");
                }
            }
            catch (Exception ex)
            {
                Log($"[inventory] attachment apply failed uid={itemUid}: " +
                    ex.GetType().Name + ": " + ex.Message);
            }
            finally
            {
                _applyingServerMods = false;
            }
        }

        /// <summary>Contract 2.16.0: the native slot key of a gun's magazine attachment.</summary>
        private static bool IsMagazineSlot(string slotKey)
        {
            if (string.IsNullOrEmpty(slotKey))
                return false;
            var key = slotKey.Trim();
            return key.Equals("Mag", StringComparison.OrdinalIgnoreCase) ||
                   key.Equals("Magazine", StringComparison.OrdinalIgnoreCase) ||
                   key.Equals("Clip", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>Comma-separated keys of the item's native slots, for diagnostics.</summary>
        private static string DescribeSlots(object slotsObject)
        {
            if (!(slotsObject is System.Collections.IEnumerable list))
                return "<none>";
            var keys = new List<string>();
            try
            {
                foreach (var slot in list)
                {
                    var key = slot?.GetType()
                        .GetProperty("Key", BindingFlags.Instance | BindingFlags.Public)
                        ?.GetValue(slot) as string;
                    if (!string.IsNullOrEmpty(key))
                        keys.Add(key);
                }
            }
            catch
            {
                return "<unknown>";
            }
            return keys.Count == 0 ? "<none>" : string.Join(",", keys);
        }

        private uint _lastCapacityRequest;
        private float _lastCapacityRequestAt;
        private void TryRequestBackpackCapacity(CharacterMainControl main)
        {
            if (main == null || _client == null || _client.State != ClientState.InGame)
                return;
            var desired = Mathf.Clamp(Mathf.RoundToInt(main.InventoryCapacity), 1, 64);
            var current = _client.InventoryMirror.CapacityOfKind(1);
            if (desired == current)
                return;
            if (_lastCapacityRequest == (uint)desired &&
                Time.unscaledTime - _lastCapacityRequestAt < 5f)
                return;
            if (Time.unscaledTime - _lastCapacityRequestAt < 1f)
                return;
            if (!_client.SendBackpackCapacity((uint)desired))
                return;
            _lastCapacityRequest = (uint)desired;
            _lastCapacityRequestAt = Time.unscaledTime;
            Log($"[inventory] backpack capacity request native={desired} server={current}");
        }

        /// <summary>
        /// A gun rebuilt from the server has an empty native magazine. The server keeps the
        /// loaded ammo in ItemDetail; restore it so the HUD/reload logic does not see 0 rounds
        /// and send another paid reload for a magazine the server already considers loaded.
        /// </summary>
        private void RestoreLoadedAmmo(Item item, ItemDetail detail)
        {
            if (item == null || detail == null)
                return;
            bool isGun;
            try
            {
                isGun = item.GetBool("IsGun");
            }
            catch
            {
                return;
            }
            if (!isGun || item.Inventory == null)
                return;

            // Contract 2.17.0: the server detail is authoritative in BOTH directions. Detaching a
            // magazine (or switching the ammo type) clears the magazine server-side while the
            // native game may keep the rounds loaded until its own lazy refresh — the player
            // could even fire rounds the server already returned to the backpack. Drop anything
            // the server does not count.
            var wantedType = unchecked((int)detail.LoadedAmmoItemId);
            var wanted = detail.LoadedAmmoCount > int.MaxValue
                ? int.MaxValue
                : (int)detail.LoadedAmmoCount;
            if (detail.LoadedAmmoItemId != 0 && detail.LoadedAmmoCount > 0)
                _loadedAmmoTypeByGun[detail.ItemUid] = detail.LoadedAmmoItemId;
            else
                _loadedAmmoTypeByGun.Remove(detail.ItemUid);
            _serverMagazineByGun[detail.ItemUid] = (wantedType, wanted);

            // The detail-mapped item is not always the object the player is holding (an inventory
            // rebuild can leave the map on a stale copy while the held agent keeps its own item).
            // Trim every instance that represents this gun, and put rounds back into the held one.
            var heldItem = HeldGunItem(detail.ItemUid);
            var removed = TrimGunMagazine(item, wantedType, wanted);
            if (heldItem != null && !ReferenceEquals(heldItem, item))
                removed += TrimGunMagazine(heldItem, wantedType, wanted);
            if (removed > 0)
                Log($"[ammo] detail sync removed {removed} native rounds uid={detail.ItemUid} " +
                    $"server={wantedType}x{wanted}" +
                    (heldItem != null && !ReferenceEquals(heldItem, item) ? " (held copy too)" : ""));

            if (detail.LoadedAmmoItemId == 0 || detail.LoadedAmmoCount == 0)
            {
                SetBulletCountCache(item, 0);
                if (heldItem != null && !ReferenceEquals(heldItem, item))
                    SetBulletCountCache(heldItem, 0);
                return;
            }

            item = heldItem ?? item;
            var current = 0;
            foreach (var child in item.Inventory)
            {
                if (child != null && child.TypeID == (int)detail.LoadedAmmoItemId)
                    current += child.StackCount;
            }
            var missing = wanted - current;
            if (missing <= 0)
            {
                SetBulletCountCache(item, wanted);
                return;
            }

            var ammo = ItemAssetsCollection.InstantiateSync((int)detail.LoadedAmmoItemId);
            if (ammo == null)
                return;
            ammo.StackCount = missing;
            ammo.Inspected = true;
            if (!item.Inventory.AddAndMerge(ammo))
            {
                ammo.DestroyTree();
                return;
            }
            SetBulletCountCache(item, wanted);
            Log($"[ammo] restored loaded ammo uid={detail.ItemUid} item={detail.LoadedAmmoItemId} " +
                $"added={missing} total={wanted}");
        }

        /// <summary>
        /// <c>ItemSetting_Gun</c> caches the magazine count in <c>Item.Variables</c> and only
        /// recomputes when the cache is negative. Local magazine edits bypass the native paths, so
        /// the cache has to be written explicitly — and with the *exact* count: a rebuilt item can
        /// carry a stale serialized count while its ammo stacks are already correct, which leaves
        /// the HUD showing the old magazine size forever.
        /// </summary>
        private static void SetBulletCountCache(Item gunItem, int count)
        {
            if (gunItem == null)
                return;
            ItemSetting_Gun setting = null;
            try
            {
                setting = gunItem.GetComponent<ItemSetting_Gun>();
            }
            catch
            {
                return;
            }
            if (setting == null)
                return;
            try
            {
                var setter = AccessTools.PropertySetter(typeof(ItemSetting_Gun), "bulletCount");
                if (setter != null)
                {
                    setter.Invoke(setting, new object[] { count });
                    return;
                }
                var field = AccessTools.Field(typeof(ItemSetting_Gun), "_bulletCountCache");
                field?.SetValue(setting, count);
            }
            catch (Exception ex)
            {
                Log($"[ammo] bullet cache reset failed: {ex.GetType().Name}: {ex.Message}");
            }
        }

        /// <summary>
        /// Contract 2.17.0: remove native magazine rounds the server no longer counts (stacks of
        /// a different ammo type are always dropped, the wanted type is trimmed to keepCount).
        /// Returns the number of rounds removed.
        /// </summary>
        private static int TrimGunMagazine(Item gunItem, int keepType, int keepCount)
        {
            if (gunItem == null || gunItem.Inventory == null)
                return 0;

            var stacks = new List<Item>();
            var sameType = 0;
            foreach (var child in gunItem.Inventory)
            {
                if (child == null)
                    continue;
                stacks.Add(child);
                if (keepType != 0 && child.TypeID == keepType)
                    sameType += child.StackCount;
            }
            var excessSameType = Math.Max(0, sameType - Math.Max(0, keepCount));

            var removed = 0;
            foreach (var child in stacks)
            {
                if (child == null)
                    continue;
                if (keepType != 0 && child.TypeID == keepType)
                {
                    if (excessSameType <= 0)
                        continue;
                    var take = Math.Min(excessSameType, child.StackCount);
                    excessSameType -= take;
                    removed += take;
                    if (child.StackCount <= take)
                        child.DestroyTree();
                    else
                        child.StackCount -= take;
                }
                else
                {
                    removed += child.StackCount;
                    child.DestroyTree();
                }
            }
            return removed;
        }

        private static void UpdateStackCount(Item item, uint count)
        {
            var value = count > int.MaxValue ? int.MaxValue : (int)count;
            if (item.StackCount != value)
                item.StackCount = value;
        }

        private bool PlaceInventoryItem(byte kind, Item item, InventoryMirrorState.Entry entry,
                                        Item characterItem, Inventory pet)
        {
            switch (kind)
            {
                case 0:
                {
                    var slot = FindEquipmentSlot(characterItem.Slots, entry.Slot);
                    if (slot == null || !slot.CanPlug(item))
                        return false;
                    var applied = slot.Plug(item, out var unplugged);
                    if (unplugged != null)
                        unplugged.DestroyTree();
                    return applied;
                }
                case 1:
                    return characterItem.Inventory.AddAt(item, entry.Slot);
                case 2:
                    return pet != null && pet.AddAt(item, entry.Slot);
                default:
                    return false;
            }
        }

        private static int CurrentItemSlot(byte kind, Item item, Item characterItem, Inventory pet)
        {
            if (item == null)
                return -1;
            switch (kind)
            {
                case 0:
                {
                    var slot = item.PluggedIntoSlot;
                    return slot != null && slot.Master == characterItem
                        ? SlotIndex(characterItem.Slots, slot)
                        : -1;
                }
                case 1:
                    return item.InInventory == characterItem.Inventory
                        ? characterItem.Inventory.GetIndex(item)
                        : -1;
                case 2:
                    return pet != null && item.InInventory == pet ? pet.GetIndex(item) : -1;
                default:
                    return -1;
            }
        }

        private void PurgeNativeKindItems(byte kind, Item characterItem, Inventory pet)
        {
            switch (kind)
            {
                case 0:
                    foreach (var slot in characterItem.Slots)
                    {
                        var content = slot != null ? slot.Content : null;
                        if (content == null || _serverUidByItem.ContainsKey(content))
                            continue;
                        slot.Unplug();
                        content.DestroyTree();
                    }
                    break;
                case 1:
                {
                    var items = new List<Item>();
                    foreach (var item in characterItem.Inventory)
                        items.Add(item);
                    foreach (var item in items)
                    {
                        if (item == null || _serverUidByItem.ContainsKey(item))
                            continue;
                        characterItem.Inventory.RemoveItem(item);
                        item.DestroyTree();
                    }
                    break;
                }
                case 2:
                {
                    if (pet == null)
                        break;
                    var items = new List<Item>();
                    foreach (var item in pet)
                        items.Add(item);
                    foreach (var item in items)
                    {
                        if (item == null || _serverUidByItem.ContainsKey(item))
                            continue;
                        pet.RemoveItem(item);
                        item.DestroyTree();
                    }
                    break;
                }
            }
        }

        private void DestroyKindItem(byte kind, uint uid, Item item)
        {
            if (item != null)
            {
                switch (kind)
                {
                    case 0:
                    {
                        var slot = item.PluggedIntoSlot;
                        if (slot != null)
                            slot.Unplug();
                        break;
                    }
                    case 1:
                    case 2:
                    {
                        var inventory = item.InInventory;
                        if (inventory != null)
                            inventory.RemoveItem(item);
                        break;
                    }
                }
                item.DestroyTree();
            }

            UnregisterKindItem(kind, uid, item);
        }

        private void RegisterKindItem(byte kind, uint uid, Item item)
        {
            _kindItems[kind][uid] = item;
            _serverUidToItem[uid] = item;
            _serverUidByItem[item] = uid;
        }

        private void UnregisterKindItem(byte kind, uint uid, Item item)
        {
            if (item != null && _kindItems[kind].TryGetValue(uid, out var existing) &&
                existing == item)
                _kindItems[kind].Remove(uid);
            // A kind-local removal must not unmap a uid that was just rebuilt into another
            // kind (equipment <- backpack transfer). The snapshot can remove the old kind
            // after the new kind was already applied.
            if (!_serverUidToItem.TryGetValue(uid, out var mapped) || mapped == null ||
                mapped == item)
                _serverUidToItem.Remove(uid);
            _detailAppliedItem.Remove(uid);
            _detailAppliedSignature.Remove(uid);
            if (item != null)
                _serverUidByItem.Remove(item);
        }

        private static Sprite FindNativeItemIcon(int typeId, Item exclude)
        {
            try
            {
                var items = Resources.FindObjectsOfTypeAll<Item>();
                foreach (var candidate in items)
                {
                    if (candidate == null || candidate == exclude || candidate.TypeID != typeId)
                        continue;
                    if (candidate.Icon != null)
                        return candidate.Icon;
                }
            }
            catch (Exception ex)
            {
                Log("[inventory] icon template scan failed: " + ex.GetType().Name + ": " +
                    ex.Message);
            }
            return null;
        }


        private static Slot FindEquipmentSlot(SlotCollection slots, int slotIndex)
        {
            if (slots == null || slotIndex < 0)
                return null;
            if (slotIndex < InventorySlotMap.EquipmentCapacity)
            {
                var key = InventorySlotMap.KeyAt(slotIndex);
                var byKey = key != null ? slots.GetSlot(key) : null;
                if (byKey != null)
                    return byKey;
            }
            return slotIndex < slots.Count ? slots[slotIndex] : null;
        }

        private Inventory EnsurePetInventory()
        {
            // Prefer the real native pet inventory; only create our own when the game has no
            // PetProxy (e.g. early scene load). Never destroy the game's inventory object.
            var native = PetProxy.PetInventory;
            if (native != null)
            {
                if (_petInventory != null && _ownsPetInventory)
                {
                    try
                    {
                        _petInventory.DestroyAllContent();
                    }
                    catch (Exception ex)
                    {
                        Log("[inventory] self pet clear failed: " + ex.GetType().Name + ": " +
                            ex.Message);
                    }
                    if (_petInventoryObject != null)
                        UnityEngine.Object.Destroy(_petInventoryObject);
                    _petInventoryObject = null;
                    _petInventory = null;
                }

                if (_petInventory != native)
                {
                    _petInventory = native;
                    _ownsPetInventory = false;
                }
                return _petInventory;
            }

            if (_petInventory != null)
                return _petInventory;

            _petInventoryObject = new GameObject("CoopPetBagInventory");
            _petInventoryObject.hideFlags = HideFlags.HideAndDontSave;
            UnityEngine.Object.DontDestroyOnLoad(_petInventoryObject);
            _petInventory = _petInventoryObject.AddComponent<Inventory>();
            _petInventory.SetCapacity(InventoryMirrorState.PetBagCapacity);
            _ownsPetInventory = true;
            return _petInventory;
        }

        private void ClearInventoryMirror(string reason)
        {
            ClearContainerInspectionState();
            _pendingApplyMask = 0;
            _pendingItemTransfer = 0;
            _pendingItemArrange = 0;
            _pendingItemConsume = 0;
            _awaitingInventorySnapshot = false;
            for (var kind = 0; kind < 3; kind++)
            {
                _lastAppliedKindRevision[kind] = 0;
                _kindItems[kind].Clear();
            }
            var hadState = _inventoryMirrorActive || _petInventory != null ||
                           _petInventoryObject != null;
            _inventoryMirrorActive = false;

            _applyingServerInventory = true;
            try
            {
                if (_petInventory != null)
                    _petInventory.DestroyAllContent();
            }
            catch (Exception ex)
            {
                Log("[inventory] pet clear failed: " + ex.GetType().Name + ": " + ex.Message);
            }
            finally
            {
                _applyingServerInventory = false;
            }

            if (_ownsPetInventory && _petInventoryObject != null)
            {
                UnityEngine.Object.Destroy(_petInventoryObject);
            }
            _petInventoryObject = null;
            _petInventory = null;
            _ownsPetInventory = false;
            _serverUidToItem.Clear();
            _serverUidByItem.Clear();
            _detailAppliedItem.Clear();
            _detailAppliedSignature.Clear();
            _appliedInventoryEquip = 0;
            _appliedInventoryBag = 0;
            _appliedInventoryPet = 0;
            _appliedInventoryRevision = 0;

            if (hadState)
                Log($"[inventory] cleared ({reason})");
        }

        private void UpdateInventoryStatus(bool force)
        {
            if (!force && Time.unscaledTime < _nextInventoryStatus)
                return;
            _nextInventoryStatus = Time.unscaledTime + 0.5f;
            _inventoryLine = $"[inventory] equip={_appliedInventoryEquip} " +
                             $"bag={_appliedInventoryBag} pet={_appliedInventoryPet} " +
                             $"rev={_appliedInventoryRevision} uidMap={_serverUidToItem.Count}";
        }

        /// <summary>
        /// T4B: intercept a drop onto a backpack/pet inventory entry and send CItemTransfer.
        /// Returns true when the native drop must be skipped.
        /// </summary>
        internal bool TryRouteInventoryDrop(UIInventoryEntry entry, PointerEventData eventData)
        {
            if (!CanRouteItemTransfer() || entry == null)
                return false;
            if (eventData == null || eventData.used ||
                eventData.button != PointerEventData.InputButton.Left)
                return false;

            var target = entry.Master != null ? entry.Master.Target : null;
            if (target == null)
                return false;

            var targetContainerId = _openContainerLootbox != null && _openContainerId != 0 &&
                                    target == _openContainerLootbox.Inventory
                ? _openContainerId
                : 0u;
            var targetIsOurs = target == MainBackpack() ||
                               (_petInventory != null && target == _petInventory) ||
                               targetContainerId != 0;
            var item = GetDragItem(eventData);
            if (item == null)
                return false;
            if (item.NeedInspection)
            {
                LogBlockedNativeMutation($"drop deferred: uninspected item={DescribeItem(item)}");
                return true;
            }

            var sourceMapped = TryMapItemLocation(item, out var sourceKind, out var sourceSlot);
            var occupied = entry.Content;
            if (_openContainerId != 0 &&
                (sourceMapped && sourceKind >= 3 || targetContainerId != 0))
            {
                Log($"[container] drag check target={target.name} slot={entry.Index} " +
                    $"src={sourceKind}:{sourceSlot} needInspect={item.NeedInspection} " +
                    $"targetIsOurs={targetIsOurs}");
            }

            // Explicit container <-> player routes. The generic branch below also covers
            // this, but keeping the fallback here makes the drag path independent of the
            // targetIsOurs inference (which depends on the native Inventory instance).
            if (sourceMapped && sourceKind >= 3)
            {
                byte playerKind;
                if (target == MainBackpack())
                    playerKind = 1;
                else if (_petInventory != null && target == _petInventory)
                    playerKind = 2;
                else
                    playerKind = 255;
                if (playerKind != 255 && !item.NeedInspection)
                {
                    Log($"[container] drag take fallback src={sourceKind}:{sourceSlot} " +
                        $"dst={playerKind}:{entry.Index} uid={_serverUidByItem[item]}");
                    return TrySendItemTransfer(item, sourceKind, sourceSlot, playerKind,
                        entry.Index);
                }
            }
            if (targetContainerId != 0 && sourceMapped && sourceKind < 3 &&
                !item.NeedInspection)
            {
                if (targetContainerId != 0 &&
                    _openContainerKind == ContainerMirrorState.CorpseContainerKind)
                {
                    LogBlockedNativeMutation("drag: corpse containers are take-only");
                    return true;
                }
                Log($"[container] drag put fallback src={sourceKind}:{sourceSlot} " +
                    $"dst={_openContainerKind}:{entry.Index} uid={_serverUidByItem[item]}");
                return TrySendItemTransfer(item, sourceKind, sourceSlot, _openContainerKind,
                    entry.Index);
            }

            if (!sourceMapped)
            {
                if (occupied != null && _serverUidByItem.ContainsKey(occupied))
                {
                    LogBlockedNativeMutation(
                        $"drop native item onto server bag slot={entry.Index} " +
                        $"target={DescribeItem(occupied)}");
                    return true;
                }
                return false; // native item: keep native drag/drop behaviour (T5 owns lootbox)
            }
            if (!targetIsOurs)
            {
                // A server-owned item must not leak into a lootbox/storage/stash: T5 will
                // route those targets; until then block instead of duplicating it.
                LogBlockedNativeMutation($"drop server item into unsupported inventory={target.name}");
                return true;
            }

            var targetKind = targetContainerId != 0
                ? _openContainerKind
                : (target == MainBackpack() ? (byte)1 : (byte)2);
            if (targetKind == ContainerMirrorState.CorpseContainerKind)
            {
                LogBlockedNativeMutation("drop server item into corpse container; take-only");
                return true;
            }
            if (occupied != null)
            {
                if (ReferenceEquals(occupied, item))
                    return true;

                // P1: same itemId onto an occupied slot is a server-side stack merge. The
                // native stack is untouched; the server moves the requested count and pushes
                // a fresh snapshot.
                if (_serverUidByItem.ContainsKey(occupied) && occupied.TypeID == item.TypeID)
                {
                    var maxStack = item.MaxStackCount;
                    var room = maxStack - occupied.StackCount;
                    if (maxStack <= 1 || room <= 0)
                    {
                        LogBlockedNativeMutation(
                            $"drop full stack slot={entry.Index} item={DescribeItem(item)}");
                        return true;
                    }

                    var count = Math.Min(item.StackCount, room);
                    if (count <= 0)
                        return true;
                    return TrySendItemTransfer(item, sourceKind, sourceSlot, targetKind,
                        entry.Index, (uint)count);
                }

                LogBlockedNativeMutation(
                    $"drop occupied bag slot={entry.Index} item={DescribeItem(item)} " +
                    $"target={DescribeItem(occupied)}");
                return true;
            }

            return TrySendItemTransfer(item, sourceKind, sourceSlot, targetKind, entry.Index);
        }

        /// <summary>
        /// T4B: intercept a drop onto a main-character equipment slot and send CItemTransfer.
        /// Returns true when the native drop must be skipped.
        /// </summary>
        internal bool TryRouteSlotDrop(SlotDisplay display, PointerEventData eventData)
        {
            if (!CanRouteItemTransfer() || display == null)
                return false;
            if (eventData == null || eventData.used ||
                eventData.button != PointerEventData.InputButton.Left)
                return false;

            var target = display.Target;
            var characterItem = MainCharacterItem();
            if (target == null || characterItem == null || target.Master != characterItem)
                return false;

            var item = GetDragItem(eventData);
            if (item == null)
                return false;
            if (item.NeedInspection)
            {
                LogBlockedNativeMutation($"slot drop deferred: uninspected item={DescribeItem(item)}");
                return true;
            }

            var sourceMapped = TryMapItemLocation(item, out var sourceKind, out var sourceSlot);
            if (target.Content != null)
            {
                if (ReferenceEquals(target.Content, item))
                    return true;
                if (!sourceMapped && !_serverUidByItem.ContainsKey(target.Content))
                    return false; // native item onto native equipment: native behaviour

                // P2: dragging a bag/pet item onto an equipped one swaps them. The server has no
                // atomic swap, so run it as two transfers: displaced item -> free bag slot, then
                // the dragged item -> the equipment slot (after the first snapshot lands).
                var swapTargetSlot = SlotIndex(characterItem.Slots, target);
                if ((sourceKind == 1 || sourceKind == 2) && swapTargetSlot >= 0 &&
                    target.CanPlug(item) &&
                    _serverUidByItem.ContainsKey(target.Content) &&
                    _serverUidByItem.ContainsKey(item))
                {
                    if (!TryFindFreeSlot(sourceKind, out var swapSlot))
                    {
                        LogBlockedNativeMutation(
                            $"slot swap blocked: no free slot kind={sourceKind} " +
                            $"item={DescribeItem(item)}");
                        return true;
                    }
                    var displaced = target.Content;
                    TrySendItemTransfer(displaced, 0, swapTargetSlot, sourceKind, swapSlot);
                    if (_pendingItemTransfer == 0)
                        return true;
                    _pendingSwap = new PendingEquipmentSwap
                    {
                        Item = item,
                        SourceKind = sourceKind,
                        SourceSlot = (ushort)sourceSlot,
                        TargetSlot = (ushort)swapTargetSlot,
                    };
                    Log($"[inventory] swap start old={DescribeItem(displaced)} " +
                        $"free={sourceKind}:{swapSlot} new={DescribeItem(item)} " +
                        $"target={swapTargetSlot}");
                    return true;
                }

                LogBlockedNativeMutation(
                    $"drop occupied equipment slot={target.Key} item={DescribeItem(item)} " +
                    $"target={DescribeItem(target.Content)}");
                return true;
            }
            if (!sourceMapped)
                return false; // native item: let native plug/denial feedback run
            if (!target.CanPlug(item))
                return false; // no mutation happens; keep the native denial feedback

            var targetSlot = SlotIndex(characterItem.Slots, target);
            if (targetSlot < 0)
                return false;

            if (sourceKind == 0 && sourceSlot == targetSlot)
                return true;
            return TrySendItemTransfer(item, sourceKind, sourceSlot, 0, targetSlot);
        }

        /// <summary>
        /// Contract 2.15.0: a native unplug of a server-declared attachment is rerouted through
        /// the server; the native mutation is blocked so no local-only item is created.
        /// </summary>
        internal bool AllowNativeSlotUnplug(ItemStatsSystem.Items.Slot slot)
        {
            // Any server-driven inventory work (rebuild, detail apply, teardown) destroys or
            // re-plugs items; the native DestroyTree path unplugs their mod slots. Intercepting
            // that as a "player detach" blocked the destroy and hung the client on scene load.
            if (slot == null || !_inRaidScene || _applyingServerMods || _applyingServerInventory ||
                _applyingServerDetail)
                return true;
            if (_client == null || _client.State != ClientState.InGame)
                return true;
            var master = slot.Master;
            if (master == null || !_serverUidByItem.TryGetValue(master, out var parentUid))
                return true;
            var slotKey = slot.Key;
            if (string.IsNullOrEmpty(slotKey))
                return true;
            if (!_client.ItemModsByUid.TryGetValue(parentUid, out var mods))
                return true;

            var known = false;
            if (mods.Mods != null)
            {
                foreach (var mod in mods.Mods)
                {
                    if (mod.SlotKey == slotKey)
                    {
                        known = true;
                        break;
                    }
                }
            }
            // Prefab-default attachments are not in the ledger; keep native behaviour for them.
            if (!known)
                return true;

            var correlation = ++_itemModDetachCorrelation;
            if (_client.SendItemModDetach(correlation, parentUid, slotKey))
            {
                _pendingModDetach = correlation;
                Log($"[inventory] detach intent: corr={correlation} parent={parentUid} " +
                    $"slot={slotKey}");
            }
            return false;
        }

        private void PollItemModDetachResults()
        {
            if (_client == null || _client.ItemModDetachCount == _lastItemModDetachCount)
                return;
            _lastItemModDetachCount = _client.ItemModDetachCount;
            var result = _client.LastItemModDetach;
            if (result == null)
                return;
            var ours = result.CorrelationId == _pendingModDetach;
            if (ours)
                _pendingModDetach = 0;
            Log($"[inventory] detach result: corr={result.CorrelationId} code={result.Code} " +
                $"uid={result.ItemUid} parent={result.ParentUid}");
            if (ours && result.Code != 0)
                Log("[inventory] detach rejected; attachment stays on the item");
        }

        /// <summary>Second leg of a pending equipment swap; runs once the first snapshot landed.</summary>
        private void UpdatePendingSwap()
        {
            if (_pendingSwap == null)
                return;
            if (_pendingItemTransfer != 0 || _awaitingInventorySnapshot)
                return;
            var swap = _pendingSwap.Value;
            _pendingSwap = null;
            if (swap.Item == null || !_serverUidByItem.ContainsKey(swap.Item))
            {
                Log("[inventory] swap aborted: dragged item is gone");
                return;
            }
            Log($"[inventory] swap finish uid={_serverUidByItem[swap.Item]} " +
                $"src={swap.SourceKind}:{swap.SourceSlot} target={swap.TargetSlot}");
            TrySendItemTransfer(swap.Item, swap.SourceKind, swap.SourceSlot, 0, swap.TargetSlot);
        }

        private bool TryFindFreeSlot(byte kind, out int slot)
        {
            slot = -1;
            var inventory = kind == 2 ? _petInventory : MainBackpack();
            if (inventory == null)
                return false;
            var capacity = Math.Max(0, inventory.Capacity);
            for (var i = 0; i < capacity; i++)
            {
                if (inventory.GetItemAt(i) == null)
                {
                    slot = i;
                    return true;
                }
            }
            return false;
        }

        private bool CanRouteItemTransfer()
        {
            return _client != null && _client.State == ClientState.InGame &&
                   _inventoryMirrorActive && !_applyingServerInventory;
        }

        private static Item MainCharacterItem()
        {
            var main = CharacterMainControl.Main;
            return main != null ? main.CharacterItem : null;
        }

        private static Inventory MainBackpack()
        {
            var characterItem = MainCharacterItem();
            return characterItem != null ? characterItem.Inventory : null;
        }

        private static Item GetDragItem(PointerEventData eventData)
        {
            if (eventData == null || eventData.pointerDrag == null)
                return null;
            var source = eventData.pointerDrag.GetComponent<IItemDragSource>();
            return source != null ? source.GetItem() : null;
        }

        private bool TryMapItemLocation(Item item, out byte kind, out int slot)
        {
            kind = 255;
            slot = -1;
            var characterItem = MainCharacterItem();
            if (item == null || characterItem == null)
                return false;

            var plugged = item.PluggedIntoSlot;
            if (plugged != null && plugged.Master == characterItem)
            {
                kind = 0;
                slot = SlotIndex(characterItem.Slots, plugged);
                return slot >= 0;
            }

            var inventory = item.InInventory;
            if (inventory == null)
                return false;
            if (_openContainerLootbox != null && _openContainerId != 0 &&
                inventory == _openContainerLootbox.Inventory)
            {
                kind = _openContainerKind;
                slot = inventory.GetIndex(item);
                return slot >= 0;
            }
            if (inventory == characterItem.Inventory)
            {
                kind = 1;
                slot = inventory.GetIndex(item);
                return slot >= 0;
            }
            if (_petInventory != null && inventory == _petInventory)
            {
                kind = 2;
                slot = inventory.GetIndex(item);
                return slot >= 0;
            }
            return false;
        }

        private static int SlotIndex(SlotCollection slots, Slot slot)
        {
            if (slot == null)
                return -1;

            // Server slot order is semantic (equipment keys), not Collection order: the
            // physical list can put Backpack/Headset in a different position.
            var byKey = InventorySlotMap.IndexOfKey(slot.Key);
            if (byKey >= 0)
                return byKey;

            if (slots == null)
                return -1;
            for (var i = 0; i < slots.Count; i++)
            {
                if (slots[i] == slot)
                    return i;
            }
            return -1;
        }

        private uint RevisionForKind(byte kind)
        {
            if ((kind == ContainerMirrorState.MapContainerKind ||
                 kind == ContainerMirrorState.CorpseContainerKind) &&
                _openContainerId != 0 && _client != null &&
                _client.ContainerMirror.TryGet(_openContainerId, out var view))
                return view.Revision;
            return _client != null ? _client.InventoryMirror.GetRevision(kind) : 0;
        }

        private bool TrySendItemTransfer(Item item, byte sourceKind, int sourceSlot,
                                         byte targetKind, int targetSlot,
                                         uint? transferCount = null)
        {            if (_pendingItemTransfer != 0 || _awaitingInventorySnapshot)
            {
                LogBlockedNativeMutation("transfer already pending");
                return true;
            }
            if (_pendingWorldItemDrop != 0)
            {
                LogBlockedNativeMutation("drop pending; transfer skipped");
                return true;
            }
            if (_pendingItemArrange != 0)
            {
                LogBlockedNativeMutation("arrange pending; transfer skipped");
                return true;
            }
            if (!_serverUidByItem.TryGetValue(item, out var itemUid))
            {
                LogBlockedNativeMutation(
                    $"transfer unknown serverUid item={(item != null ? item.name : "<null>")}");
                return true;
            }

            var revision = RevisionForKind(sourceKind);
            if (revision == 0)
            {
                LogBlockedNativeMutation($"transfer missing revision kind={sourceKind}");
                return true;
            }

            var correlation = ++_itemTransferCorrelation;
            var itemId = item.TypeID > 0 ? (uint)item.TypeID : 0u;
            var count = transferCount ?? (item.StackCount > 0 ? (uint)item.StackCount : 1u);
            if (count == 0)
            {
                LogBlockedNativeMutation("transfer zero count");
                return true;
            }
            var sourceId = sourceKind >= 3 ? _openContainerId : _client.PlayerId;
            var targetId = targetKind >= 3 ? _openContainerId : _client.PlayerId;
            if (sourceKind >= 3 && sourceId == 0)
            {
                LogBlockedNativeMutation("transfer missing open container source");
                return true;
            }
            if (targetKind >= 3 && targetId == 0)
            {
                LogBlockedNativeMutation("transfer missing open container target");
                return true;
            }
            var sent = _client.SendItemTransfer(correlation, sourceKind, sourceId,
                (ushort)sourceSlot, targetKind, targetId, (ushort)targetSlot, itemUid,
                count, itemId, revision);
            if (!sent)
            {
                LogBlockedNativeMutation("transfer send failed");
                return true;
            }

            _pendingItemTransfer = correlation;
            _pendingTransferSince = Time.unscaledTime;
            ItemUIUtilities.NotifyPutItem(item);
            Log($"[inventory] transfer intent: corr={correlation} " +
                $"src={sourceKind}:{sourceSlot} dst={targetKind}:{targetSlot} " +
                $"uid={itemUid} item={itemId} count={count} rev={revision}");
            return true;
        }

        internal bool ShouldBlockInventoryClick(UIInventoryEntry entry,
                                                PointerEventData eventData)
        {
            if (!_inventoryMirrorActive || _applyingServerInventory ||
                entry == null || eventData == null ||
                eventData.button != PointerEventData.InputButton.Left)
                return false;

            var selected = ItemUIUtilities.SelectedItem;
            if (selected == null || !_serverUidByItem.ContainsKey(selected))
                return false; // native items keep native click behaviour

            LogBlockedNativeMutation(
                $"click move bag slot={entry.Index} item={DescribeItem(selected)}");
            return true;
        }

        internal bool ShouldBlockSlotClick(SlotDisplay display, PointerEventData eventData)
        {
            if (!_inventoryMirrorActive || _applyingServerInventory ||
                display == null || eventData == null ||
                eventData.button != PointerEventData.InputButton.Left)
                return false;

            var selected = ItemUIUtilities.SelectedItem;
            if (selected == null || !_serverUidByItem.ContainsKey(selected))
                return false;

            LogBlockedNativeMutation(
                $"click move equipment slot={display.Target?.Key} item={DescribeItem(selected)}");
            return true;
        }

        /// <summary>
        /// P1: native Inventory.Sort is blocked and re-implemented as an authoritative layout
        /// submit. The client only reads item metadata and slot order; it never calls Detach,
        /// Combine or AddItem, so StackCount stays server-owned. Returns true when the native
        /// sort must be skipped.
        /// </summary>
        internal bool TryRouteInventorySort(Inventory inventory)
        {
            if (!_inventoryMirrorActive || _applyingServerInventory || inventory == null)
                return false;

            byte kind;
            uint ownerId;
            uint revision;
            var backpack = MainBackpack();
            var playerId = _client != null ? _client.PlayerId : 0u;
            if (inventory == backpack)
            {
                kind = 1;
                ownerId = playerId;
                revision = _client != null ? _client.PlayerInventoryRevision(1) : 0;
            }
            else if (_petInventory != null && inventory == _petInventory)
            {
                kind = 2;
                ownerId = playerId;
                revision = _client != null ? _client.PlayerInventoryRevision(2) : 0;
            }
            else if (_openContainerLootbox != null && _openContainerId != 0 &&
                     inventory == _openContainerLootbox.Inventory &&
                     _client != null &&
                     _client.ContainerMirror.TryGet(_openContainerId, out var containerView))
            {
                if (_openContainerKind != ContainerMirrorState.MapContainerKind)
                {
                    LogBlockedNativeMutation("sort blocked: corpse containers are take-only");
                    return true;
                }
                kind = _openContainerKind;
                ownerId = _openContainerId;
                revision = containerView.Revision;
            }
            else
            {
                return false;
            }

            if (_pendingItemTransfer != 0 || _awaitingInventorySnapshot ||
                _pendingWorldItemDrop != 0 || _pendingItemArrange != 0)
            {
                LogBlockedNativeMutation("sort deferred: another inventory op is pending");
                return true;
            }

            if (revision == 0)
            {
                LogBlockedNativeMutation($"sort missing revision kind={kind}");
                return true;
            }

            List<DuckovCoop.Client.InventoryEntry> layout;
            try
            {
                layout = BuildSortLayout(inventory);
            }
            catch (Exception ex)
            {
                Log("[inventory] sort layout failed: " + ex.GetType().Name + ": " + ex.Message);
                return true;
            }

            if (layout == null)
            {
                LogBlockedNativeMutation("sort blocked: container has a native/unmapped item");
                return true;
            }
            if (layout.Count == 0)
            {
                Log("[inventory] sort no-op: empty container");
                return true;
            }
            if (IsSameLayout(inventory, layout))
            {
                Log("[inventory] sort no-op: layout unchanged");
                return true;
            }

            var correlation = ++_itemArrangeCorrelation;
            var sent = _client.SendItemArrange(correlation, kind, ownerId, revision,
                layout.ToArray());
            if (!sent)
            {
                LogBlockedNativeMutation("sort send failed");
                return true;
            }

            _pendingItemArrange = correlation;
            _pendingArrangeSince = Time.unscaledTime;
            Log($"[inventory] arrange intent: corr={correlation} kind={kind} " +
                $"entries={layout.Count} rev={revision}");
            return true;
        }

        private List<DuckovCoop.Client.InventoryEntry> BuildSortLayout(Inventory inventory)
        {
            var capacity = inventory.Capacity;
            var locked = new List<SortItem>();
            var movable = new List<SortItem>();
            for (var slot = 0; slot < capacity; slot++)
            {
                var item = inventory.GetItemAt(slot);
                if (item == null)
                    continue;
                if (!_serverUidByItem.TryGetValue(item, out var uid))
                    return null; // native item: do not mix client-only items into a server layout

                var entry = new SortItem
                {
                    Slot = slot,
                    Item = item,
                    Uid = uid,
                    Count = item.StackCount > 0 ? (uint)item.StackCount : 0u,
                    MaxStack = item.MaxStackCount > 0 ? (uint)item.MaxStackCount : 1u,
                };
                if (inventory.IsIndexLocked(slot))
                    locked.Add(entry);
                else
                    movable.Add(entry);
            }

            var layout = new List<DuckovCoop.Client.InventoryEntry>();
            var occupiedByLocked = new HashSet<int>();
            foreach (var entry in locked)
            {
                occupiedByLocked.Add(entry.Slot);
                layout.Add(ToLayoutEntry(entry.Slot, entry.Uid, entry.Item.TypeID, entry.Count));
            }

            if (movable.Count == 0)
                return layout;

            var sorted = SortMovableItems(movable);
            var freeSlots = new List<int>();
            for (var slot = 0; slot < capacity; slot++)
            {
                if (!occupiedByLocked.Contains(slot))
                    freeSlots.Add(slot);
            }

            var index = 0;
            foreach (var stack in sorted)
            {
                if (index >= freeSlots.Count)
                    return null; // cannot represent the merged stacks in the available slots
                layout.Add(ToLayoutEntry(freeSlots[index++], stack.Uid, (int)stack.ItemId,
                    stack.Count));
            }
            return layout;
        }

        /// <summary>Mirrors native Inventory.Sort: first-tag order, then Item.Order/TypeID, then
        /// max-stack merge. It only computes the target uids/counts; nothing is mutated.</summary>
        private static List<SortStack> SortMovableItems(List<SortItem> movable)
        {
            var groups = new List<SortGroup>();
            foreach (var entry in movable)
            {
                var tag = FirstTag(entry.Item);
                SortGroup group = null;
                foreach (var candidate in groups)
                {
                    if (ReferenceEquals(candidate.Tag, tag))
                    {
                        group = candidate;
                        break;
                    }
                }
                if (group == null)
                {
                    group = new SortGroup { Tag = tag };
                    groups.Add(group);
                }
                group.Items.Add(entry);
            }

            groups.Sort(static (a, b) =>
            {
                var pa = a.Tag != null ? a.Tag.Priority : -1;
                var pb = b.Tag != null ? b.Tag.Priority : -1;
                if (pa != pb)
                    return pa - pb;
                var na = a.Tag != null ? a.Tag.name : string.Empty;
                var nb = b.Tag != null ? b.Tag.name : string.Empty;
                return string.Compare(na, nb, StringComparison.OrdinalIgnoreCase);
            });

            var result = new List<SortStack>();
            foreach (var group in groups)
            {
                var typeGroups = new List<SortTypeGroup>();
                foreach (var entry in group.Items)
                {
                    var typeId = entry.Item.TypeID;
                    SortTypeGroup typeGroup = null;
                    foreach (var candidate in typeGroups)
                    {
                        if (candidate.TypeId == typeId)
                        {
                            typeGroup = candidate;
                            break;
                        }
                    }
                    if (typeGroup == null)
                    {
                        typeGroup = new SortTypeGroup { TypeId = typeId };
                        typeGroups.Add(typeGroup);
                    }
                    typeGroup.Items.Add(entry);
                }

                typeGroups.Sort(static (a, b) =>
                {
                    var oa = a.Items[0].Item.Order;
                    var ob = b.Items[0].Item.Order;
                    return oa == ob ? a.TypeId - b.TypeId : oa - ob;
                });

                foreach (var typeGroup in typeGroups)
                {
                    if (!typeGroup.Items[0].Item.Stackable)
                    {
                        foreach (var entry in typeGroup.Items)
                            result.Add(SortStack.From(entry));
                        continue;
                    }
                    MergeStacks(typeGroup.Items, result);
                }
            }
            return result;
        }

        private static void MergeStacks(List<SortItem> items, List<SortStack> result)
        {
            var stack = new Stack<SortItem>(items);
            SortStack current = null;
            while (stack.Count > 0)
            {
                if (current == null)
                    current = SortStack.From(stack.Pop());
                if (stack.Count == 0)
                {
                    result.Add(current);
                    break;
                }

                SortItem last = null;
                while (current.Count < current.MaxStack && stack.Count > 0)
                {
                    last = stack.Pop();
                    var room = current.MaxStack - current.Count;
                    var moved = Math.Min(room, last.Count);
                    current.Count += moved;
                    last.Count -= moved;
                }

                result.Add(current);
                if (last != null && last.Count > 0)
                {
                    if (stack.Count == 0)
                    {
                        result.Add(SortStack.From(last));
                        break;
                    }
                    current = SortStack.From(last);
                }
                else
                {
                    current = null;
                }
            }
        }

        private static Tag FirstTag(Item item)
        {
            if (item == null || item.Tags == null || item.Tags.Count == 0)
                return null;
            return item.Tags.Get(0);
        }

        private bool IsSameLayout(Inventory inventory,
                                  List<DuckovCoop.Client.InventoryEntry> layout)
        {
            var bySlot = new Dictionary<int, DuckovCoop.Client.InventoryEntry>();
            foreach (var entry in layout)
                bySlot[entry.Slot] = entry;

            for (var slot = 0; slot < inventory.Capacity; slot++)
            {
                var item = inventory.GetItemAt(slot);
                if (item == null)
                {
                    if (bySlot.ContainsKey(slot))
                        return false;
                    continue;
                }
                if (!bySlot.TryGetValue(slot, out var entry))
                    return false;
                if (!_serverUidByItem.TryGetValue(item, out var uid) || entry.ItemUid != uid)
                    return false;
                if (entry.ItemId != (uint)item.TypeID)
                    return false;
                if (entry.Count != (item.StackCount > 0 ? (uint)item.StackCount : 0u))
                    return false;
            }
            return true;
        }

        private static DuckovCoop.Client.InventoryEntry ToLayoutEntry(int slot, uint uid,
                                                                     int itemId, uint count)
            => new DuckovCoop.Client.InventoryEntry
            {
                Slot = (ushort)slot,
                ItemUid = uid,
                ItemId = itemId > 0 ? (uint)itemId : 0u,
                Count = count,
            };

        private sealed class SortItem
        {
            public int Slot;
            public Item Item;
            public uint Uid;
            public uint Count;
            public uint MaxStack;
        }

        private sealed class SortStack
        {
            public uint Uid;
            public uint ItemId;
            public uint Count;
            public uint MaxStack;

            public static SortStack From(SortItem item) => new SortStack
            {
                Uid = item.Uid,
                ItemId = item.Item != null && item.Item.TypeID > 0
                    ? (uint)item.Item.TypeID
                    : 0u,
                Count = item.Count,
                MaxStack = item.MaxStack,
            };
        }

        private sealed class SortGroup
        {
            public Tag Tag;
            public readonly List<SortItem> Items = new List<SortItem>();
        }

        private sealed class SortTypeGroup
        {
            public int TypeId;
            public readonly List<SortItem> Items = new List<SortItem>();
        }

        private static readonly AccessTools.FieldRef<SplitDialogue, Item> SplitTargetField =
            AccessTools.FieldRefAccess<SplitDialogue, Item>("target");
        private static readonly AccessTools.FieldRef<SplitDialogue, Inventory> SplitDestinationField =
            AccessTools.FieldRefAccess<SplitDialogue, Inventory>("destination");
        private static readonly AccessTools.FieldRef<SplitDialogue, int> SplitDestinationIndexField =
            AccessTools.FieldRefAccess<SplitDialogue, int>("destinationIndex");
        private static readonly AccessTools.FieldRef<CA_UseItem, Item> UseItemField =
            AccessTools.FieldRefAccess<CA_UseItem, Item>("item");

        /// <summary>
        /// Contract 2.16.0: HP the native usage behaviors would restore for this item.
        ///
        /// Healing consumables (bandage 10, first-aid 15/16/17, ...) are
        /// <c>Duckov.ItemUsage.Drug</c> components in <c>Item.UsageUtilities.behaviors</c>;
        /// <c>Drug.OnUse</c> feeds its public int <c>healValue</c> into
        /// <c>CharacterMainControl.AddHealth</c>. It is read by reflection so the mod keeps
        /// building against a different assembly layout, and cached per item type. Items with
        /// no usage component at all (136 Injection, plain loot, ...) have no behaviors and
        /// therefore report 0; negative values (damage drugs) also report 0.
        /// </summary>
        internal static int ReadItemHealAmount(Item item)
        {
            if (item == null)
                return 0;

            var typeId = item.TypeID;
            if (_healAmountByType.TryGetValue(typeId, out var cached))
                return cached;

            var heal = 0;
            var source = "none";
            try
            {
                var usage = item.UsageUtilities;
                if (usage != null && usage.behaviors != null)
                {
                    foreach (var behavior in usage.behaviors)
                    {
                        if (behavior == null)
                            continue;
                        var type = behavior.GetType();
                        foreach (var field in type.GetFields(BindingFlags.Instance | BindingFlags.Public))
                        {
                            var fieldName = field.Name;
                            var looksLikeHeal =
                                fieldName.IndexOf("heal", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                fieldName.IndexOf("recover", StringComparison.OrdinalIgnoreCase) >= 0;
                            if (!looksLikeHeal)
                                continue;
                            var value = field.GetValue(behavior);
                            if (!(value is IConvertible convertible))
                                continue;
                            float amount;
                            try
                            {
                                amount = convertible.ToSingle(null);
                            }
                            catch
                            {
                                continue;
                            }
                            if (amount <= 0f)
                                continue;
                            heal += Mathf.CeilToInt(amount);
                            source = type.Name + "." + field.Name;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Log($"[consume] heal probe failed item={typeId}: {ex.GetType().Name}: {ex.Message}");
                return 0;
            }

            _healAmountByType[typeId] = heal;
            // One line per item type: the native field really is Drug.healValue.
            Log($"[consume] item={typeId} heal={heal} via={source}");
            return heal;
        }

        /// <summary>
        /// P1: CA_UseItem.OnFinish normally decrements StackCount/destroys the item locally.
        /// For server-owned stackable items we send CItemConsume instead, run the local use
        /// effects (energy/water/buffs) once, and skip the native count mutation. The server
        /// pushes the authoritative count/removal snapshot and, for a healing item, the
        /// authoritative HP (CombatEventV4 type=2).
        /// </summary>
        internal bool TryHandleServerItemUse(CA_UseItem action)
        {
            if (!_inventoryMirrorActive || _applyingServerInventory || action == null)
                return false;

            var item = UseItemField(action);
            if (item == null || !_serverUidByItem.ContainsKey(item))
                return false; // native item: keep the vanilla use/count path
            if (!item.Stackable)
                return false; // durability/ammo: handled by a later consume phase

            if (_pendingItemConsume != 0 || _pendingItemTransfer != 0 ||
                _awaitingInventorySnapshot || _pendingWorldItemDrop != 0 ||
                _pendingItemArrange != 0)
            {
                LogBlockedNativeMutation("consume deferred: another inventory op is pending");
                return true;
            }

            if (!TryMapItemLocation(item, out var kind, out var slot))
            {
                LogBlockedNativeMutation($"consume unmapped server item={DescribeItem(item)}");
                return true;
            }
            var revision = _client != null ? _client.PlayerInventoryRevision(kind) : 0;
            if (revision == 0)
            {
                LogBlockedNativeMutation($"consume missing revision kind={kind}");
                return true;
            }

            var itemUid = _serverUidByItem[item];
            var correlation = ++_itemConsumeCorrelation;
            var healAmount = ReadItemHealAmount(item);
            var sent = _client.SendItemConsume(correlation, kind, _client.PlayerId, (ushort)slot,
                itemUid, (uint)item.TypeID, 1, revision, 0, (uint)Mathf.Max(0, healAmount));
            if (!sent)
            {
                LogBlockedNativeMutation("consume send failed");
                return true;
            }

            _pendingItemConsume = correlation;
            _pendingConsumeSince = Time.unscaledTime;
            Log($"[inventory] consume intent: corr={correlation} kind={kind}:{slot} " +
                $"uid={itemUid} item={item.TypeID} heal={healAmount}");

            // Local effects still run immediately (the native use action already played); the
            // server owns the stack count and the following snapshot updates/removes the Item.
            try
            {
                var main = CharacterMainControl.Main;
                var inBase = LevelManager.Instance != null && LevelManager.Instance.IsBaseLevel;
                item.Use(main, inBase);
                // Contract 2.16.0: CItemConsume (sent above) already settles the heal, so the
                // use-completion message must not make the server heal a second time.
                NoteConsumableUsed(item, UseConsumable.FlagSettledByItemConsume);
            }
            catch (Exception ex)
            {
                Log("[inventory] local use failed: " + ex.GetType().Name + ": " + ex.Message);
            }
            return true;
        }

        /// <summary>
        /// P1: native SplitDialogue.DoSplit is skipped for server-owned stacks. The requested
        /// count is routed as a partial CItemTransfer; the server creates the new uid and the
        /// following snapshot updates the native items. Native stacks keep the vanilla split.
        /// </summary>
        internal bool TryRouteItemSplit(SplitDialogue dialogue, int value)
        {
            if (!_inventoryMirrorActive || _applyingServerInventory || dialogue == null)
                return false;

            var target = SplitTargetField(dialogue);
            if (target == null || !_serverUidByItem.ContainsKey(target))
                return false; // native stack: keep vanilla Split

            if (value <= 0 || value >= target.StackCount)
            {
                LogBlockedNativeMutation($"split invalid count={value} item={DescribeItem(target)}");
                return true;
            }
            if (!TryMapItemLocation(target, out var sourceKind, out var sourceSlot))
            {
                LogBlockedNativeMutation($"split unmapped server item={DescribeItem(target)}");
                return true;
            }

            var destination = SplitDestinationField(dialogue);
            var destinationIndex = SplitDestinationIndexField(dialogue);
            byte targetKind;
            int targetSlot;
            if (destination != null && destinationIndex >= 0 &&
                destination.Capacity > destinationIndex &&
                destination.GetItemAt(destinationIndex) == null)
            {
                if (destination == MainBackpack())
                    targetKind = 1;
                else if (_petInventory != null && destination == _petInventory)
                    targetKind = 2;
                else
                {
                    LogBlockedNativeMutation(
                        $"split destination unsupported inventory={destination.name}");
                    return true;
                }
                targetSlot = destinationIndex;
            }
            else
            {
                // Native falls back to SendToPlayerCharacterInventory (first free backpack slot).
                targetKind = 1;
                targetSlot = _client != null
                    ? _client.InventoryMirror.FindFirstFreeSlot(1)
                    : -1;
                if (targetSlot < 0)
                {
                    LogBlockedNativeMutation("split blocked: backpack full");
                    _interaction = "inventory: backpack full";
                    return true;
                }
            }

            if (sourceKind == targetKind && sourceSlot == targetSlot)
                return true;

            ItemUIUtilities.Select(null);
            return TrySendItemTransfer(target, sourceKind, sourceSlot, targetKind, targetSlot,
                (uint)value);
        }

        internal bool ShouldBlockServerItem(Item item)
        {
            if (!_inventoryMirrorActive || _applyingServerInventory || item == null)
                return false;
            if (!_serverUidByItem.ContainsKey(item))
                return false;
            // LMG/box-mag reloads pull rounds out of the inventory stack before the native load
            // point. The server charges the same count when CItemReload commits, so this one
            // removal has to pass or the reload aborts and never commits.
            return !IsReloadAmmoPull(item);
        }

        private bool IsReloadAmmoPull(Item item)
        {
            if ((!_ammoReloadArmed && !_ammoReloadPending) || _ammoReloadAmmoItemId == 0)
                return false;
            if (item.TypeID != unchecked((int)_ammoReloadAmmoItemId))
                return false;
            _ammoReloadPullSeen = true;
            if (!_ammoReloadPullLogged)
            {
                _ammoReloadPullLogged = true;
                Log($"[ammo] native ammo pull allowed item={item.TypeID} " +
                    $"uid={(_serverUidByItem.TryGetValue(item, out var uid) ? uid : 0)}");
            }
            return true;
        }

        private bool _ammoReloadPullLogged;

        /// <summary>
        /// Native reload splits/removes ammo stacks directly. The flat ledger has no magazine
        /// model, so allowing a native Split would desync the server stack silently; block it
        /// until an ammo contract exists. UI splitting is routed through TryRouteItemSplit.
        /// </summary>
        internal bool ShouldBlockServerItemSplit(Item item)
        {
            return _inventoryMirrorActive && !_applyingServerInventory &&
                   item != null && _serverUidByItem.ContainsKey(item);
        }

        /// <summary>P1 phase 3e: mirror native durability changes for server-owned items.</summary>
        internal void OnNativeDurabilityChanged(Item item)
        {
            if (!_inventoryMirrorActive || _applyingServerInventory || _applyingServerDetail ||
                _client == null || _client.State != ClientState.InGame || !_inRaidScene ||
                item == null || !item.UseDurability || item.MaxDurability <= 0f)
                return;
            if (!_serverUidByItem.TryGetValue(item, out var uid))
                return;

            var max = (uint)Mathf.Max(0, Mathf.RoundToInt(item.MaxDurability * 1000f));
            var current = (uint)Mathf.Max(0, Mathf.RoundToInt(
                Mathf.Clamp(item.Durability, 0f, item.MaxDurability) * 1000f));
            if (max == 0 || current > max)
                return;
            _client.SendItemDurability(uid, current, max);
        }

        internal bool ShouldBlockInventoryAddAt(Inventory inventory, int atPosition, Item item)
        {
            if (!_inventoryMirrorActive || _applyingServerInventory)
                return false;
            if (ShouldBlockServerItem(item))
                return true;
            var existing = inventory != null ? inventory.GetItemAt(atPosition) : null;
            return existing != null && _serverUidByItem.ContainsKey(existing);
        }

        /// <summary>
        /// A custom Comparison sort cannot be expressed as a native-order layout, so it stays
        /// blocked for the server-owned containers instead of silently applying the default
        /// sort order.
        /// </summary>
        internal bool ShouldBlockCustomInventorySort(Inventory inventory)
        {
            if (!_inventoryMirrorActive || _applyingServerInventory || inventory == null)
                return false;
            var backpack = MainBackpack();
            if (inventory != backpack &&
                (_petInventory == null || inventory != _petInventory) &&
                (_openContainerLootbox == null || inventory != _openContainerLootbox.Inventory))
                return false;
            Log("[inventory] custom sort blocked");
            return true;
        }

        internal bool ShouldBlockServerSlotMutation(Slot slot, Item item)
        {
            if (!_inventoryMirrorActive || _applyingServerInventory || slot == null)
                return false;
            var characterItem = MainCharacterItem();
            if (characterItem == null || slot.Master != characterItem)
                return false;
            return (item != null && _serverUidByItem.ContainsKey(item)) ||
                   (slot.Content != null && _serverUidByItem.ContainsKey(slot.Content));
        }

        /// <summary>
        /// Contract 2.17.0: plugging a server-owned backpack item into an attachment slot of a
        /// server-owned item is rerouted through CItemModAttach. The native plug is blocked:
        /// without the server round-trip the attachment exists natively while the backpack item
        /// stays in the ledger (the attachment appeared in both places).
        /// </summary>
        internal bool TryRouteModAttach(Slot slot, Item incoming)
        {
            if (!_inventoryMirrorActive || _applyingServerInventory || _applyingServerMods ||
                slot == null || incoming == null)
                return false;
            if (_client == null || _client.State != ClientState.InGame || !_inRaidScene)
                return false;

            var master = slot.Master;
            if (master == null || !_serverUidByItem.TryGetValue(master, out var parentUid))
                return false; // native gun: keep the native plug
            var slotKey = slot.Key;
            if (string.IsNullOrEmpty(slotKey))
                return false;

            if (!_serverUidByItem.TryGetValue(incoming, out var itemUid))
                return false; // native item: keep native behaviour
            if (!TryMapItemLocation(incoming, out var kind, out var itemSlot) || kind != 1)
            {
                LogBlockedNativeMutation(
                    $"attach unmapped item={DescribeItem(incoming)} slot={slotKey}");
                return true;
            }
            if (slot.Content != null)
            {
                LogBlockedNativeMutation(
                    $"attach slot busy uid={parentUid} slot={slotKey} " +
                    $"item={DescribeItem(slot.Content)}");
                return true;
            }
            if (_pendingModAttach != 0)
            {
                LogBlockedNativeMutation("attach deferred: another attach is pending");
                return true;
            }

            var correlation = ++_itemModAttachCorrelation;
            if (!_client.SendItemModAttach(correlation, parentUid, itemUid, slotKey))
            {
                LogBlockedNativeMutation("attach send failed");
                return true;
            }
            _pendingModAttach = correlation;
            Log($"[inventory] attach intent: corr={correlation} parent={parentUid} " +
                $"slot={slotKey} uid={itemUid} item={incoming.TypeID} bagSlot={itemSlot}");
            return true;
        }

        private void PollItemModAttachResults()
        {
            if (_client == null || _client.ItemModAttachCount == _lastItemModAttachCount)
                return;
            _lastItemModAttachCount = _client.ItemModAttachCount;
            var result = _client.LastItemModAttach;
            if (result == null)
                return;
            var ours = result.CorrelationId == _pendingModAttach;
            if (ours)
                _pendingModAttach = 0;
            Log($"[inventory] attach result: corr={result.CorrelationId} code={result.Code} " +
                $"parent={result.ParentUid} item={result.ItemId} uid={result.ItemUid}");
            if (ours && result.Code != 0)
                Log("[inventory] attach rejected; the item stays in the backpack");
        }

        internal bool ShouldBlockServerSlotUnplug(Slot slot)
        {
            if (!_inventoryMirrorActive || _applyingServerInventory || slot == null)
                return false;
            var characterItem = MainCharacterItem();
            if (characterItem == null || slot.Master != characterItem)
                return false;
            var content = slot.Content;
            if (content == null || !_serverUidByItem.ContainsKey(content))
                return false;

            // Fallback for unplug paths that are not the routed double-click/drag UI.
            _interaction = "inventory: unequip blocked; drag or double-click to backpack";
            return true;
        }

        internal bool TryRouteSlotUnload(SlotDisplay display)
        {
            if (!CanRouteItemTransfer() || display == null)
                return false;

            var slot = display.Target;
            var characterItem = MainCharacterItem();
            if (slot == null || characterItem == null || slot.Master != characterItem)
                return false;

            var content = slot.Content;
            if (content == null || !_serverUidByItem.ContainsKey(content))
                return false; // native equipment keeps native click behaviour

            if (_pendingItemTransfer != 0 || _awaitingInventorySnapshot)
            {
                LogBlockedNativeMutation($"click unload pending item={DescribeItem(content)}");
                return true;
            }

            var sourceSlot = SlotIndex(characterItem.Slots, slot);
            if (sourceSlot < 0)
                return true;

            var targetSlot = _client.InventoryMirror.FindFirstFreeSlot(1);
            if (targetSlot < 0)
            {
                LogBlockedNativeMutation(
                    $"click unload backpack full item={DescribeItem(content)}");
                _interaction = "inventory: backpack full";
                return true;
            }

            return TrySendItemTransfer(content, 0, sourceSlot, 1, targetSlot);
        }

        internal bool ShouldBlockNativePickup(Item item)
        {
            if (!_inventoryMirrorActive || _applyingServerInventory)
                return false;
            // T6 owns mirrored ground items: its own Harmony guard turns the interaction into
            // CWorldItemPickup. Native items (no serverUid) keep the vanilla pickup path.
            if (IsWorldItemMirrorItem(item))
                return false;
            return item != null && _serverUidByItem.ContainsKey(item);
        }

        internal string DescribeItem(Item item)
        {
            if (item == null)
                return "<null>";
            var uid = _serverUidByItem.TryGetValue(item, out var serverUid)
                ? serverUid.ToString()
                : "native";
            return $"{item.name}/itemId={item.TypeID}/uid={uid}";
        }

        /// <summary>
        /// A blocked native swap can still clear the slot on the native side before the guard
        /// returns false, which leaves the local character holding no weapon (and makes V8 send
        /// zeros). Re-apply the server's equipment kind so the slots and visual agents match
        /// the authoritative layout again.
        /// </summary>
        internal void QueueEquipmentReapply(string reason)
        {
            if (!_inventoryMirrorActive || _applyingServerInventory)
                return;
            _pendingApplyMask |= 0x1;
            if (Time.unscaledTime >= _nextEquipmentReapplyLogAt)
            {
                _nextEquipmentReapplyLogAt = Time.unscaledTime + 2f;
                Log("[inventory] equipment reapply queued: " + reason);
            }
        }

        internal void LogBlockedNativeMutation(string detail)
        {
            if (Time.unscaledTime < _nextInventoryBlockLog)
                return;
            _nextInventoryBlockLog = Time.unscaledTime + 0.5f;
            Log($"[inventory] blocked native mutation {detail}");
        }
    }

    /// <summary>
    /// T4B Harmony entry point at the native drag/drop UI layer. Only main-character backpack
    /// entries and equipment slots are routed; every other drop keeps native behaviour.
    /// </summary>
    internal static class InventoryTransferGuard
    {
        private const string HarmonyId = "duckovcoop.inventory.transfer";
        private static Harmony _harmony;

        internal static void Init()
        {
            if (_harmony != null)
                return;
            try
            {
                _harmony = new Harmony(HarmonyId);
                Patch(typeof(UIInventoryEntry), "OnDrop", nameof(BlockInventoryDrop));
                Patch(typeof(SlotDisplay), "OnDrop", nameof(BlockSlotDrop));
                Patch(typeof(UIInventoryEntry), "OnPointerClick", nameof(BlockInventoryClick));
                Patch(typeof(SlotDisplay), "OnPointerClick", nameof(BlockSlotClick));
                Patch(typeof(SlotDisplay), "OnItemDisplayDoubleClicked",
                    nameof(BlockSlotDoubleClick));
                Patch(typeof(SplitDialogue), "DoSplit", nameof(BlockSplitDialogue));
                Patch(typeof(CA_UseItem), "OnFinish", nameof(BlockItemUse));
                CoopRuntime.Log("[inventory] transfer guards installed");
            }
            catch (Exception ex)
            {
                CoopRuntime.Log(
                    $"[inventory] transfer guard failed: {ex.GetType().Name}: {ex.Message}");
            }
        }

        private static void Patch(Type type, string method, string prefix)
        {
            var target = AccessTools.Method(type, method);
            if (target == null)
            {
                CoopRuntime.Log($"[inventory] {type.Name}.{method} not found");
                return;
            }
            _harmony.Patch(target,
                prefix: new HarmonyMethod(typeof(InventoryTransferGuard), prefix));
        }

        private static bool BlockInventoryDrop(UIInventoryEntry __instance,
                                               PointerEventData eventData)
        {
            var runtime = CoopRuntime.Active;
            return runtime == null || !runtime.TryRouteInventoryDrop(__instance, eventData);
        }

        private static bool BlockSlotDrop(SlotDisplay __instance, PointerEventData eventData)
        {
            var runtime = CoopRuntime.Active;
            return runtime == null || !runtime.TryRouteSlotDrop(__instance, eventData);
        }

        private static bool BlockInventoryClick(UIInventoryEntry __instance,
                                                PointerEventData eventData)
        {
            var runtime = CoopRuntime.Active;
            return runtime == null || !runtime.ShouldBlockInventoryClick(__instance, eventData);
        }

        private static bool BlockSlotClick(SlotDisplay __instance, PointerEventData eventData)
        {
            var runtime = CoopRuntime.Active;
            return runtime == null || !runtime.ShouldBlockSlotClick(__instance, eventData);
        }

        private static bool BlockSlotDoubleClick(SlotDisplay __instance)
        {
            var runtime = CoopRuntime.Active;
            return runtime == null || !runtime.TryRouteSlotUnload(__instance);
        }

        private static bool BlockSplitDialogue(SplitDialogue __instance, int value)
        {
            var runtime = CoopRuntime.Active;
            return runtime == null || !runtime.TryRouteItemSplit(__instance, value);
        }

        private static bool BlockItemUse(CA_UseItem __instance)
        {
            var runtime = CoopRuntime.Active;
            return runtime == null || !runtime.TryHandleServerItemUse(__instance);
        }
    }

    /// <summary>
    /// Manual Harmony patches for T4A read-only mode. Only the main character's inventory and
    /// equipment slots are affected; lootboxes, other characters and the T6 world-item path
    /// keep their native behaviour.
    /// </summary>
    internal static class InventoryBridgeGuard
    {
        private const string HarmonyId = "duckovcoop.inventory";
        private static Harmony _harmony;

        internal static void Init()
        {
            if (_harmony != null)
                return;
            try
            {
                _harmony = new Harmony(HarmonyId);

                Patch(typeof(Inventory), "AddAt", nameof(BlockAddAt));
                Patch(typeof(Inventory), "AddItem", nameof(BlockAddItem));
                Patch(typeof(Inventory), "RemoveAt", nameof(BlockRemoveAt));
                Patch(typeof(Inventory), "RemoveItem", nameof(BlockRemoveItem));
                var itemSplit = AccessTools.Method(typeof(Item), "Split", new[] { typeof(int) });
                if (itemSplit != null)
                {
                    _harmony.Patch(itemSplit,
                        prefix: new HarmonyMethod(typeof(InventoryBridgeGuard), nameof(BlockSplit)));
                    CoopRuntime.Log("[inventory] native item split guard installed");
                }
                Patch(typeof(Slot), "Plug", nameof(BlockPlug));
                Patch(typeof(Slot), "Unplug", nameof(BlockUnplug));
                var tryReload = AccessTools.Method(typeof(CharacterMainControl), "TryToReload");
                if (tryReload != null)
                {
                    _harmony.Patch(tryReload,
                        prefix: new HarmonyMethod(typeof(InventoryBridgeGuard),
                            nameof(PrefixTryReload)));
                    CoopRuntime.Log("[ammo] server reload hook installed");
                }
                var startLoadBullets = AccessTools.Method(typeof(ItemAgent_Gun), "StartLoadBullets");
                if (startLoadBullets != null)
                {
                    _harmony.Patch(startLoadBullets,
                        prefix: new HarmonyMethod(typeof(InventoryBridgeGuard),
                            nameof(PrefixStartLoadBullets)));
                    CoopRuntime.Log("[ammo] native reload load-point hook installed");
                }
                var cancleReload = AccessTools.Method(typeof(ItemAgent_Gun), "CancleReload");
                if (cancleReload != null)
                {
                    _harmony.Patch(cancleReload,
                        prefix: new HarmonyMethod(typeof(InventoryBridgeGuard),
                            nameof(PrefixCancleReload)));
                    CoopRuntime.Log("[ammo] native reload cancel hook installed");
                }
                var useBullet = AccessTools.Method(typeof(ItemSetting_Gun), "UseABullet");
                if (useBullet != null)
                {
                    _harmony.Patch(useBullet,
                        postfix: new HarmonyMethod(typeof(InventoryBridgeGuard),
                            nameof(PostfixUseABullet)));
                    CoopRuntime.Log("[ammo] weapon fire hook installed");
                }
                var hitMarkerKill = AccessTools.Method(typeof(HitMarker), "OnKill");
                if (hitMarkerKill != null)
                {
                    _harmony.Patch(hitMarkerKill,
                        prefix: new HarmonyMethod(typeof(InventoryBridgeGuard),
                            nameof(PrefixHitMarkerKill)));
                    CoopRuntime.Log("[combat] native kill marker guard installed");
                }
                var durabilitySetter = AccessTools.PropertySetter(typeof(Item), "Durability");
                if (durabilitySetter != null)
                {
                    _harmony.Patch(durabilitySetter,
                        postfix: new HarmonyMethod(typeof(InventoryBridgeGuard),
                            nameof(PostfixDurability)));
                    CoopRuntime.Log("[durability] native durability hook installed");
                }
                var deathTask = AccessTools.Method(typeof(LevelManager), "CharacterDieTask");
                if (deathTask != null)
                {
                    _harmony.Patch(deathTask,
                        prefix: new HarmonyMethod(typeof(InventoryBridgeGuard),
                            nameof(BlockDeathTask)));
                    CoopRuntime.Log("[respawn] native death task guard installed");
                }
                var fogDeath = AccessTools.Method(typeof(FogOfWarManager), "OnCharacterDie");
                if (fogDeath != null)
                {
                    _harmony.Patch(fogDeath,
                        prefix: new HarmonyMethod(typeof(InventoryBridgeGuard),
                            nameof(BlockFogOfWarDeath)));
                    var fogUpdate = AccessTools.Method(typeof(FogOfWarManager), "Update");
                    if (fogUpdate != null)
                    {
                        _harmony.Patch(fogUpdate,
                            postfix: new HarmonyMethod(typeof(InventoryBridgeGuard),
                                nameof(PostfixFogOfWar)));
                    }
                    CoopRuntime.Log("[respawn] fog of war death guard installed");
                }
                Patch(typeof(CharacterMainControl), "PickupItem", nameof(BlockNativePickup));
                Patch(typeof(CharacterMainControl), "UpdateInventoryCapacity",
                    nameof(BlockCapacityUpdate));

                var sortNoArgs = AccessTools.Method(typeof(Inventory), "Sort", Type.EmptyTypes);
                if (sortNoArgs != null)
                {
                    _harmony.Patch(sortNoArgs,
                        prefix: new HarmonyMethod(typeof(InventoryBridgeGuard), nameof(BlockSort)));
                }
                var sortComparison = AccessTools.Method(typeof(Inventory), "Sort",
                    new[] { typeof(Comparison<Item>) });
                if (sortComparison != null)
                {
                    _harmony.Patch(sortComparison,
                        prefix: new HarmonyMethod(typeof(InventoryBridgeGuard),
                            nameof(BlockCustomSort)));
                }

                var slotUnplug = AccessTools.Method(typeof(ItemStatsSystem.Items.Slot), "Unplug");
                if (slotUnplug != null)
                {
                    _harmony.Patch(slotUnplug,
                        prefix: new HarmonyMethod(typeof(InventoryBridgeGuard),
                            nameof(PrefixSlotUnplug)));
                    CoopRuntime.Log("[inventory] attachment detach hook installed");
                }

                CoopRuntime.Log("[inventory] native mutation guard installed");
            }
            catch (Exception ex)
            {
                CoopRuntime.Log($"[inventory] guard failed: {ex.GetType().Name}: {ex.Message}");
            }
        }

        private static void Patch(Type type, string method, string prefix)
        {
            var target = AccessTools.Method(type, method);
            if (target == null)
            {
                CoopRuntime.Log($"[inventory] {type.Name}.{method} not found");
                return;
            }
            _harmony.Patch(target,
                prefix: new HarmonyMethod(typeof(InventoryBridgeGuard), prefix));
        }

        private static bool PrefixSlotUnplug(ItemStatsSystem.Items.Slot __instance)
        {
            var runtime = CoopRuntime.Active;
            return runtime == null || runtime.AllowNativeSlotUnplug(__instance);
        }

        private static bool BlockAddAt(Inventory __instance, Item item, int atPosition)
        {
            var runtime = CoopRuntime.Active;
            if (runtime == null ||
                !runtime.ShouldBlockInventoryAddAt(__instance, atPosition, item))
                return true;
            runtime.LogBlockedNativeMutation(
                $"inventory={Describe(__instance)} op=AddAt slot={atPosition} " +
                $"item={runtime.DescribeItem(item)}");
            return false;
        }

        private static bool BlockAddItem(Inventory __instance, Item item)
        {
            var runtime = CoopRuntime.Active;
            if (runtime == null || !runtime.ShouldBlockServerItem(item))
                return true;
            runtime.LogBlockedNativeMutation(
                $"inventory={Describe(__instance)} op=AddItem item={runtime.DescribeItem(item)}");
            return false;
        }

        private static bool BlockRemoveAt(Inventory __instance, int position)
        {
            var runtime = CoopRuntime.Active;
            var item = __instance != null ? __instance.GetItemAt(position) : null;
            if (runtime == null || !runtime.ShouldBlockServerItem(item))
                return true;
            runtime.LogBlockedNativeMutation(
                $"inventory={Describe(__instance)} op=RemoveAt slot={position} " +
                $"item={runtime.DescribeItem(item)}");
            return false;
        }

        private static bool BlockRemoveItem(Inventory __instance, Item item)
        {
            var runtime = CoopRuntime.Active;
            if (runtime == null || !runtime.ShouldBlockServerItem(item))
                return true;
            runtime.LogBlockedNativeMutation(
                $"inventory={Describe(__instance)} op=RemoveItem item={runtime.DescribeItem(item)}");
            return false;
        }

        private static bool BlockSplit(Item __instance, ref UniTask<Item> __result)
        {
            var runtime = CoopRuntime.Active;
            if (runtime == null || !runtime.ShouldBlockServerItemSplit(__instance))
                return true;
            runtime.LogBlockedNativeMutation(
                $"item split blocked item={runtime.DescribeItem(__instance)}");
            __result = UniTask.FromResult<Item>(null);
            return false;
        }

        private static bool PrefixTryReload(CharacterMainControl __instance, Item preferedBulletToLoad)
        {
            var runtime = CoopRuntime.Active;
            if (runtime == null)
                return true;
            var handled = runtime.TryBeginServerReload(__instance, preferedBulletToLoad);
            if (!handled)
                return true;
            // A successful CItemReload request must still run the native reload action for
            // its animation/progress. The server request is deferred to the native load
            // point, so reserve ammo is charged only when the bar completes.
            return runtime.AmmoReloadArmed;
        }

        private static bool PrefixStartLoadBullets(ItemAgent_Gun __instance)
        {
            var runtime = CoopRuntime.Active;
            if (runtime == null || !runtime.ShouldInterceptNativeReload(__instance))
                return true;
            runtime.OnNativeReloadStartLoad(__instance);
            return false;
        }

        private static bool PrefixCancleReload(ItemAgent_Gun __instance)
        {
            CoopRuntime.Active?.OnNativeReloadCanceled(__instance);
            return true; // X cancel always runs; no server commit was sent yet
        }

        private static void PostfixUseABullet(ItemSetting_Gun __instance)
        {
            CoopRuntime.Active?.OnGunUsedBullet(__instance);
        }

        private static bool PrefixHitMarkerKill(DamageInfo dmgInfo)
        {
            var runtime = CoopRuntime.Active;
            return runtime == null || runtime.AllowNativeKillMarker(dmgInfo);
        }

        private static void PostfixDurability(Item __instance)
        {
            CoopRuntime.Active?.OnNativeDurabilityChanged(__instance);
        }

        private static bool BlockDeathTask(LevelManager __instance)
        {
            var runtime = CoopRuntime.Active;
            return runtime == null || !runtime.ShouldBlockNativeDeathTask();
        }

        private static bool _fogDeathBlockedLogged;

        // FogOfWarManager destroys itself on main-character death. Coop keeps the same
        // character and revives it, so this must be blocked for the whole mod lifetime.
        // Do not gate on _inRaidScene: the native level loader can raise the death event
        // during a scene transition, before the runtime marks the raid scene active.
        private static bool BlockFogOfWarDeath()
        {
            if (CoopRuntime.Active == null)
                return true;
            if (!_fogDeathBlockedLogged)
            {
                _fogDeathBlockedLogged = true;
                CoopRuntime.Log("[respawn] fog of war death destroy suppressed");
            }
            return false;
        }

        private static readonly FieldInfo FogAllVisionField =
            AccessTools.Field(typeof(FogOfWarManager), "allVision");
        private static bool _fogAllVisionLogged;

        // Native Init() only sets allVision=true when fog is disabled; it never clears an
        // already-true flag when SRaidRules later re-enables fog. Keep it in sync so a raid
        // cannot silently become full-map vision.
        private static void PostfixFogOfWar(FogOfWarManager __instance)
        {
            try
            {
                var level = LevelManager.Instance;
                var rule = LevelManager.Rule;
                if (__instance == null || level == null || rule == null ||
                    FogAllVisionField == null)
                    return;
                var wantAllVision = !level.IsRaidMap || !rule.FogOfWar;
                if ((bool)FogAllVisionField.GetValue(__instance) == wantAllVision)
                    return;
                FogAllVisionField.SetValue(__instance, wantAllVision);
                if (!_fogAllVisionLogged)
                {
                    _fogAllVisionLogged = true;
                    CoopRuntime.Log($"[respawn] fog allVision={wantAllVision} " +
                                    $"raid={level.IsRaidMap} ruleFog={rule.FogOfWar}");
                }
            }
            catch (Exception ex)
            {
                CoopRuntime.Log("[respawn] fog sync failed: " + ex.GetType().Name);
            }
        }

        private static bool BlockPlug(Slot __instance, Item otherItem)
        {
            var runtime = CoopRuntime.Active;
            if (runtime == null)
                return true;
            // Contract 2.17.0: a backpack item plugged onto a server-owned item's attachment slot
            // goes through CItemModAttach (the ledger has to move the item out of the backpack).
            if (runtime.TryRouteModAttach(__instance, otherItem))
                return false;
            if (!runtime.ShouldBlockServerSlotMutation(__instance, otherItem))
                return true;
            runtime.LogBlockedNativeMutation(
                $"slot={DescribeSlot(__instance)} op=Plug item={runtime.DescribeItem(otherItem)}");
            runtime.QueueEquipmentReapply("blocked plug");
            return false;
        }

        private static bool BlockUnplug(Slot __instance)
        {
            var runtime = CoopRuntime.Active;
            if (runtime == null || !runtime.ShouldBlockServerSlotUnplug(__instance))
                return true;
            runtime.LogBlockedNativeMutation(
                $"slot={DescribeSlot(__instance)} op=Unplug " +
                $"item={runtime.DescribeItem(__instance != null ? __instance.Content : null)}");
            runtime.QueueEquipmentReapply("blocked unplug");
            return false;
        }

        private static bool BlockNativePickup(Item item)
        {
            var runtime = CoopRuntime.Active;
            if (runtime == null || !runtime.ShouldBlockNativePickup(item))
                return true;
            runtime.LogBlockedNativeMutation($"pickup item={runtime.DescribeItem(item)}");
            return false;
        }

        private static bool BlockSort(Inventory __instance)
        {
            var runtime = CoopRuntime.Active;
            return runtime == null || !runtime.TryRouteInventorySort(__instance);
        }

        private static bool BlockCustomSort(Inventory __instance)
        {
            var runtime = CoopRuntime.Active;
            return runtime == null || !runtime.ShouldBlockCustomInventorySort(__instance);
        }

        private static bool BlockCapacityUpdate(CharacterMainControl __instance)
        {
            var runtime = CoopRuntime.Active;
            if (runtime == null || !runtime.InventoryMirrorActive)
                return true;
            return __instance != CharacterMainControl.Main;
        }

        private static string Describe(Inventory inventory)
            => inventory != null ? inventory.name : "<null>";

        private static string DescribeSlot(Slot slot)
            => slot != null ? slot.Key : "<null>";
    }
}
