#pragma once

#include <cstdint>
#include <map>
#include <vector>

#include "storage/IStorage.h"

namespace coop {

/// Process-local IStorage for offline/CI runs (`--storage=memory`). Settlement idempotency
/// mirrors FileStorage but nothing is written to disk.
class InMemoryStorage final : public IStorage {
public:
    bool LoadStash(std::uint64_t identity, std::vector<StoredItem>& out) override;
    bool SaveStash(std::uint64_t identity, const std::vector<StoredItem>& items) override;
    bool SettleRaid(std::uint64_t identity, std::uint32_t raidEpoch,
                    const std::vector<StoredItem>& items, bool extracted) override;

private:
    std::map<std::uint64_t, std::vector<StoredItem>> _stashes;
    std::map<std::uint64_t, std::uint32_t> _settledEpoch;
};

} // namespace coop
