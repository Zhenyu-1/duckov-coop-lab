#pragma once

#include <cstddef>
#include <vector>

#include "storage/ISettlementQueue.h"

namespace coop {

/// FIFO skeleton: records stay in memory until a backend drains them. Durable journaling and
/// the MySQL projection arrive with P-STORE/M2.2.
class InMemorySettlementQueue final : public ISettlementQueue {
public:
    bool Enqueue(const SettlementRecord& record) override;
    std::size_t Drain(std::vector<SettlementRecord>& out, std::size_t max = 64) override;
    std::size_t Pending() const override { return _pending.size(); }

private:
    std::vector<SettlementRecord> _pending;
};

} // namespace coop
