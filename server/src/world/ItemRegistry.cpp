#include "world/ItemRegistry.h"

#include <algorithm>

#include "core/Log.h"

namespace coop {

namespace {

std::uint32_t ToU32(const Json& value, std::uint32_t fallback = 0)
{
    return static_cast<std::uint32_t>(value.AsNumber(fallback));
}

/// T1 display metadata: the JSON fallback carries the raw dump names (`quality`, `value`,
/// `displayQuality`). Values outside the XML loader's range are reported and fall back to 0,
/// so the fallback table never produces a definition the XML loader would have rejected
/// (and a bogus double never reaches the int cast).
int ToDisplayInt(const Json& entry, const char* key, std::uint32_t itemId, int maxValue)
{
    const Json* value = entry.Find(key);
    if (value == nullptr || !value->IsNumber())
        return 0;
    const double number = value->AsNumber(0.0);
    if (number < 0.0 || number > static_cast<double>(maxValue)) {
        Log::Warn("items.json: item " + std::to_string(itemId) + " has out-of-range " + key +
                  " (" + std::to_string(number) + "), using 0");
        return 0;
    }
    return static_cast<int>(number);
}

} // namespace

bool ItemRegistry::LoadFromFile(const std::string& path, std::string& error)
{
    Json root;
    if (!LoadJsonFile(path, root, error))
        return false;
    return LoadFromJson(root, error);
}

bool ItemRegistry::Add(const ItemDef& def)
{
    return _items.emplace(def.id, def).second;
}

bool ItemRegistry::LoadFromJson(const Json& root, std::string& error)
{
    const Json* items = root.Find("items");
    if (items == nullptr || !items->IsArray()) {
        error = "items.json: missing 'items' array";
        return false;
    }

    _items.clear();
    for (const auto& entry : items->items) {
        const Json* id = entry.Find("id");
        if (id == nullptr || !id->IsNumber()) {
            error = "items.json: entry without numeric id";
            return false;
        }

        ItemDef def;
        def.id = ToU32(*id);
        if (const Json* name = entry.Find("name"))
            def.name = name->AsString();
        if (const Json* size = entry.Find("size"))
            def.size = static_cast<std::uint16_t>(ToU32(*size, 1));
        if (const Json* stack = entry.Find("stack"))
            def.stack = ToU32(*stack, 1);
        if (const Json* weight = entry.Find("weight"))
            def.weight = weight->AsNumber();
        def.quality = ToDisplayInt(entry, "quality", def.id, 65535);
        def.value = ToDisplayInt(entry, "value", def.id, 1000000000);
        def.displayQuality = ToDisplayInt(entry, "displayQuality", def.id, 7);

        // ADR-017: keep the exported value; counts are u32 since wire v2.
        if (def.stack > 65535u) {
            Log::Warn("items.json: item " + std::to_string(def.id) + " stack " +
                      std::to_string(def.stack) +
                      " exceeds u16 (65535); kept as u32 (ADR-017, wire v2)");
        }

        _items[def.id] = def;
    }

    return true;
}

const ItemDef* ItemRegistry::Find(std::uint32_t itemId) const
{
    const auto it = _items.find(itemId);
    return it == _items.end() ? nullptr : &it->second;
}

bool LoadLootTable(const std::string& path, const std::string& tableId, LootTable& out, std::string& error)
{
    Json root;
    if (!LoadJsonFile(path, root, error))
        return false;
    return ParseLootTable(root, tableId, out, error);
}

bool LoadAllLootTables(const std::string& path,
                       std::unordered_map<std::string, LootTable>& out, std::string& error)
{
    out.clear();
    Json root;
    if (!LoadJsonFile(path, root, error))
        return false;
    const Json* tables = root.Find("tables");
    if (tables == nullptr || !tables->IsArray()) {
        error = "loot_tables.json: missing 'tables' array";
        return false;
    }
    for (const auto& table : tables->items) {
        const Json* id = table.Find("id");
        if (id == nullptr || !id->IsString())
            continue;
        LootTable parsed;
        if (!ParseLootTable(root, id->AsString(), parsed, error))
            return false;
        out[parsed.id] = std::move(parsed);
    }
    return !out.empty();
}

bool LoadAiLootTables(const std::string& path,
                      std::unordered_map<std::uint32_t, std::string>& out, std::string& error)
{
    out.clear();
    Json root;
    if (!LoadJsonFile(path, root, error))
        return false;
    const Json* tables = root.Find("tables");
    if (tables == nullptr || !tables->IsArray()) {
        error = "ai_loot_tables.json: missing 'tables' array";
        return false;
    }
    // Key 0 is the optional fallback table used for monsters without an explicit mapping.
    if (const Json* fallback = root.Find("default")) {
        if (fallback->IsString() && !fallback->AsString().empty())
            out[0] = fallback->AsString();
    }
    for (const auto& entry : tables->items) {
        const Json* monsterId = entry.Find("monsterId");
        const Json* lootTable = entry.Find("lootTable");
        if (monsterId == nullptr || lootTable == nullptr || !lootTable->IsString())
            continue;
        const std::uint32_t id = ToU32(*monsterId);
        if (id != 0 && !lootTable->AsString().empty())
            out[id] = lootTable->AsString();
    }
    return true;
}

bool ParseLootTable(const Json& root, const std::string& tableId, LootTable& out, std::string& error)
{    const Json* tables = root.Find("tables");
    if (tables == nullptr || !tables->IsArray()) {
        error = "loot_tables.json: missing 'tables' array";
        return false;
    }

    for (const auto& table : tables->items) {
        const Json* id = table.Find("id");
        if (id == nullptr || id->AsString() != tableId)
            continue;

        out = LootTable{};
        out.id = tableId;
        if (const Json* capacity = table.Find("capacity"))
            out.capacity = static_cast<std::uint16_t>(ToU32(*capacity, 8));
        if (const Json* slots = table.Find("slots"))
            out.capacity = static_cast<std::uint16_t>(ToU32(*slots, out.capacity));
        if (const Json* rolls = table.Find("rolls")) {
            if (rolls->IsObject()) {
                const Json* min = rolls->Find("min");
                const Json* max = rolls->Find("max");
                if (min != nullptr || max != nullptr) {
                    out.rollMin = static_cast<int>(ToU32(min != nullptr ? *min : *max, 1));
                    out.rollMax = static_cast<int>(ToU32(max != nullptr ? *max : *min, out.rollMin));
                    if (out.rollMax < out.rollMin)
                        out.rollMax = out.rollMin;
                }
            } else if (rolls->IsNumber()) {
                out.rolls = static_cast<int>(ToU32(*rolls, 3));
            }
        }
        if (out.capacity == 0)
            out.capacity = 1;

        const Json* entries = table.Find("entries");
        if (entries == nullptr || !entries->IsArray()) {
            error = "loot table '" + tableId + "': missing entries";
            return false;
        }

        for (const auto& entry : entries->items) {
            LootEntry lootEntry;
            if (const Json* itemId = entry.Find("itemId"))
                lootEntry.itemId = ToU32(*itemId);
            if (const Json* weight = entry.Find("weight"))
                lootEntry.weight = static_cast<int>(ToU32(*weight, 1));
            if (const Json* min = entry.Find("min"))
                lootEntry.minCount = ToU32(*min, 1);
            if (const Json* max = entry.Find("max"))
                lootEntry.maxCount = ToU32(*max, 1);
            // v2: { "count": { "min": .., "max": .. } } or { "count": n }.
            if (const Json* count = entry.Find("count")) {
                if (count->IsObject()) {
                    const Json* min = count->Find("min");
                    const Json* max = count->Find("max");
                    if (min != nullptr)
                        lootEntry.minCount = ToU32(*min, 1);
                    if (max != nullptr)
                        lootEntry.maxCount = ToU32(*max, lootEntry.minCount);
                } else if (count->IsNumber()) {
                    lootEntry.minCount = lootEntry.maxCount = ToU32(*count, 1);
                }
            }
            if (lootEntry.minCount == 0)
                lootEntry.minCount = 1;
            if (lootEntry.maxCount < lootEntry.minCount)
                lootEntry.maxCount = lootEntry.minCount;
            out.entries.push_back(lootEntry);
        }

        return true;
    }

    error = "loot table not found: " + tableId;
    return false;
}

std::vector<RolledItem> RollLoot(const LootTable& table, std::mt19937& rng,
                                 const ItemRegistry* registry, int rollCount)
{
    std::vector<RolledItem> result;
    if (table.entries.empty())
        return result;

    int count = rollCount;
    if (count < 0) {
        const int low = std::max(0, table.MinRolls());
        const int high = std::max(low, table.MaxRolls());
        if (low == high) {
            count = low;
        } else {
            std::uniform_int_distribution<int> rollDist(low, high);
            count = rollDist(rng);
        }
    }
    if (count <= 0)
        return result;

    int totalWeight = 0;
    for (const auto& entry : table.entries)
        totalWeight += std::max(0, entry.weight);

    if (totalWeight <= 0)
        return result;

    std::uniform_int_distribution<int> weightDist(1, totalWeight);

    for (int i = 0; i < count; ++i) {
        int pick = weightDist(rng);
        const LootEntry* chosen = nullptr;
        for (const auto& entry : table.entries) {
            pick -= std::max(0, entry.weight);
            if (pick <= 0) {
                chosen = &entry;
                break;
            }
        }

        if (chosen == nullptr)
            continue;
        if (registry != nullptr && registry->Find(chosen->itemId) == nullptr)
            continue;

        const std::uint32_t low = std::max<std::uint32_t>(1, chosen->minCount);
        const std::uint32_t high = std::max(low, chosen->maxCount);
        std::uniform_int_distribution<std::uint32_t> countDist(low, high);

        result.push_back(RolledItem{chosen->itemId, static_cast<std::uint32_t>(countDist(rng))});
    }

    return result;
}

} // namespace coop


