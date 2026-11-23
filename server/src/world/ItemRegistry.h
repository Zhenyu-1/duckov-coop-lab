#pragma once

#include <cstdint>
#include <random>
#include <string>
#include <unordered_map>
#include <vector>

#include "core/Json.h"

namespace coop {

struct ItemDef {
    std::uint32_t id = 0;
    std::string name;
    std::uint16_t size = 1;
    std::uint32_t stack = 1;
    double weight = 0.0;

    // Display/loot metadata (T1). `quality` is the native Item.Quality (inspecting-time
    // index); `value` is the native Item.Value (price); `displayQuality` is the native
    // DisplayQuality enum as an int: 0 None / 1 White / 2 Green / 3 Blue / 4 Purple /
    // 5 Orange / 6 LightRed / 7 Red. All three default to 0 when the source omits them.
    int quality = 0;
    int value = 0;
    int displayQuality = 0;
};

/// Item registry (B6): itemId -> static definition. Loaded from data/config/items.xml
/// (ADR-014) with items.json kept as a fallback during the dual-read period.
class ItemRegistry {
public:
    bool LoadFromFile(const std::string& path, std::string& error);
    bool LoadFromJson(const Json& root, std::string& error);

    /// Adds one definition; false when the id is already registered.
    bool Add(const ItemDef& def);

    const ItemDef* Find(std::uint32_t itemId) const;
    std::size_t Count() const { return _items.size(); }

private:
    std::unordered_map<std::uint32_t, ItemDef> _items;
};

struct LootEntry {
    std::uint32_t itemId = 0;
    int weight = 1;
    std::uint32_t minCount = 1;
    std::uint32_t maxCount = 1;
};

struct LootTable {
    std::string id;
    /// Container slots this table is authored for (v2: single source for capacity).
    std::uint16_t capacity = 8;
    /// Legacy fixed roll count; v2 tables use rollMin/rollMax instead.
    int rolls = 3;
    /// Per-open item count range (v2). -1 means "not configured, use rolls".
    int rollMin = -1;
    int rollMax = -1;
    std::vector<LootEntry> entries;

    int MinRolls() const { return rollMin >= 0 ? rollMin : rolls; }
    int MaxRolls() const { return rollMax >= 0 ? rollMax : rolls; }
};

/// Loads one table by id from data/config/loot_tables.json.
bool LoadLootTable(const std::string& path, const std::string& tableId, LootTable& out, std::string& error);
bool ParseLootTable(const Json& root, const std::string& tableId, LootTable& out, std::string& error);
/// Loads every table in loot_tables.json (v1 map containers select by type.lootTableId).
bool LoadAllLootTables(const std::string& path,
                       std::unordered_map<std::string, LootTable>& out, std::string& error);

/// Optional AI corpse table mapping: monsterId -> loot table id (ai_loot_tables.json).
bool LoadAiLootTables(const std::string& path,
                      std::unordered_map<std::uint32_t, std::string>& out, std::string& error);

struct RolledItem {
    std::uint32_t itemId = 0;
    std::uint32_t count = 1;
};

/// Weighted rolls; unknown itemIds are skipped when a registry is provided.
/// rollCount < 0 uses the table's own [MinRolls, MaxRolls] range.
std::vector<RolledItem> RollLoot(const LootTable& table, std::mt19937& rng,
                                 const ItemRegistry* registry, int rollCount = -1);

} // namespace coop

