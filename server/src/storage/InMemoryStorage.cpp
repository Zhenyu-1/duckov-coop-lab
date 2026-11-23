#include "storage/InMemoryStorage.h"

namespace coop {

bool InMemoryStorage::LoadStash(std::uint64_t identity, std::vector<StoredItem>& out)
{
    out.clear();
    const auto it = _stashes.find(identity);
    if (it != _stashes.end())
        out = it->second;
    return true;
}

bool InMemoryStorage::SaveStash(std::uint64_t identity, const std::vector<StoredItem>& items)
{
    _stashes[identity] = items;
    return true;
}

bool InMemoryStorage::SettleRaid(std::uint64_t identity, std::uint32_t raidEpoch,
                                 const std::vector<StoredItem>& items, bool extracted)
{
    (void)extracted; // the world already passes an empty list for a lost raid

    const auto settled = _settledEpoch.find(identity);
    if (settled != _settledEpoch.end() && settled->second == raidEpoch)
        return true; // idempotent replay of the same settlement

    _settledEpoch[identity] = raidEpoch;
    return SaveStash(identity, items);
}

} // namespace coop
