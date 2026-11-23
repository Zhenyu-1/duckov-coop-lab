#include "storage/StorageFactory.h"

#include "core/Log.h"
#include "storage/FileStorage.h"
#include "storage/InMemoryStorage.h"

namespace coop {

bool ParseStorageKind(const std::string& value, StorageKind& out, std::string& error)
{
    if (value == "file") {
        out = StorageKind::File;
        return true;
    }
    if (value == "memory") {
        out = StorageKind::Memory;
        return true;
    }
    if (value == "redis" || value == "mysql" || value == "redis+mysql") {
        error = "storage backend '" + value +
                "' is deferred to M2.2 (P-STORE); use --storage=file";
        return false;
    }

    error = "unknown storage backend '" + value + "' (expected file|memory)";
    return false;
}

std::unique_ptr<IStorage> CreateStorage(StorageKind kind, const std::string& dataDir,
                                       std::string& error)
{
    switch (kind) {
    case StorageKind::File:
        return std::make_unique<FileStorage>(dataDir);
    case StorageKind::Memory:
        Log::Warn("storage: memory backend selected; state is lost on exit");
        return std::make_unique<InMemoryStorage>();
    }

    error = "unsupported storage backend";
    return nullptr;
}

const char* StorageKindName(StorageKind kind)
{
    switch (kind) {
    case StorageKind::File:
        return "file";
    case StorageKind::Memory:
        return "memory";
    }
    return "unknown";
}

} // namespace coop
