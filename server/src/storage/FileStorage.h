#pragma once

#include <cstdint>
#include <string>

#include "storage/IStorage.h"

namespace coop {

/// Minimal file storage: one JSON file per player under <dir>/players/.
/// Writes are atomic (temp file + rename).
class FileStorage final : public IStorage {
public:
    explicit FileStorage(std::string directory);

    bool LoadStash(std::uint64_t identity, std::vector<StoredItem>& out) override;
    bool SaveStash(std::uint64_t identity, const std::vector<StoredItem>& items) override;
    bool SettleRaid(std::uint64_t identity, std::uint32_t raidEpoch,
                    const std::vector<StoredItem>& items, bool extracted) override;

    const std::string& Directory() const { return _directory; }

private:
    std::string PlayerPath(std::uint64_t identity) const;
    std::string TransactionPath(std::uint64_t identity) const;

    std::string _directory;
    std::uint64_t _runId = 0; // per-process id: settlement idempotency is scoped to a run
};

} // namespace coop
