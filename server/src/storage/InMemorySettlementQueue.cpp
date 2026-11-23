#include "storage/InMemorySettlementQueue.h"

#include <algorithm>

namespace coop {

bool InMemorySettlementQueue::Enqueue(const SettlementRecord& record)
{
    _pending.push_back(record);
    return true;
}

std::size_t InMemorySettlementQueue::Drain(std::vector<SettlementRecord>& out, std::size_t max)
{
    const std::size_t count = std::min(max, _pending.size());
    out.reserve(out.size() + count);
    for (std::size_t i = 0; i < count; ++i)
        out.push_back(std::move(_pending[i]));
    _pending.erase(_pending.begin(), _pending.begin() + static_cast<std::ptrdiff_t>(count));
    return count;
}

} // namespace coop
