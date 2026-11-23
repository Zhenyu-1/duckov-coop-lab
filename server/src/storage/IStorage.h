#pragma once

#include <cstdint>
#include <vector>

namespace coop {

struct StoredItem {
    std::uint32_t itemId = 0;
    std::uint32_t count = 0;
    // P1 phase 3f: detail survives settlement/rejoin. Zero means "not present" for old data.
    std::uint32_t durability = 0;
    std::uint32_t maxDurability = 0;
    std::uint32_t loadedAmmoItemId = 0;
    std::uint32_t loadedAmmoCount = 0;
};

/// Persistence boundary. World only sees transactions, never file/db details.
class IStorage {
public:
    virtual ~IStorage() = default;

    /// Called when a player enters the session: returns their stash.
    /// `identity` is a stable account key (hash), not the ephemeral session player id.
    virtual bool LoadStash(std::uint64_t identity, std::vector<StoredItem>& out) = 0;

    /// Called on raid end: extracted -> current items, dead -> empty list.
    virtual bool SaveStash(std::uint64_t identity, const std::vector<StoredItem>& items) = 0;

    /// B3/C9: per-raid settlement transaction. A second call for the same
    /// (server run, raidEpoch) must be a no-op (idempotent replay).
    virtual bool SettleRaid(std::uint64_t identity, std::uint32_t raidEpoch,
                            const std::vector<StoredItem>& items, bool extracted)
    {
        (void)raidEpoch;
        (void)extracted;
        return SaveStash(identity, items);
    }
};

} // namespace coop

