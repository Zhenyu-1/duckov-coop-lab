#pragma once

// Durable settlement boundary (ADR-016): the world enqueues a settlement and the backend
// persists it asynchronously (journal -> MySQL in M2.2). The queue is intentionally
// append-only and idempotency is the backend's job via (runId, identity, raidEpoch).

#include <cstddef>
#include <cstdint>
#include <vector>

#include "storage/IStorage.h"

namespace coop {

struct SettlementRecord {
    std::uint64_t identity = 0;
    std::uint32_t raidEpoch = 0;
    bool extracted = false;
    std::vector<StoredItem> items;
};

class ISettlementQueue {
public:
    virtual ~ISettlementQueue() = default;

    virtual bool Enqueue(const SettlementRecord& record) = 0;
    /// Move at most `max` buffered records into `out` (FIFO); returns the drained count.
    virtual std::size_t Drain(std::vector<SettlementRecord>& out, std::size_t max = 64) = 0;
    virtual std::size_t Pending() const = 0;
};

} // namespace coop
