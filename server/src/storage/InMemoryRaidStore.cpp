#include "storage/InMemoryRaidStore.h"

#include <algorithm>

namespace coop {

bool InMemoryRaidStore::Put(const RaidCheckpoint& checkpoint, std::uint32_t ttlSeconds)
{
    for (auto& entry : _entries) {
        if (entry.checkpoint.runId == checkpoint.runId &&
            entry.checkpoint.identity == checkpoint.identity) {
            entry.checkpoint = checkpoint;
            entry.ttlSeconds = ttlSeconds;
            return true;
        }
    }
    _entries.push_back(Entry{checkpoint, ttlSeconds});
    return true;
}

bool InMemoryRaidStore::Get(std::uint32_t runId, std::uint64_t identity, RaidCheckpoint& out)
{
    for (const auto& entry : _entries) {
        if (entry.checkpoint.runId == runId && entry.checkpoint.identity == identity) {
            out = entry.checkpoint;
            return true;
        }
    }
    return false;
}

bool InMemoryRaidStore::EraseRaid(std::uint32_t runId)
{
    std::erase_if(_entries, [runId](const Entry& entry) {
        return entry.checkpoint.runId == runId;
    });
    return true;
}

} // namespace coop
