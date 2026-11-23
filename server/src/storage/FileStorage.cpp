#include "storage/FileStorage.h"

#include <algorithm>
#include <chrono>
#include <filesystem>
#include <fstream>
#include <sstream>

#include "core/Json.h"
#include "core/Log.h"

namespace coop {

namespace fs = std::filesystem;

FileStorage::FileStorage(std::string directory)
    : _directory(std::move(directory))
{
    std::error_code ec;
    fs::create_directories(fs::path(_directory) / "players", ec);
    fs::create_directories(fs::path(_directory) / "transactions", ec);

    // settlement idempotency is scoped to this server run: after a restart a new
    // raid may reuse the same epoch counter, so the run id is part of the key
    _runId = static_cast<std::uint64_t>(
        std::chrono::system_clock::now().time_since_epoch().count());
}

std::string FileStorage::PlayerPath(std::uint64_t identity) const
{
    return (fs::path(_directory) / "players" / ("player_" + std::to_string(identity) + ".json")).string();
}

std::string FileStorage::TransactionPath(std::uint64_t identity) const
{
    return (fs::path(_directory) / "transactions" / ("player_" + std::to_string(identity) + ".json")).string();
}

bool FileStorage::LoadStash(std::uint64_t identity, std::vector<StoredItem>& out)
{
    out.clear();

    const std::string path = PlayerPath(identity);
    if (!fs::exists(path))
        return true; // new player, empty stash

    Json root;
    std::string error;
    if (!LoadJsonFile(path, root, error)) {
        Log::Warn("storage: cannot read " + path + ": " + error);
        return false;
    }

    const Json* items = root.Find("items");
    if (items == nullptr || !items->IsArray())
        return true;

    for (const auto& entry : items->items) {
        const Json* id = entry.Find("itemId");
        const Json* count = entry.Find("count");
        if (id == nullptr || !id->IsNumber())
            continue;

        StoredItem item;
        item.itemId = static_cast<std::uint32_t>(id->AsNumber());
        item.count = static_cast<std::uint32_t>(std::max(0.0, count != nullptr ? count->AsNumber(1) : 1));
        const auto optionalValue = [&](const char* key) -> std::uint32_t {
            const Json* value = entry.Find(key);
            if (value == nullptr || !value->IsNumber())
                return 0;
            return static_cast<std::uint32_t>(std::max(0.0, value->AsNumber()));
        };
        item.durability = optionalValue("durability");
        item.maxDurability = optionalValue("maxDurability");
        item.loadedAmmoItemId = optionalValue("loadedAmmoItemId");
        item.loadedAmmoCount = optionalValue("loadedAmmoCount");
        out.push_back(item);
    }

    return true;
}

bool FileStorage::SaveStash(std::uint64_t identity, const std::vector<StoredItem>& items)
{
    const std::string path = PlayerPath(identity);
    const std::string temp = path + ".tmp";

    std::ostringstream buffer;
    buffer << "{\n  \"items\": [\n";
    for (std::size_t i = 0; i < items.size(); ++i) {
        buffer << "    { \"itemId\": " << items[i].itemId << ", \"count\": " << items[i].count
               << ", \"durability\": " << items[i].durability
               << ", \"maxDurability\": " << items[i].maxDurability
               << ", \"loadedAmmoItemId\": " << items[i].loadedAmmoItemId
               << ", \"loadedAmmoCount\": " << items[i].loadedAmmoCount << " }";
        if (i + 1 < items.size())
            buffer << ",";
        buffer << "\n";
    }
    buffer << "  ]\n}\n";

    {
        std::ofstream file(temp, std::ios::binary | std::ios::trunc);
        if (!file) {
            Log::Warn("storage: cannot write " + temp);
            return false;
        }
        file << buffer.str();
        file.flush();
    }

    std::error_code ec;
    fs::remove(path, ec);
    fs::rename(temp, path, ec);
    if (ec) {
        Log::Warn("storage: rename failed for " + path + ": " + ec.message());
        return false;
    }

    return true;
}

bool FileStorage::SettleRaid(std::uint64_t identity, std::uint32_t raidEpoch,
                             const std::vector<StoredItem>& items, bool extracted)
{
    const std::string path = TransactionPath(identity);

    Json existing;
    std::string error;
    if (fs::exists(path) && LoadJsonFile(path, existing, error)) {
        const Json* runId = existing.Find("runId");
        const Json* epoch = existing.Find("raidEpoch");
        // runId is serialized as a string: a 64-bit clock value loses precision as a double
        if (runId != nullptr && runId->IsString() && runId->AsString() == std::to_string(_runId) &&
            epoch != nullptr &&
            static_cast<std::uint32_t>(epoch->AsNumber()) == raidEpoch) {
            return true; // idempotent replay of the same settlement
        }
    }

    if (!SaveStash(identity, items))
        return false;

    const std::string temp = path + ".tmp";
    std::ostringstream buffer;
    buffer << "{\n  \"runId\": \"" << _runId << "\",\n  \"raidEpoch\": " << raidEpoch
           << ",\n  \"outcome\": \"" << (extracted ? "extracted" : "dead") << "\",\n"
           << "  \"items\": " << items.size() << "\n}\n";

    {
        std::ofstream file(temp, std::ios::binary | std::ios::trunc);
        if (!file) {
            Log::Warn("storage: cannot write " + temp);
            return false;
        }
        file << buffer.str();
        file.flush();
    }

    std::error_code ec;
    fs::remove(path, ec);
    fs::rename(temp, path, ec);
    if (ec) {
        Log::Warn("storage: transaction rename failed for " + path + ": " + ec.message());
        return false;
    }

    return true;
}

} // namespace coop



