#include "world/ItemLedger.h"

#include <algorithm>

namespace coop {

ItemUid ItemLedger::CreateItem(std::uint32_t itemId, std::uint32_t count)
{
    const ItemUid uid = _nextUid++;
    _items[uid] = ItemInstance{uid, itemId, count};
    return uid;
}

bool ItemLedger::CreateContainer(std::uint32_t containerId, std::uint16_t capacity)
{
    if (_containers.find(containerId) != _containers.end() || capacity == 0)
        return false;

    _containers[containerId] = Container{capacity, 0, std::vector<ItemUid>(capacity, kNoItem)};
    return true;
}

bool ItemLedger::DestroyContainer(std::uint32_t containerId)
{
    auto it = _containers.find(containerId);
    if (it == _containers.end())
        return false;
    for (const ItemUid uid : it->second.slots) {
        if (uid == kNoItem)
            continue;
        _items.erase(uid);
        _locations.erase(uid);
    }
    _containers.erase(it);
    return true;
}

bool ItemLedger::ResizeContainer(std::uint32_t containerId, std::uint16_t capacity)
{
    auto it = _containers.find(containerId);
    if (it == _containers.end() || capacity == 0)
        return false;
    auto& container = it->second;
    if (capacity == container.capacity)
        return true;
    if (capacity < container.slots.size()) {
        for (std::size_t slot = capacity; slot < container.slots.size(); ++slot) {
            if (container.slots[slot] != kNoItem)
                return false;
        }
    }
    container.slots.resize(capacity, kNoItem);
    container.capacity = capacity;
    ++container.revision;
    return true;
}

bool ItemLedger::SetItemDetail(ItemUid itemUid, std::uint32_t durability,
                               std::uint32_t maxDurability, std::uint32_t loadedAmmoItemId,
                               std::uint32_t loadedAmmoCount)
{
    const auto it = _items.find(itemUid);
    if (it == _items.end())
        return false;
    it->second.durability = durability;
    it->second.maxDurability = maxDurability;
    it->second.loadedAmmoItemId = loadedAmmoItemId;
    it->second.loadedAmmoCount = loadedAmmoCount;
    return true;
}

bool ItemLedger::SetItemMods(ItemUid itemUid, std::vector<ItemAttachment> mods)
{
    const auto it = _items.find(itemUid);
    if (it == _items.end())
        return false;
    it->second.mods = std::move(mods);
    return true;
}

bool ItemLedger::RemoveItemMod(ItemUid itemUid, const std::string& slotKey,
                               std::uint32_t& outItemId)
{
    outItemId = 0;
    const auto it = _items.find(itemUid);
    if (it == _items.end())
        return false;
    auto& mods = it->second.mods;
    for (auto mod = mods.begin(); mod != mods.end(); ++mod) {
        if (mod->slotKey != slotKey)
            continue;
        outItemId = mod->itemId;
        mods.erase(mod);
        return true;
    }
    return false;
}

ItemLedger::ReloadResult ItemLedger::ReloadAmmo(std::uint32_t backpackContainer,
                                                ItemUid gunUid,
                                                std::uint32_t ammoItemId,
                                                std::uint32_t count,
                                                std::uint32_t loadedAfter,
                                                std::uint32_t loadedAmmoMaxStack)
{
    ReloadResult result;
    auto containerIt = _containers.find(backpackContainer);
    if (containerIt == _containers.end()) {
        result.error = TransferError::MissingContainer;
        return result;
    }
    const auto gunIt = _items.find(gunUid);
    if (gunIt == _items.end()) {
        result.error = TransferError::MissingItem;
        return result;
    }
    if (ammoItemId == 0 || count == 0) {
        result.error = TransferError::CountMismatch;
        return result;
    }

    // Contract 2.16.0: reloading a different ammo type replaces the magazine. The rounds that
    // are currently loaded are returned to the backpack first (the client's native reload
    // dumps them locally), then `count` rounds of the new type are charged. Only the *new*
    // magazine count is accepted here, so a switch never has to keep the old count.
    const bool replacesMagazine = gunIt->second.loadedAmmoCount > 0 &&
                                  gunIt->second.loadedAmmoItemId != 0 &&
                                  gunIt->second.loadedAmmoItemId != ammoItemId;
    if (replacesMagazine) {
        if (loadedAfter != count) {
            result.error = TransferError::CountMismatch;
            return result;
        }
    } else {
        // loadedAfter is the client's post-reload magazine count. If it equals the server
        // count, the native magazine was rebuilt empty (dropped/re-picked gun): acknowledge
        // the resync without charging ammo. A lower value is stale, a higher one must be paid.
        if (loadedAfter == gunIt->second.loadedAmmoCount) {
            result.loadedCount = loadedAfter;
            result.backpackRevision = containerIt->second.revision;
            return result;
        }
        if (loadedAfter < gunIt->second.loadedAmmoCount ||
            loadedAfter > gunIt->second.loadedAmmoCount + count) {
            result.error = TransferError::CountMismatch;
            return result;
        }
    }

    auto& container = containerIt->second;
    for (const ItemUid uid : container.slots) {
        if (uid == gunUid) {
            result.error = TransferError::MissingItem;
            return result;
        }
    }
    // Validate everything before the first mutation so a rejected reload changes nothing.
    std::uint64_t available = 0;
    for (const ItemUid uid : container.slots) {
        if (uid == kNoItem)
            continue;
        const auto itemIt = _items.find(uid);
        if (itemIt != _items.end() && itemIt->second.itemId == ammoItemId)
            available += itemIt->second.count;
    }
    if (available < count) {
        result.error = TransferError::CountMismatch;
        return result;
    }
    if (replacesMagazine) {
        const std::uint32_t stack = std::max<std::uint32_t>(1, loadedAmmoMaxStack);
        const std::uint32_t oldAmmoId = gunIt->second.loadedAmmoItemId;
        const std::uint32_t oldCount = gunIt->second.loadedAmmoCount;
        std::uint64_t room = 0;
        std::uint32_t freeSlots = 0;
        for (const ItemUid uid : container.slots) {
            if (uid == kNoItem) {
                ++freeSlots;
                continue;
            }
            const auto itemIt = _items.find(uid);
            if (itemIt != _items.end() && itemIt->second.itemId == oldAmmoId &&
                itemIt->second.count < stack)
                room += stack - itemIt->second.count;
        }
        room += static_cast<std::uint64_t>(freeSlots) * stack;
        if (room < oldCount) {
            // No room for the rounds that would come out of the magazine.
            result.error = TransferError::CountMismatch;
            return result;
        }
        const auto added = AddCount(backpackContainer, oldAmmoId, oldCount, stack);
        if (added != oldCount) {
            result.error = TransferError::CountMismatch;
            return result;
        }
    }

    std::uint32_t remaining = count;
    for (auto& slot : container.slots) {
        if (remaining == 0)
            break;
        if (slot == kNoItem)
            continue;
        auto itemIt = _items.find(slot);
        if (itemIt == _items.end() || itemIt->second.itemId != ammoItemId)
            continue;
        const std::uint32_t take = std::min(itemIt->second.count, remaining);
        itemIt->second.count -= take;
        remaining -= take;
        if (itemIt->second.count == 0) {
            _locations.erase(slot);
            _items.erase(itemIt);
            slot = kNoItem;
        }
    }

    ++container.revision;
    gunIt->second.loadedAmmoItemId = ammoItemId;
    gunIt->second.loadedAmmoCount = loadedAfter;
    result.backpackRevision = container.revision;
    result.loadedCount = loadedAfter;
    return result;
}

std::uint32_t ItemLedger::AddCount(std::uint32_t containerId, std::uint32_t itemId,
                                   std::uint32_t count, std::uint32_t maxStack)
{
    auto containerIt = _containers.find(containerId);
    if (containerIt == _containers.end() || itemId == 0 || count == 0)
        return 0;

    auto& container = containerIt->second;
    const std::uint32_t stack = std::max<std::uint32_t>(1, maxStack);
    std::uint32_t remaining = count;

    // Merge into stacks that are not full yet (what a native pickup does first).
    for (auto& slot : container.slots) {
        if (remaining == 0)
            break;
        if (slot == kNoItem)
            continue;
        const auto itemIt = _items.find(slot);
        if (itemIt == _items.end() || itemIt->second.itemId != itemId)
            continue;
        if (itemIt->second.count >= stack)
            continue;
        const std::uint32_t add = std::min(stack - itemIt->second.count, remaining);
        itemIt->second.count += add;
        remaining -= add;
    }

    // Then open new stacks in free slots.
    for (std::uint16_t index = 0; index < container.capacity && remaining > 0; ++index) {
        if (container.slots[index] != kNoItem)
            continue;
        const std::uint32_t chunk = std::min(stack, remaining);
        const ItemUid uid = CreateItem(itemId, chunk);
        container.slots[index] = uid;
        _locations[uid] = {containerId, index};
        remaining -= chunk;
    }

    const std::uint32_t added = count - remaining;
    if (added > 0)
        ++container.revision;
    return added;
}

std::uint32_t ItemLedger::Revision(std::uint32_t containerId) const
{
    const auto it = _containers.find(containerId);
    return it == _containers.end() ? 0 : it->second.revision;
}

bool ItemLedger::SetRevision(std::uint32_t containerId, std::uint32_t revision)
{
    auto it = _containers.find(containerId);
    if (it == _containers.end())
        return false;
    it->second.revision = revision;
    return true;
}

bool ItemLedger::DoPut(std::uint32_t containerId, ItemUid item, std::uint16_t preferSlot,
                       std::uint16_t& placedSlot)
{
    placedSlot = 0;

    auto containerIt = _containers.find(containerId);
    if (containerIt == _containers.end())
        return false;
    if (_items.find(item) == _items.end())
        return false;
    if (_locations.find(item) != _locations.end())
        return false; // already placed somewhere

    auto& container = containerIt->second;

    if (preferSlot < container.capacity && container.slots[preferSlot] == kNoItem) {
        container.slots[preferSlot] = item;
        _locations[item] = {containerId, preferSlot};
        placedSlot = preferSlot;
        return true;
    }

    for (std::uint16_t slot = 0; slot < container.capacity; ++slot) {
        if (container.slots[slot] != kNoItem)
            continue;
        container.slots[slot] = item;
        _locations[item] = {containerId, slot};
        placedSlot = slot;
        return true;
    }

    return false;
}

bool ItemLedger::DoTake(std::uint32_t containerId, std::uint16_t slot, ItemUid& out)
{
    out = kNoItem;

    auto containerIt = _containers.find(containerId);
    if (containerIt == _containers.end())
        return false;

    auto& container = containerIt->second;
    if (slot >= container.capacity || container.slots[slot] == kNoItem)
        return false;

    out = container.slots[slot];
    container.slots[slot] = kNoItem;
    _locations.erase(out);
    return true;
}

bool ItemLedger::TryPut(std::uint32_t containerId, ItemUid item, std::uint16_t preferSlot,
                        std::uint16_t& placedSlot)
{
    if (!DoPut(containerId, item, preferSlot, placedSlot))
        return false;
    ++_containers[containerId].revision;
    return true;
}

bool ItemLedger::TryTake(std::uint32_t containerId, std::uint16_t slot, ItemUid& out)
{
    if (!DoTake(containerId, slot, out))
        return false;
    ++_containers[containerId].revision;
    return true;
}

bool ItemLedger::Move(ItemUid item, std::uint32_t toContainer, std::uint16_t preferSlot)
{
    std::uint32_t fromContainer = 0;
    std::uint16_t fromSlot = 0;
    if (!Locate(item, fromContainer, fromSlot))
        return false;

    ItemUid removed = kNoItem;
    if (!TryTake(fromContainer, fromSlot, removed))
        return false;

    std::uint16_t placed = 0;
    if (TryPut(toContainer, item, preferSlot, placed))
        return true;

    // rollback (bumps the source revision again; the item is back where it was)
    std::uint16_t ignored = 0;
    TryPut(fromContainer, item, fromSlot, ignored);
    return false;
}

ItemLedger::TransferResult ItemLedger::Transfer(ItemUid item, std::uint32_t sourceContainer,
                                                std::uint16_t sourceSlot,
                                                std::uint32_t targetContainer,
                                                std::uint16_t targetSlot,
                                                std::uint32_t expectedItemId,
                                                std::uint32_t expectedRevision,
                                                std::uint32_t count)
{
    TransferResult result;

    auto sourceIt = _containers.find(sourceContainer);
    auto targetIt = _containers.find(targetContainer);
    if (sourceIt == _containers.end() || targetIt == _containers.end()) {
        result.error = TransferError::MissingContainer;
        return result;
    }

    const auto itemIt = _items.find(item);
    if (itemIt == _items.end()) {
        result.error = TransferError::MissingItem;
        return result;
    }

    const auto locationIt = _locations.find(item);
    if (locationIt == _locations.end() || locationIt->second.first != sourceContainer ||
        locationIt->second.second != sourceSlot) {
        result.error = TransferError::SourceMismatch;
        return result;
    }

    if (itemIt->second.itemId != expectedItemId) {
        result.error = TransferError::ExpectedItemMismatch;
        return result;
    }
    if (itemIt->second.count != count) {
        result.error = TransferError::CountMismatch;
        return result;
    }
    if (sourceIt->second.revision != expectedRevision) {
        result.error = TransferError::RevisionMismatch;
        return result;
    }
    if (targetSlot >= targetIt->second.capacity) {
        result.error = TransferError::TargetSlotOutOfRange;
        return result;
    }
    if (targetIt->second.slots[targetSlot] != kNoItem) {
        result.error = TransferError::TargetSlotBusy;
        return result;
    }

    // Mutations cannot fail after the checks above: take from the validated source slot and
    // put into the validated free target slot.
    sourceIt->second.slots[sourceSlot] = kNoItem;
    targetIt->second.slots[targetSlot] = item;
    _locations[item] = {targetContainer, targetSlot};
    result.movedCount = count;

    if (sourceContainer == targetContainer) {
        ++targetIt->second.revision;
        result.sourceRevision = targetIt->second.revision;
        result.targetRevision = targetIt->second.revision;
    } else {
        ++sourceIt->second.revision;
        ++targetIt->second.revision;
        result.sourceRevision = sourceIt->second.revision;
        result.targetRevision = targetIt->second.revision;
    }
    return result;
}

ItemLedger::TransferResult ItemLedger::TransferEx(ItemUid item, std::uint32_t sourceContainer,
                                                  std::uint16_t sourceSlot,
                                                  std::uint32_t targetContainer,
                                                  std::uint16_t targetSlot,
                                                  std::uint32_t expectedItemId,
                                                  std::uint32_t expectedRevision,
                                                  std::uint32_t count, std::uint32_t maxStack)
{
    TransferResult result;

    auto sourceIt = _containers.find(sourceContainer);
    auto targetIt = _containers.find(targetContainer);
    if (sourceIt == _containers.end() || targetIt == _containers.end()) {
        result.error = TransferError::MissingContainer;
        return result;
    }

    auto itemIt = _items.find(item);
    if (itemIt == _items.end()) {
        result.error = TransferError::MissingItem;
        return result;
    }

    const auto locationIt = _locations.find(item);
    if (locationIt == _locations.end() || locationIt->second.first != sourceContainer ||
        locationIt->second.second != sourceSlot) {
        result.error = TransferError::SourceMismatch;
        return result;
    }

    if (itemIt->second.itemId != expectedItemId) {
        result.error = TransferError::ExpectedItemMismatch;
        return result;
    }
    if (sourceIt->second.revision != expectedRevision) {
        result.error = TransferError::RevisionMismatch;
        return result;
    }
    if (targetSlot >= targetIt->second.capacity) {
        result.error = TransferError::TargetSlotOutOfRange;
        return result;
    }
    if (count == 0 || count > itemIt->second.count) {
        result.error = TransferError::CountMismatch;
        return result;
    }

    const ItemUid targetUid = targetIt->second.slots[targetSlot];
    if (targetUid == item && sourceContainer == targetContainer && sourceSlot == targetSlot) {
        // Empty move (same slot): no mutation and no revision bump.
        result.sourceRevision = sourceIt->second.revision;
        result.targetRevision = targetIt->second.revision;
        return result;
    }

    if (targetUid == kNoItem) {
        if (count == itemIt->second.count) {
            sourceIt->second.slots[sourceSlot] = kNoItem;
            targetIt->second.slots[targetSlot] = item;
            _locations[item] = {targetContainer, targetSlot};
            result.movedCount = count;
        } else {
            // Partial transfer: keep the source uid and create a new uid for the split stack.
            itemIt->second.count -= count;
            const ItemUid splitUid = CreateItem(itemIt->second.itemId, count);
            _items[splitUid].durability = itemIt->second.durability;
            _items[splitUid].maxDurability = itemIt->second.maxDurability;
            _items[splitUid].loadedAmmoItemId = itemIt->second.loadedAmmoItemId;
            _items[splitUid].loadedAmmoCount = itemIt->second.loadedAmmoCount;
            targetIt->second.slots[targetSlot] = splitUid;
            _locations[splitUid] = {targetContainer, targetSlot};
            result.movedCount = count;
            result.newItemUid = splitUid;
        }
    } else {
        const auto targetItemIt = _items.find(targetUid);
        if (targetItemIt == _items.end()) {
            result.error = TransferError::MissingItem;
            return result;
        }
        if (targetItemIt->second.itemId != itemIt->second.itemId || maxStack <= 1) {
            result.error = TransferError::TargetSlotBusy;
            return result;
        }
        if (targetItemIt->second.count >= maxStack) {
            result.error = TransferError::TargetSlotBusy;
            return result;
        }

        const std::uint32_t room = maxStack - targetItemIt->second.count;
        const std::uint32_t moved = std::min(count, room);
        targetItemIt->second.count += moved;
        itemIt->second.count -= moved;
        result.movedCount = moved;

        if (itemIt->second.count == 0) {
            // The source stack was fully merged into the target uid.
            sourceIt->second.slots[sourceSlot] = kNoItem;
            _locations.erase(item);
            _items.erase(itemIt);
        }
    }

    if (sourceContainer == targetContainer) {
        ++targetIt->second.revision;
        result.sourceRevision = targetIt->second.revision;
        result.targetRevision = targetIt->second.revision;
    } else {
        ++sourceIt->second.revision;
        ++targetIt->second.revision;
        result.sourceRevision = sourceIt->second.revision;
        result.targetRevision = targetIt->second.revision;
    }
    return result;
}

ItemLedger::ArrangeError ItemLedger::Arrange(
    std::uint32_t containerId, std::uint32_t expectedRevision,
    const std::vector<ArrangeEntry>& layout,
    const std::function<std::uint32_t(std::uint32_t)>& maxStackForItem)
{
    auto containerIt = _containers.find(containerId);
    if (containerIt == _containers.end())
        return ArrangeError::MissingContainer;
    auto& container = containerIt->second;
    if (container.revision != expectedRevision)
        return ArrangeError::RevisionMismatch;

    std::vector<bool> usedSlots(container.capacity, false);
    std::unordered_map<ItemUid, const ArrangeEntry*> byUid;
    byUid.reserve(layout.size());
    std::unordered_map<std::uint32_t, std::uint32_t> beforeCount;
    std::unordered_map<std::uint32_t, std::uint32_t> afterCount;

    for (const ItemUid uid : container.slots) {
        if (uid == kNoItem)
            continue;
        const auto itemIt = _items.find(uid);
        if (itemIt == _items.end())
            return ArrangeError::UnknownItem;
        beforeCount[itemIt->second.itemId] += itemIt->second.count;
    }

    for (const auto& entry : layout) {
        if (entry.slot >= container.capacity)
            return ArrangeError::SlotOutOfRange;
        if (usedSlots[entry.slot])
            return ArrangeError::DuplicateSlot;
        usedSlots[entry.slot] = true;

        if (entry.uid == kNoItem || !byUid.emplace(entry.uid, &entry).second)
            return ArrangeError::DuplicateItem;

        const auto locationIt = _locations.find(entry.uid);
        if (locationIt == _locations.end() || locationIt->second.first != containerId)
            return ArrangeError::UnknownItem;

        const auto itemIt = _items.find(entry.uid);
        if (itemIt == _items.end())
            return ArrangeError::UnknownItem;
        if (itemIt->second.itemId != entry.itemId)
            return ArrangeError::ItemIdMismatch;
        if (entry.count == 0)
            return ArrangeError::InvalidCount;

        std::uint32_t maxStack = maxStackForItem ? maxStackForItem(entry.itemId) : 1;
        if (maxStack == 0)
            maxStack = 1;
        if (entry.count > maxStack)
            return ArrangeError::StackOverflow;

        afterCount[entry.itemId] += entry.count;
    }

    // Every uid currently in the container must be accounted for: either present in the
    // layout (possibly with a different count) or fully merged into another uid.
    for (const ItemUid uid : container.slots) {
        if (uid == kNoItem)
            continue;
        if (byUid.find(uid) != byUid.end())
            continue;
        const auto itemIt = _items.find(uid);
        if (itemIt == _items.end())
            return ArrangeError::UnknownItem;
        if (afterCount[itemIt->second.itemId] == 0)
            return ArrangeError::CountMismatch;
    }

    for (const auto& [itemId, before] : beforeCount) {
        const auto afterIt = afterCount.find(itemId);
        if (afterIt == afterCount.end() || afterIt->second != before)
            return ArrangeError::CountMismatch;
    }

    // All checks passed: rebuild slots, update counts, destroy merged-away uid instances.
    for (const ItemUid uid : container.slots) {
        if (uid == kNoItem || byUid.find(uid) != byUid.end())
            continue;
        _locations.erase(uid);
        _items.erase(uid);
    }

    std::vector<ItemUid> slots(container.capacity, kNoItem);
    for (const auto& entry : layout) {
        slots[entry.slot] = entry.uid;
        _locations[entry.uid] = {containerId, entry.slot};
        _items[entry.uid].count = entry.count;
    }
    container.slots = std::move(slots);
    ++container.revision;
    return ArrangeError::Ok;
}

ItemLedger::ConsumeResult ItemLedger::Consume(ItemUid item, std::uint32_t containerId,
                                              std::uint16_t slot,
                                              std::uint32_t expectedItemId,
                                              std::uint32_t expectedRevision,
                                              std::uint32_t count)
{
    ConsumeResult result;

    auto containerIt = _containers.find(containerId);
    if (containerIt == _containers.end()) {
        result.error = TransferError::MissingContainer;
        return result;
    }
    auto itemIt = _items.find(item);
    if (itemIt == _items.end()) {
        result.error = TransferError::MissingItem;
        return result;
    }
    const auto locationIt = _locations.find(item);
    if (locationIt == _locations.end() || locationIt->second.first != containerId ||
        locationIt->second.second != slot) {
        result.error = TransferError::SourceMismatch;
        return result;
    }
    if (itemIt->second.itemId != expectedItemId) {
        result.error = TransferError::ExpectedItemMismatch;
        return result;
    }
    if (containerIt->second.revision != expectedRevision) {
        result.error = TransferError::RevisionMismatch;
        return result;
    }
    if (count == 0 || count > itemIt->second.count) {
        result.error = TransferError::CountMismatch;
        return result;
    }

    itemIt->second.count -= count;
    result.remaining = itemIt->second.count;
    if (itemIt->second.count == 0) {
        containerIt->second.slots[slot] = kNoItem;
        _locations.erase(item);
        _items.erase(item);
    }
    ++containerIt->second.revision;
    result.sourceRevision = containerIt->second.revision;
    return result;
}

bool ItemLedger::Locate(ItemUid item, std::uint32_t& containerId, std::uint16_t& slot) const
{
    const auto it = _locations.find(item);
    if (it == _locations.end())
        return false;

    containerId = it->second.first;
    slot = it->second.second;
    return true;
}

const ItemInstance* ItemLedger::GetItem(ItemUid item) const
{
    const auto it = _items.find(item);
    return it == _items.end() ? nullptr : &it->second;
}

bool ItemLedger::Contains(ItemUid item) const
{
    return _items.find(item) != _items.end();
}

bool ItemLedger::DestroyItem(ItemUid item)
{
    if (_items.find(item) == _items.end())
        return false;
    if (_locations.find(item) != _locations.end())
        return false; // still placed in a container
    _items.erase(item);
    return true;
}

bool ItemLedger::AnyItem(std::uint32_t containerId) const
{
    const auto it = _containers.find(containerId);
    if (it == _containers.end())
        return false;
    for (const ItemUid item : it->second.slots) {
        if (item != kNoItem)
            return true;
    }
    return false;
}

void ItemLedger::ClearContainer(std::uint32_t containerId)
{
    const auto it = _containers.find(containerId);
    if (it == _containers.end())
        return;

    for (const ItemUid item : it->second.slots) {
        _locations.erase(item);
        _items.erase(item);
    }

    it->second.slots.assign(it->second.capacity, kNoItem);
    ++it->second.revision;
}

const std::vector<ItemUid>* ItemLedger::Slots(std::uint32_t containerId) const
{
    const auto it = _containers.find(containerId);
    return it == _containers.end() ? nullptr : &it->second.slots;
}

std::uint16_t ItemLedger::Capacity(std::uint32_t containerId) const
{
    const auto it = _containers.find(containerId);
    return it == _containers.end() ? 0 : it->second.capacity;
}

} // namespace coop
