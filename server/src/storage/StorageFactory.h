#pragma once

// Storage backend selection (ADR-016/017): `--storage=file` is the production path today,
// `memory` exists for offline/CI runs. Redis/MySQL are interface-only until M2.2.

#include <memory>
#include <string>

#include "storage/IStorage.h"

namespace coop {

enum class StorageKind {
    File,
    Memory,
};

/// Parses a `--storage` value; fills `error` for unknown/deferred backends.
bool ParseStorageKind(const std::string& value, StorageKind& out, std::string& error);

/// Creates the durable stash backend. Returns nullptr and fills `error` on failure.
std::unique_ptr<IStorage> CreateStorage(StorageKind kind, const std::string& dataDir,
                                       std::string& error);

const char* StorageKindName(StorageKind kind);

} // namespace coop
