using System;
using System.Collections.Generic;
using Duckov.Utilities;
using DuckovCoop.Client;
using HarmonyLib;
using ItemStatsSystem;
using UnityEngine;

namespace DuckovCoopClient
{
    /// <summary>
    /// P1 phase 3c: replace native reload with a server-authoritative CItemReload. Native always
    /// moved ammo with Item.Split, which the T4 guard blocks; here the server deducts the ammo,
    /// then the client adds the already-paid stack to the gun's local inventory (instant load,
    /// no local split/detach). Native reload is skipped entirely.
    /// </summary>
    public sealed partial class CoopRuntime
    {
        private bool _ammoReloadPending;
        private bool _ammoReloadArmed;
        private ulong _ammoReloadCorrelation;
        private uint _ammoReloadGunUid;
        private uint _ammoReloadAmmoItemId;
        private uint _ammoReloadCount;
        private uint _ammoReloadLoadedAfter;
        private float _ammoReloadSince;
        /// <summary>Contract 2.16.0: this reload replaces the magazine with another ammo type.</summary>
        private bool _ammoReloadSwitch;
        /// <summary>Ammo type that was loaded before the switch (returns to the backpack).</summary>
        private uint _ammoReloadSwitchFromType;
        /// <summary>True while the native type switch of a server-driven reload may run.</summary>
        private bool _allowNativeTypeSwitch;
        /// <summary>Last magazine state the server reported per gun (type, count).</summary>
        private readonly Dictionary<uint, (int Type, int Count)> _serverMagazineByGun =
            new Dictionary<uint, (int Type, int Count)>();
        private float _lastMagazineEnforce;
        /// <summary>Do not enforce the server magazine while a native type switch is settling.</summary>
        private float _nativeSwitchGraceUntil;
        /// <summary>True once the native reload pulled rounds from the stack for this attempt.</summary>
        private bool _ammoReloadPullSeen;
        /// <summary>Last server-acked loaded ammo type per gun uid.</summary>
        private readonly Dictionary<uint, uint> _loadedAmmoTypeByGun =
            new Dictionary<uint, uint>();
        private int _lastItemReloadCount;
        private const float ReloadPresentationSeconds = 0.8f;
        private float _ammoReloadStartedAt;
        private float _ammoReloadApplyAt;
        private ItemReloadResult _ammoReloadReadyResult;

        internal bool AmmoReloadActive => _ammoReloadPending;
        internal bool AmmoReloadArmed => _ammoReloadArmed;
        internal float AmmoReloadProgress
        {
            get
            {
                if (!_ammoReloadPending)
                    return 0f;
                var duration = Mathf.Max(0.01f, ReloadPresentationSeconds);
                return Mathf.Clamp01((Time.unscaledTime - _ammoReloadStartedAt) / duration);
            }
        }

        internal void OnGunUsedBullet(ItemSetting_Gun setting)
        {
            if (!_inventoryMirrorActive || _client == null ||
                _client.State != ClientState.InGame || !_inRaidScene || setting == null)
                return;
            var gunItem = setting.Item;
            if (gunItem == null || !_serverUidByItem.TryGetValue(gunItem, out var gunUid))
                return;
            var character = gunItem.GetCharacterMainControl();
            if (character != CharacterMainControl.Main)
                return;

            var current = setting.GetCurrentLoadedBullet();
            var ammoItemId = current != null ? current.TypeID : setting.TargetBulletID;
            if (ammoItemId <= 0)
                return;
            var remaining = setting.GetBulletCount();
            if (remaining < 0)
                return;
            _client.SendItemWeaponFire(gunUid, (uint)ammoItemId, (uint)remaining);
        }

        internal bool TryBeginServerReload(CharacterMainControl character, Item preferredBullet)
        {
            if (!_inventoryMirrorActive || _client == null ||
                _client.State != ClientState.InGame || character == null ||
                character != CharacterMainControl.Main)
                return false;
            if (_ammoReloadPending || _pendingItemTransfer != 0 ||
                _awaitingInventorySnapshot || _pendingItemArrange != 0)
            {
                LogBlockedNativeMutation("reload deferred: another item op is pending");
                return true;
            }

            var gun = character.agentHolder != null ? character.agentHolder.CurrentHoldGun : null;
            if (gun == null || gun.Item == null)
                return false;
            if (!_serverUidByItem.TryGetValue(gun.Item, out var gunUid))
                return false;

            var setting = gun.GunItemSetting;
            if (setting == null)
                return false;

            // Ammo-type switches are blocked while the magazine still holds another type: the
            // native unload would create a local-only item (server never sees it), which is how
            // a full magazine of advanced AP disappeared. Neither GetCurrentLoadedBullet() nor
            // TargetBulletID is reliable here (both are stale after our server-acked sync), so
            // read the rounds that are actually in the gun's magazine inventory.
            var current = setting.GetCurrentLoadedBullet();
            var ammoItemId = current != null ? current.TypeID : setting.TargetBulletID;
            var switchFromType = 0;
            if (preferredBullet != null)
            {
                if (current != null && current.TypeID != preferredBullet.TypeID)
                {
                    // Contract 2.16.0: reloading another ammo type replaces the magazine. The
                    // server returns the loaded rounds to the backpack and charges the new
                    // type, so the native type switch (which dumps them locally) may run.
                    switchFromType = current.TypeID;
                }
                ammoItemId = preferredBullet.TypeID;
            }
            // The native T-key selector (BulletTypeHUD.SetBulletType → SetTargetBulletType, then
            // TryToReload) dumps the magazine BEFORE the reload starts, so `current` is already
            // null here. The server still tracks the old loaded type, and asking for a different
            // type means a magazine replacement: old rounds return to the backpack, new type is
            // charged. Compare against the server-known type, not the native magazine.
            if (switchFromType == 0 && ammoItemId > 0 &&
                _loadedAmmoTypeByGun.TryGetValue(gunUid, out var serverLoadedType) &&
                serverLoadedType != 0 && serverLoadedType != (uint)ammoItemId)
            {
                switchFromType = (int)serverLoadedType;
            }
            if (ammoItemId <= 0)
            {
                setting.AutoSetTypeInInventory(character.CharacterItem.Inventory);
                ammoItemId = setting.TargetBulletID;
            }
            if (ammoItemId <= 0)
                return false; // no ammo type known: let native show its out-of-ammo feedback

            var magazineRounds = 0;
            var magazineType = 0;
            if (gun.Item.Inventory != null)
            {
                foreach (var child in gun.Item.Inventory)
                {
                    if (child == null)
                        continue;
                    magazineRounds += child.StackCount;
                    if (magazineType == 0)
                        magazineType = child.TypeID;
                }
            }
            if (switchFromType == 0 && magazineRounds > 0 && magazineType != 0 &&
                magazineType != ammoItemId)
            {
                LogBlockedNativeMutation(
                    $"reload blocked: ammo switch loaded={magazineType} " +
                    $"requested={ammoItemId} rounds={magazineRounds}");
                return true;
            }

            // A switch empties the magazine first, so the new load starts from zero.
            var before = switchFromType != 0 ? 0 : Math.Max(0, setting.BulletCount);
            var capacity = setting.Capacity;
            if (capacity <= before)
                return false;

            uint available = 0;
            foreach (var entry in _client.InventoryMirror.Entries)
            {
                if (entry.Kind == 1 && entry.ItemId == (uint)ammoItemId)
                    available += entry.Count;
            }
            var missing = (uint)Math.Max(0, capacity - before);
            var count = Math.Min(missing, available);
            if (count == 0)
                return false; // native out-of-ammo poptext is fine; server also has none

            var loadedAfter = (uint)before + count;
            // Do not send CItemReload yet. Native reload can still be cancelled with X;
            // the server must not charge reserve ammo until the native progress reaches
            // its load point. Send the prepared reload from PrefixStartLoadBullets.
            _ammoReloadArmed = true;
            _ammoReloadPending = false;
            _ammoReloadCorrelation = 0;
            _ammoReloadGunUid = gunUid;
            _ammoReloadAmmoItemId = (uint)ammoItemId;
            _ammoReloadCount = count;
            _ammoReloadLoadedAfter = loadedAfter;
            _ammoReloadSince = Time.unscaledTime;
            _ammoReloadPullLogged = false;
            _ammoReloadPullSeen = false;
            _ammoReloadSwitch = switchFromType != 0;
            _ammoReloadSwitchFromType = (uint)switchFromType;
            _allowNativeTypeSwitch = _ammoReloadSwitch;
            Log($"[ammo] reload armed: gun={gunUid} ammo={ammoItemId} " +
                $"count={count} loadedAfter={loadedAfter} " +
                (switchFromType != 0 ? $"switchFrom={switchFromType} " : string.Empty) +
                $"reserveMirror={ReserveFromMirror((uint)ammoItemId)} " +
                $"reserveNative={ReserveFromNative((uint)ammoItemId)}");
            return true;
        }

        private bool _ammoReloadNativeEnded;

        internal bool ShouldInterceptNativeReload(ItemAgent_Gun gun)
        {
            if ((!_ammoReloadArmed && !_ammoReloadPending) ||
                gun == null || gun.Item == null)
                return false;
            return _serverUidByItem.TryGetValue(gun.Item, out var uid) &&
                   uid == _ammoReloadGunUid;
        }

        internal void OnNativeReloadStartLoad(ItemAgent_Gun gun)
        {
            if (!ShouldInterceptNativeReload(gun) || !_ammoReloadArmed)
                return;
            var correlation = ++_ammoReloadCorrelation;
            if (!_client.SendItemReload(correlation, _ammoReloadGunUid,
                    _ammoReloadAmmoItemId, _ammoReloadCount, _ammoReloadLoadedAfter))
            {
                _ammoReloadArmed = false;
                LogBlockedNativeMutation("[ammo] reload send at load point failed");
                return;
            }
            _ammoReloadArmed = false;
            _ammoReloadPending = true;
            _ammoReloadNativeEnded = true;
            _ammoReloadSince = Time.unscaledTime;
            Log($"[ammo] reload commit sent at native load point corr={correlation} " +
                $"gun={_ammoReloadGunUid} ammo={_ammoReloadAmmoItemId} " +
                $"count={_ammoReloadCount} loadedAfter={_ammoReloadLoadedAfter}");
        }

        internal void OnNativeReloadCanceled(ItemAgent_Gun gun)
        {
            if (!_ammoReloadArmed || !ShouldInterceptNativeReload(gun))
                return;
            var wasSwitch = _ammoReloadSwitch;
            _ammoReloadArmed = false;
            _ammoReloadCorrelation = 0;
            _allowNativeTypeSwitch = false;
            Log($"[ammo] reload canceled before commit gun={_ammoReloadGunUid}");
            if (wasSwitch)
            {
                AbortAmmoSwitch("cancel");
                return;
            }
            _ammoReloadSwitch = false;
            // The native reload may already have pulled rounds out of the stack (and put them in
            // the gun's magazine). The server was never charged, so roll the magazine back to the
            // last server detail and ask for a snapshot to put the backpack stack back.
            if (_ammoReloadPullSeen)
            {
                RollbackGunMagazine(gun);
                RequestAmmoResync("cancel");
            }
        }

        /// <summary>
        /// Put the gun's magazine back to the last server-known loaded state. Without this the
        /// native-pulled rounds stay in the gun while the resync also restores the backpack
        /// stack, so the HUD reserve shows more rounds than the player owns.
        /// </summary>
        private void RollbackGunMagazine(ItemAgent_Gun gun)
        {
            if (gun == null || gun.Item == null || _client == null)
                return;
            if (!_serverUidByItem.TryGetValue(gun.Item, out var gunUid))
                return;
            var inventory = gun.Item.Inventory;
            if (inventory == null)
                return;

            var children = new List<Item>();
            foreach (var child in inventory)
            {
                if (child != null)
                    children.Add(child);
            }
            foreach (var child in children)
                child.DestroyTree();

            var loaded = 0u;
            var ammoId = 0u;
            if (_client.ItemDetails.TryGetValue(gunUid, out var detail) && detail != null)
            {
                loaded = detail.LoadedAmmoCount;
                ammoId = detail.LoadedAmmoItemId;
            }
            if (loaded > 0 && ammoId != 0)
            {
                var add = ItemAssetsCollection.InstantiateSync(unchecked((int)ammoId));
                if (add != null)
                {
                    add.StackCount = (int)loaded;
                    add.Inspected = true;
                    if (!inventory.AddAndMerge(add))
                        add.DestroyTree();
                }
            }
            var setting = gun.GunItemSetting;
            if (setting != null)
            {
                AccessTools.Field(typeof(ItemSetting_Gun), "_bulletCountCache")
                    ?.SetValue(setting, -1);
            }
            Log($"[ammo] magazine rolled back uid={gunUid} loaded={loaded} ammo={ammoId}");
        }

        private void RollbackGunMagazineByUid(uint gunUid)
        {
            if (_serverUidToItem.TryGetValue(gunUid, out var item) && item != null &&
                item.ActiveAgent is ItemAgent_Gun mapped)
            {
                RollbackGunMagazine(mapped);
                return;
            }
            var main = CharacterMainControl.Main;
            var held = main != null && main.agentHolder != null
                ? main.agentHolder.CurrentHoldGun
                : null;
            if (held != null && held.Item != null &&
                _serverUidByItem.TryGetValue(held.Item, out var heldUid) && heldUid == gunUid)
                RollbackGunMagazine(held);
        }

        private void RequestAmmoResync(string reason)
        {
            _ammoReloadPullSeen = false;
            if (_client == null || _client.State != ClientState.InGame)
                return;
            if (_client.SendInventorySync(++_inventorySyncCorrelation, 1, _client.PlayerId))
                Log($"[ammo] inventory resync requested reason={reason}");
        }

        /// <summary>
        /// The gun item the player is currently holding, when it is the server item
        /// <paramref name="uid"/>. Inventory rebuilds can leave the map on a stale copy, so the
        /// held agent's item is preferred whenever it resolves to the same uid.
        /// </summary>
        private Item HeldGunItem(uint uid)
        {
            var main = CharacterMainControl.Main;
            var held = main != null && main.agentHolder != null
                ? main.agentHolder.CurrentHoldGun
                : null;
            if (held == null || held.Item == null)
                return null;
            return _serverUidByItem.TryGetValue(held.Item, out var heldUid) && heldUid == uid
                ? held.Item
                : null;
        }

        /// <summary>
        /// Backstop for the server-authoritative magazine: when the last server detail says the gun
        /// holds fewer rounds (or a different type) than the native magazine, force the native side
        /// to match. Needed because some native paths (unplugging a magazine that was re-attached,
        /// inventory rebuilds) leave the held copy loaded after the detail was applied elsewhere.
        /// </summary>
        private void EnforceServerMagazine(float now)
        {
            if (now - _lastMagazineEnforce < 0.5f)
                return;
            _lastMagazineEnforce = now;
            if (_client == null || _client.State != ClientState.InGame || !_inRaidScene)
                return;
            if (_ammoReloadArmed || _ammoReloadPending)
                return; // our own reload/switch is in flight; do not fight it
            if (now < _nativeSwitchGraceUntil)
                return; // the native type switch just dumped the magazine; CItemReload follows

            var main = CharacterMainControl.Main;
            var held = main != null && main.agentHolder != null
                ? main.agentHolder.CurrentHoldGun
                : null;
            if (held == null || held.Item == null || held.Item.Inventory == null)
                return;
            if (!_serverUidByItem.TryGetValue(held.Item, out var gunUid))
                return;
            if (!_serverMagazineByGun.TryGetValue(gunUid, out var server))
                return;

            var rounds = 0;
            var type = 0;
            foreach (var child in held.Item.Inventory)
            {
                if (child == null)
                    continue;
                rounds += child.StackCount;
                if (type == 0)
                    type = child.TypeID;
            }
            ItemSetting_Gun setting = null;
            var reported = -1;
            try
            {
                setting = held.Item.GetComponent<ItemSetting_Gun>();
                if (setting != null)
                    reported = setting.BulletCount;
            }
            catch
            {
                setting = null;
            }
            var stacksMatch = rounds == server.Count && (rounds == 0 || type == server.Type);
            // A rebuilt gun can carry a stale serialized magazine count while its stacks are
            // already correct — the HUD then shows the old number forever, so compare it too.
            if (stacksMatch && (setting == null || reported == server.Count))
                return;

            var removed = TrimGunMagazine(held.Item, server.Type, server.Count);
            var added = 0;
            if (server.Count > rounds - removed && server.Type != 0)
            {
                var missing = server.Count - Math.Max(0, rounds - removed);
                var ammo = ItemAssetsCollection.InstantiateSync(server.Type);
                if (ammo != null)
                {
                    ammo.StackCount = missing;
                    ammo.Inspected = true;
                    if (held.Item.Inventory.AddAndMerge(ammo))
                        added = missing;
                    else
                        ammo.DestroyTree();
                }
            }
            SetBulletCountCache(held.Item, server.Count);
            Log($"[ammo] enforced server magazine gun={gunUid} native={type}x{rounds} " +
                $"reported={reported} server={server.Type}x{server.Count} " +
                $"removed={removed} added={added}");
        }

        /// <summary>
        /// Contract 2.16.0: an aborted ammo-type switch already dumped the old rounds into the
        /// local inventory (the native <c>SetTargetBulletType</c> runs before the load point),
        /// but the server still has them loaded. Restore the magazine from the last server
        /// detail and resync the backpack so the dump is removed again.
        /// </summary>
        private void AbortAmmoSwitch(string reason)
        {
            if (!_ammoReloadSwitch)
                return;
            _allowNativeTypeSwitch = false;
            _ammoReloadSwitch = false;
            RollbackGunMagazineByUid(_ammoReloadGunUid);
            RequestAmmoResync(reason);
            Log($"[ammo] switch aborted ({reason}); magazine restored from the server");
        }

        private void UpdateAmmoReload()
        {
            if (!_ammoReloadPending)
            {
                if (_ammoReloadArmed)
                {
                    // Switching away from the gun mid-reload does not always call CancleReload.
                    // Treat "no longer holding it" as an interruption so the pulled rounds are
                    // rolled back instead of leaking into the magazine.
                    var main = CharacterMainControl.Main;
                    var held = main != null && main.agentHolder != null
                        ? main.agentHolder.CurrentHoldGun
                        : null;
                    var stillHolding = held != null && held.Item != null &&
                                       _serverUidByItem.TryGetValue(held.Item, out var heldUid) &&
                                       heldUid == _ammoReloadGunUid;
                    if (!stillHolding && Time.unscaledTime - _ammoReloadSince > 0.5f)
                    {
                        var wasSwitch = _ammoReloadSwitch;
                        _ammoReloadArmed = false;
                        _allowNativeTypeSwitch = false;
                        Log($"[ammo] reload interrupted by weapon switch gun={_ammoReloadGunUid}");
                        if (wasSwitch)
                        {
                            AbortAmmoSwitch("switch");
                        }
                        else if (_ammoReloadPullSeen)
                        {
                            RollbackGunMagazineByUid(_ammoReloadGunUid);
                            RequestAmmoResync("switch");
                        }
                        return;
                    }
                }
                // LMG/box-mag reloads animate for 4-6s before their load point, and the native
                // ammo pull can happen mid-animation; too short a window aborts the reload.
                if (_ammoReloadArmed && Time.unscaledTime - _ammoReloadSince > 10f)
                {
                    var wasSwitch = _ammoReloadSwitch;
                    _ammoReloadArmed = false;
                    _allowNativeTypeSwitch = false;
                    LogBlockedNativeMutation("[ammo] armed reload expired without native load");
                    if (wasSwitch)
                    {
                        AbortAmmoSwitch("expired");
                    }
                    else if (_ammoReloadPullSeen)
                    {
                        RollbackGunMagazineByUid(_ammoReloadGunUid);
                        RequestAmmoResync("expired");
                    }
                }
                return;
            }
            if (_client == null)
            {
                _ammoReloadPending = false;
                _ammoReloadArmed = false;
                _allowNativeTypeSwitch = false;
                _ammoReloadSwitch = false;
                return;
            }
            if (_client.ItemReloadCount != _lastItemReloadCount)
            {
                _lastItemReloadCount = _client.ItemReloadCount;
                var result = _client.LastItemReload;
                if (result != null && result.CorrelationId == _ammoReloadCorrelation)
                {
                    _ammoReloadPending = false;
                    _ammoReloadNativeEnded = false;
                    if (result.Code != 0)
                    {
                        _allowNativeTypeSwitch = false;
                        _ammoReloadSwitch = false;
                        LogBlockedNativeMutation(
                            $"[ammo] reload rejected code={result.Code} gun={_ammoReloadGunUid}");
                        return;
                    }
                    ApplyServerReload(result);
                }
            }
            if (_ammoReloadPending && Time.unscaledTime - _ammoReloadSince > 3f)
            {
                var wasSwitch = _ammoReloadSwitch;
                _ammoReloadPending = false;
                _ammoReloadNativeEnded = false;
                _allowNativeTypeSwitch = false;
                LogBlockedNativeMutation("[ammo] reload result timeout; native state untouched");
                if (wasSwitch)
                    AbortAmmoSwitch("result timeout");
                else
                    _ammoReloadSwitch = false;
            }
        }

        private void ApplyServerReload(ItemReloadResult result)
        {
            if (!_serverUidToItem.TryGetValue(result.GunUid, out var gunItem) || gunItem == null)
            {
                Log($"[ammo] reload ack but gun item not mapped uid={result.GunUid}");
                return;
            }
            var gun = gunItem.ActiveAgent as ItemAgent_Gun;
            // Prefer the actually held gun. After an inventory rebuild the mapped item can be
            // a backpack copy while the held agent is a different native object.
            var main = CharacterMainControl.Main;
            var held = main != null && main.agentHolder != null
                ? main.agentHolder.CurrentHoldGun
                : null;
            if (held != null && held.Item != null &&
                _serverUidByItem.TryGetValue(held.Item, out var heldUid) &&
                heldUid == result.GunUid)
                gun = held;
            if (gun == null)
            {
                var heldGun = main != null && main.agentHolder != null
                    ? main.agentHolder.CurrentHoldGun
                    : null;
                if (heldGun != null && heldGun.Item == gunItem)
                    gun = heldGun;
            }
            if (gun == null)
            {
                Log($"[ammo] reload ack but no gun agent uid={result.GunUid}");
                return;
            }

            var setting = gun.GunItemSetting;
            if (setting == null)
                return;
            var current = setting.GetCurrentLoadedBullet();
            if (current != null && current.TypeID != (int)result.AmmoItemId)
            {
                // Contract 2.16.0: a replaced magazine ends the reload with the NEW type on the
                // server. The native type switch already ran locally (its dump was allowed), so
                // any leftover rounds of the old type are dropped here; the server already
                // returned them to the backpack, which the next snapshot applies.
                if (_ammoReloadSwitch)
                {
                    var cleared = ClearOtherAmmoStacks(gunItem, (int)result.AmmoItemId);
                    if (cleared > 0)
                        Log($"[ammo] switch cleared native magazine from={current.TypeID} " +
                            $"rounds={cleared}");
                }
                LogBlockedNativeMutation(
                    $"[ammo] reload ack type mismatch current={current.TypeID} " +
                    $"ack={result.AmmoItemId}; local load skipped");
                return;
            }

            var ammo = ItemAssetsCollection.InstantiateSync(unchecked((int)result.AmmoItemId));
            if (ammo == null)
            {
                Log($"[ammo] reload ammo prefab missing item={result.AmmoItemId}");
                return;
            }
            ammo.DestroyTree(); // only used to validate the prefab; SyncGunMagazine creates the delta

            if (current == null)
                setting.SetTargetBulletType(unchecked((int)result.AmmoItemId));
            var delta = SyncGunMagazine(gunItem, (int)result.AmmoItemId, result.LoadedCount);
            if (delta == int.MinValue)
            {
                Log($"[ammo] local ammo sync failed gun={result.GunUid} " +
                    $"item={result.AmmoItemId} target={result.LoadedCount}");
                return;
            }
            var switched = _ammoReloadSwitch;
            var switchedFrom = _ammoReloadSwitchFromType;
            _allowNativeTypeSwitch = false;
            _ammoReloadSwitch = false;
            if (switched)
            {
                // The native dump and the server-side return both landed; reconcile the backpack
                // with the authoritative count once more so ordering cannot duplicate rounds.
                RequestAmmoResync("ammo switch");
                Log($"[ammo] switch applied gun={result.GunUid} from={switchedFrom} " +
                    $"to={result.AmmoItemId} loaded={result.LoadedCount}");
            }
            if (result.LoadedCount > 0)
                _loadedAmmoTypeByGun[result.GunUid] = result.AmmoItemId;
            else
                _loadedAmmoTypeByGun.Remove(result.GunUid);
            // ItemSetting_Gun caches bulletCount until a native reload invalidates it. Our
            // local add bypasses that path, so write the authoritative count explicitly.
            SetBulletCountCache(gunItem, (int)result.LoadedCount);
            _serverMagazineByGun[result.GunUid] = ((int)result.AmmoItemId, (int)result.LoadedCount);
            // Native reload normally ends in TransToReady. Our server ack bypasses the native
            // action, so an empty gun stays in "empty" state and ignores the trigger even
            // though the local magazine now has bullets (switching weapons masked this).
            if (gun.GunState == ItemAgent_Gun.GunStates.empty)
            {
                var transToReady = AccessTools.Method(typeof(ItemAgent_Gun), "TransToReady");
                transToReady?.Invoke(gun, null);
                Log($"[ammo] reload ready state gun={result.GunUid} from=empty");
            }
            Log($"[ammo] reload applied gun={result.GunUid} item={result.AmmoItemId} " +
                $"requested={_ammoReloadCount} syncDelta={delta} loaded={result.LoadedCount} " +
                $"backpackRev={result.BackpackRevision} " +
                $"reserveMirror={ReserveFromMirror(result.AmmoItemId)} " +
                $"reserveNative={ReserveFromNative(result.AmmoItemId)}");
        }

        /// <summary>
        /// Remove every magazine stack whose ammo type is not <paramref name="keepType"/>.
        /// Returns the number of rounds destroyed (they were already returned server-side).
        /// </summary>
        private static int ClearOtherAmmoStacks(Item gunItem, int keepType)
        {
            if (gunItem == null || gunItem.Inventory == null)
                return 0;
            var doomed = new List<Item>();
            var rounds = 0;
            foreach (var child in gunItem.Inventory)
            {
                if (child == null || child.TypeID == keepType)
                    continue;
                doomed.Add(child);
                rounds += child.StackCount;
            }
            foreach (var item in doomed)
                item.DestroyTree();
            return rounds;
        }

        /// <summary>Rounds of <paramref name="ammoItemId"/> the server says are in the backpack.</summary>
        private int ReserveFromMirror(uint ammoItemId)
        {
            if (_client == null || ammoItemId == 0)
                return 0;
            var total = 0;
            foreach (var entry in _client.InventoryMirror.Entries)
            {
                if (entry.Kind == 1 && entry.ItemId == ammoItemId)
                    total += (int)entry.Count;
            }
            return total;
        }

        /// <summary>Rounds of <paramref name="ammoItemId"/> actually in the native backpack.</summary>
        private static int ReserveFromNative(uint ammoItemId)
        {
            if (ammoItemId == 0)
                return 0;
            var backpack = MainBackpack();
            if (backpack == null)
                return 0;
            var total = 0;
            foreach (var child in backpack)
            {
                if (child != null && child.TypeID == unchecked((int)ammoItemId))
                    total += child.StackCount;
            }
            return total;
        }

        /// <summary>
        /// Force the native gun's ammo stacks to exactly target rounds. The server detail may
        /// already have restored the magazine before the ack snapshot arrives, so adding the
        /// requested count blindly would double-load it (MP5 showed 21/22 instead of 20).
        /// </summary>
        private static int SyncGunMagazine(Item gunItem, int ammoItemId, uint target)
        {
            if (gunItem == null || gunItem.Inventory == null)
                return int.MinValue;
            var items = new List<Item>();
            var current = 0;
            foreach (var child in gunItem.Inventory)
            {
                if (child == null || child.TypeID != ammoItemId)
                    continue;
                items.Add(child);
                current += child.StackCount;
            }

            var wanted = target > int.MaxValue ? int.MaxValue : (int)target;
            if (current < wanted)
            {
                var add = ItemAssetsCollection.InstantiateSync(ammoItemId);
                if (add == null)
                    return int.MinValue;
                add.StackCount = wanted - current;
                add.Inspected = true;
                if (!gunItem.Inventory.AddAndMerge(add))
                {
                    add.DestroyTree();
                    return int.MinValue;
                }
                return wanted - current;
            }

            var remove = current - wanted;
            if (remove <= 0)
                return 0;
            foreach (var item in items)
            {
                if (remove <= 0)
                    break;
                if (item.StackCount <= remove)
                {
                    remove -= item.StackCount;
                    item.DestroyTree();
                }
                else
                {
                    item.StackCount -= remove;
                    remove = 0;
                }
            }
            return -remove;
        }

        /// <summary>
        /// Contract 2.16.0: true when this gun setting belongs to the gun in the player's hands.
        /// The native drag-ammo-onto-a-gun gesture uses the reload action for a held gun
        /// (<c>CharacterReload</c>), which our reload guard turns into a server-side magazine
        /// replacement; only a stowed gun takes the async split/dump path we must refuse.
        /// </summary>
        internal static bool IsGunHeld(ItemSetting_Gun setting)
        {
            if (setting == null)
                return false;
            var item = setting.Item;
            if (item == null)
                return false;
            var agent = item.ActiveAgent as ItemAgent_Gun;
            return agent != null && agent.Holder != null;
        }

        /// <summary>
        /// Contract 2.16.0: a native ammo-type switch while the magazine still holds rounds
        /// dumps those rounds into the local inventory
        /// (<c>ItemSetting_Gun.SetTargetBulletType</c> → <c>TakeOutAllBullets</c>). The server
        /// never sees that dump, so the next inventory apply removes the local-only stack and
        /// the bullets disappear. The reload path already refuses such a switch for a held gun;
        /// this guard covers the inventory-UI path (dragging ammo onto a gun /
        /// <c>LoadSpecificBullet</c>) and clears nothing until the magazine is empty.
        /// </summary>
        internal static bool ShouldBlockAmmoSwitch(ItemSetting_Gun setting, int targetTypeId)
        {
            var runtime = Active;
            return runtime != null && runtime.ShouldBlockAmmoSwitchLocal(setting, targetTypeId);
        }

        private bool ShouldBlockAmmoSwitchLocal(ItemSetting_Gun setting, int targetTypeId)
        {
            if (!_inventoryMirrorActive || _client == null ||
                _client.State != ClientState.InGame || !_inRaidScene || setting == null)
                return false;
            if (_allowNativeTypeSwitch)
                return false; // server-driven magazine replacement: the native dump may run
            var gunItem = setting.Item;
            if (gunItem == null || !_serverUidByItem.ContainsKey(gunItem))
                return false; // native gun: untouched
            if (setting.TargetBulletID == targetTypeId)
                return false; // same selection: nothing is dumped
            if (gunItem.Inventory == null)
                return false;

            var rounds = 0;
            var roundType = 0;
            foreach (var child in gunItem.Inventory)
            {
                if (child == null)
                    continue;
                rounds += child.StackCount;
                if (roundType == 0)
                    roundType = child.TypeID;
            }
            if (rounds <= 0 || (targetTypeId >= 0 && roundType == targetTypeId))
                return false;

            // Native T-key selector on the gun in hand: BulletTypeHUD.SetBulletType sets the type
            // and immediately calls TryToReload, which the reload guard turns into a server-side
            // magazine replacement. Let the native dump run when the new type is available (the
            // server returns the dumped rounds, so nothing is lost).
            if (targetTypeId > 0 && IsGunHeld(setting) && ReserveFromMirror((uint)targetTypeId) > 0)
            {
                _nativeSwitchGraceUntil = Time.unscaledTime + 2f;
                Log($"[ammo] type switch via T selector: loaded={roundType} rounds={rounds} " +
                    $"requested={targetTypeId} (server replacement follows)");
                return false;
            }

            LogBlockedNativeMutation(
                $"[ammo] type switch blocked: loaded={roundType} rounds={rounds} " +
                $"requested={targetTypeId} (hold the gun to switch, or empty the magazine first)");
            return true;
        }
    }

    /// <summary>
    /// Contract 2.16.0: closes the inventory-UI ammo switch path. A switch to another ammo type
    /// while the server-owned magazine still holds rounds would dump them locally
    /// (<c>SetTargetBulletType</c> → <c>TakeOutAllBullets</c>) and the next server snapshot would
    /// delete them. The held-gun path is already covered by the CItemReload guard.
    /// </summary>
    internal static class AmmoSwitchGuard
    {
        private const string HarmonyId = "duckovcoop.ammo.switch";
        private static Harmony _harmony;

        internal static void Init()
        {
            if (_harmony != null)
                return;
            try
            {
                _harmony = new Harmony(HarmonyId);
                Patch(nameof(ItemSetting_Gun.LoadSpecificBullet), new[] { typeof(Item) },
                    nameof(BlockLoadSpecificBullet));
                Patch(nameof(ItemSetting_Gun.SetTargetBulletType), new[] { typeof(int) },
                    nameof(BlockSetTargetBulletType));
                CoopRuntime.Log("[ammo] type-switch guard installed");
            }
            catch (Exception ex)
            {
                CoopRuntime.Log($"[ammo] type-switch guard failed: {ex.GetType().Name}: {ex.Message}");
            }
        }

        private static void Patch(string method, Type[] args, string prefix)
        {
            var target = AccessTools.Method(typeof(ItemSetting_Gun), method, args);
            if (target == null)
            {
                CoopRuntime.Log($"[ammo] ItemSetting_Gun.{method} not found");
                return;
            }
            _harmony.Patch(target, prefix: new HarmonyMethod(typeof(AmmoSwitchGuard), prefix));
        }

        private static bool BlockLoadSpecificBullet(ItemSetting_Gun __instance, Item newBulletItem)
        {
            // Native SlotDisplay.OnDrop → SetAmmo → LoadSpecificBullet: a gun in hand routes to
            // CharacterReload (our reload guard handles the switch server-side); a stowed gun
            // would split/dump locally without a reload action, so that path stays refused.
            if (CoopRuntime.IsGunHeld(__instance))
                return true;
            return !CoopRuntime.ShouldBlockAmmoSwitch(__instance,
                newBulletItem != null ? newBulletItem.TypeID : -1);
        }

        private static bool BlockSetTargetBulletType(ItemSetting_Gun __instance, int typeID)
        {
            return !CoopRuntime.ShouldBlockAmmoSwitch(__instance, typeID);
        }
    }
}
