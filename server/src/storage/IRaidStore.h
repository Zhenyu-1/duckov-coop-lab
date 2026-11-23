#pragma once

// Hot raid state boundary (ADR-016): per-player checkpoints, container rolls and anything
// that only needs to live for the raid/session. Redis lands in M2.2; until then only the
// in-memory skeleton is wired. Records are opaque to the store: the caller owns the payload
// format (JSON/protobuf) and the store only keys it by (runId, identity).

#include <cstdint>
#include <cstddef>
#include <string>
#include <vector>

namespace coop {

struct RaidCheckpoint {
    std::uint32_t runId = 0;      // server process / raid identity
    std::uint32_t raidEpoch = 0;  // epoch within the run
    std::uint64_t identity = 0;   // stable account key (not the ephemeral player id)
    std::uint64_t version = 0;    // monotonically increasing snapshot version
    std::vector<std::uint8_t> payload;
};

class IRaidStore {
public:
    virtual ~IRaidStore() = default;

    /// Upsert one checkpoint. `ttlSeconds` is advisory; backends may ignore it.
    virtual bool Put(const RaidCheckpoint& checkpoint, std::uint32_t ttlSeconds) = 0;
    virtual bool Get(std::uint32_t runId, std::uint64_t identity, RaidCheckpoint& out) = 0;
    /// Drop every checkpoint of one run (raid end / server start).
    virtual bool EraseRaid(std::uint32_t runId) = 0;
    virtual std::size_t Count() const = 0;
};

} // namespace coop
