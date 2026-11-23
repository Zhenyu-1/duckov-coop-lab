#pragma once

#include <cstdint>
#include <string>
#include <vector>

#include "storage/IRaidStore.h"

namespace coop {

/// Process-local raid store used until Redis is wired (M2.2). Intentionally single-threaded:
/// the server tick is the only writer.
class InMemoryRaidStore final : public IRaidStore {
public:
    bool Put(const RaidCheckpoint& checkpoint, std::uint32_t ttlSeconds) override;
    bool Get(std::uint32_t runId, std::uint64_t identity, RaidCheckpoint& out) override;
    bool EraseRaid(std::uint32_t runId) override;
    std::size_t Count() const override { return _entries.size(); }

private:
    struct Entry {
        RaidCheckpoint checkpoint;
        std::uint32_t ttlSeconds = 0;
    };

    std::vector<Entry> _entries; // linear scan is fine for players * checkpoints
};

} // namespace coop
