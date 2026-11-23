#pragma once

#include <cstdint>
#include <functional>
#include <string>
#include <unordered_map>
#include <utility>
#include <vector>

namespace coop {

using ItemUid = std::uint32_t;

/// One server-owned attachment on an item. `slotKey` is the native `Item.Slots` key; the
/// server never interprets it, it only stores/replays it with the item.
struct ItemAttachment {
    std::string slotKey;
    std::uint32_t itemId = 0;
};

struct ItemInstance {
    ItemUid uid = 0;
    std::uint32_t itemId = 0;
    std::uint32_t count = 0;
    // P1 phase 3 side-channel state. Defaults keep every old item unchanged; the detail is
    // carried by SItemDetail instead of widening InventoryEntry.
    std::uint32_t durability = 0;
    std::uint32_t maxDurability = 0;
    std::uint32_t loadedAmmoItemId = 0;
    std::uint32_t loadedAmmoCount = 0;
    // Contract 2.14.0: pre-installed attachments (loadout tickets today, loot later). They
    // travel with the uid across containers and are replayed through SItemMods.
    std::vector<ItemAttachment> mods;
};

/// Authoritative item ledger: the ONLY place that may move items between containers.
/// Invariant: an ItemUid is in at most one container slot at any time.
class ItemLedger {
public:
    static constexpr ItemUid kNoItem = 0;

    /// Failure reason of an atomic Transfer. `Ok` means the item moved and both containers
    /// advanced their revision.
    enum class TransferError : std::uint8_t {
        Ok = 0,
        MissingContainer,
        MissingItem,
        SourceMismatch,
        ExpectedItemMismatch,
        RevisionMismatch,
        CountMismatch,
        TargetSlotOutOfRange,
        TargetSlotBusy,
    };

    struct TransferResult {
        TransferError error = TransferError::Ok;
        std::uint32_t sourceRevision = 0;
        std::uint32_t targetRevision = 0;
        /// P1: uid created by a split transfer (0 when the source uid itself moved/merged).
        ItemUid newItemUid = kNoItem;
        /// P1: how many units actually moved/merged (0 for a no-op, <= requested count).
        std::uint32_t movedCount = 0;

        bool Ok() const { return error == TransferError::Ok; }
    };

    /// P1: final layout submitted by the client for a container arrange/sort. The server
    /// validates uid identity, per-itemId count conservation and max stack before applying.
    struct ArrangeEntry {
        std::uint16_t slot = 0;
        ItemUid uid = kNoItem;
        std::uint32_t itemId = 0;
        std::uint32_t count = 0;
    };

    enum class ArrangeError : std::uint8_t {
        Ok = 0,
        MissingContainer,
        RevisionMismatch,
        SlotOutOfRange,
        DuplicateSlot,
        UnknownItem,
        ItemIdMismatch,
        DuplicateItem,
        InvalidCount,
        CountMismatch,
        StackOverflow,
    };

    /// P1 consume: decrement or destroy one stack in place (food/drug/ammo/durability).
    struct ConsumeResult {
        TransferError error = TransferError::Ok;
        std::uint32_t sourceRevision = 0;
        std::uint32_t remaining = 0;

        bool Ok() const { return error == TransferError::Ok; }
    };

    ItemUid CreateItem(std::uint32_t itemId, std::uint32_t count);
    bool CreateContainer(std::uint32_t containerId, std::uint16_t capacity);
    /// Removes a container and every item it owns (used to release corpse containers when a
    /// raid is torn down; corpse ids are reused by the next raid).
    bool DestroyContainer(std::uint32_t containerId);
    /// P1 phase 3: shrink/grow an existing container without moving items. Shrinking fails
    /// when a slot >= capacity is occupied. Successful resize bumps the container revision.
    bool ResizeContainer(std::uint32_t containerId, std::uint16_t capacity);
    /// P1 phase 3: update the durability/loaded-ammo side channel for one item.
    bool SetItemDetail(ItemUid itemUid, std::uint32_t durability, std::uint32_t maxDurability,
                       std::uint32_t loadedAmmoItemId, std::uint32_t loadedAmmoCount);
    /// Contract 2.14.0: replace the attachment list of one item. Attachments are part of the
    /// item, so they follow the uid through transfers; they are cleared with DestroyItem.
    bool SetItemMods(ItemUid itemUid, std::vector<ItemAttachment> mods);

    /// Contract 2.15.0: remove one attachment by slot key and report its item id. Returns
    /// false when the item or the slot key is unknown.
    bool RemoveItemMod(ItemUid itemUid, const std::string& slotKey, std::uint32_t& outItemId);

    /// P1 phase 3b: atomically consume `count` units of ammoItemId from `backpackContainer`
    /// and set the gun's loaded ammo detail. Fails with no mutation when the backpack does
    /// not hold enough ammo or the gun does not exist.
    struct ReloadResult {
        TransferError error = TransferError::Ok;
        std::uint32_t backpackRevision = 0;
        std::uint32_t loadedCount = 0;
        bool Ok() const { return error == TransferError::Ok; }
    };
    ReloadResult ReloadAmmo(std::uint32_t backpackContainer, ItemUid gunUid,
                            std::uint32_t ammoItemId, std::uint32_t count,
                            std::uint32_t loadedAfter, std::uint32_t loadedAmmoMaxStack = 1);

    /// Contract 2.16.0: add `count` units of `itemId` to a container, merging into existing
    /// partial stacks first (like a native pickup) and creating new stacks in free slots for
    /// the remainder. Returns the number of units actually added (0 when the container has no
    /// room). Bumps the container revision when anything was added. Used to return a gun's
    /// magazine rounds to the backpack when its magazine attachment is unplugged.
    std::uint32_t AddCount(std::uint32_t containerId, std::uint32_t itemId, std::uint32_t count,
                           std::uint32_t maxStack);

    /// Per-container content revision (0 when the container does not exist). Ticket seeding
    /// starts at 1, every successful mutation increments it.
    std::uint32_t Revision(std::uint32_t containerId) const;
    bool SetRevision(std::uint32_t containerId, std::uint32_t revision);

    bool TryPut(std::uint32_t containerId, ItemUid item, std::uint16_t preferSlot, std::uint16_t& placedSlot);
    bool TryTake(std::uint32_t containerId, std::uint16_t slot, ItemUid& out);
    bool Move(ItemUid item, std::uint32_t toContainer, std::uint16_t preferSlot);

    /// Atomic v9 transfer: validates the item id/count/source slot/revision and the target
    /// slot, then moves the item and bumps both revisions. Any failure leaves the ledger
    /// untouched (no partial take/put).
    TransferResult Transfer(ItemUid item, std::uint32_t sourceContainer, std::uint16_t sourceSlot,
                            std::uint32_t targetContainer, std::uint16_t targetSlot,
                            std::uint32_t expectedItemId, std::uint32_t expectedRevision,
                            std::uint32_t count);

    /// P1: transfer that also supports partial counts (split into a new uid) and merging into
    /// an occupied target slot of the same itemId up to `maxStack`. Any failure leaves the
    /// ledger untouched. `maxStack <= 1` disables merging.
    TransferResult TransferEx(ItemUid item, std::uint32_t sourceContainer,
                              std::uint16_t sourceSlot, std::uint32_t targetContainer,
                              std::uint16_t targetSlot, std::uint32_t expectedItemId,
                              std::uint32_t expectedRevision, std::uint32_t count,
                              std::uint32_t maxStack);

    /// P1: replace a container's whole layout (sort/merge/reorder). Validates every uid,
    /// slot, max stack and per-itemId count conservation before mutating. Uids absent from
    /// the layout are treated as merged away and destroyed. Bumps revision on success.
    ArrangeError Arrange(std::uint32_t containerId, std::uint32_t expectedRevision,
                         const std::vector<ArrangeEntry>& layout,
                         const std::function<std::uint32_t(std::uint32_t)>& maxStackForItem);

    /// P1: consume `count` units from a stack. When the last unit is consumed the item is
    /// removed from its slot and destroyed. Failure leaves the ledger untouched.
    ConsumeResult Consume(ItemUid item, std::uint32_t containerId, std::uint16_t slot,
                          std::uint32_t expectedItemId, std::uint32_t expectedRevision,
                          std::uint32_t count);

    bool Locate(ItemUid item, std::uint32_t& containerId, std::uint16_t& slot) const;
    const ItemInstance* GetItem(ItemUid item) const;
    bool Contains(ItemUid item) const;
    std::uint32_t ItemCount() const { return static_cast<std::uint32_t>(_items.size()); }

    /// Removes an item that is not placed in any container (world-item cleanup). Items that
    /// still occupy a slot are kept and reported as false.
    bool DestroyItem(ItemUid item);

    /// True when at least one slot of the container is occupied.
    bool AnyItem(std::uint32_t containerId) const;

    /// Drop every item that sits in the given container (used between raids).
    void ClearContainer(std::uint32_t containerId);

    /// Read-only slot view; nullptr when the container does not exist.
    const std::vector<ItemUid>* Slots(std::uint32_t containerId) const;
    std::uint16_t Capacity(std::uint32_t containerId) const;

private:
    struct Container {
        std::uint16_t capacity = 0;
        std::uint32_t revision = 0;
        std::vector<ItemUid> slots;
    };

    /// Slot mutations without revision bookkeeping; callers bump the container revision.
    bool DoPut(std::uint32_t containerId, ItemUid item, std::uint16_t preferSlot, std::uint16_t& placedSlot);
    bool DoTake(std::uint32_t containerId, std::uint16_t slot, ItemUid& out);

    ItemUid _nextUid = 1;
    std::unordered_map<ItemUid, ItemInstance> _items;
    std::unordered_map<ItemUid, std::pair<std::uint32_t, std::uint16_t>> _locations;
    std::unordered_map<std::uint32_t, Container> _containers;
};

} // namespace coop
