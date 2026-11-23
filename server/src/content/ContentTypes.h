#pragma once

#include <array>
#include <cstddef>
#include <cstdint>
#include <string>
#include <vector>

namespace coop {

/// One map scene: the client reports `name` (Level_*), the server maps it to a mapId.
struct SceneDef {
    std::uint16_t mapId = 0;
    std::uint32_t index = 0;
    std::string name;
    std::string spawnTable;                 // empty: no spawns bound
    std::uint16_t activationRadius = 10000; // cm (default 100 m)
};

/// Monster registry entry (ADR-014/019). `key`/`prefabKey` are the stable asset names;
/// `id` is the server-assigned, build-independent id carried by SAiSpawn.
struct MonsterDef {
    std::uint32_t id = 0;
    std::string key;             // stable assetName
    std::string name;            // display/nameKey (optional)
    std::string prefabKey;       // EnemyPreset_* asset name
    std::string characterModelKey;
    std::string animatorControllerKey;
    std::string faction;
    std::string team;            // "scav"/"usec"/.../"all"
    std::int32_t hp = 100;
    double scale = 1.0;
    bool isBoss = false;
    std::uint32_t nativeId = 0;  // Unity path_id, metadata only
    std::string lootTableId;     // empty: no drop table
    std::uint16_t activationRadius = 0; // 0: use scene-global activation

    // D5 combat fields from CharacterRandomPreset (native damage formula).
    double gunCritRateGain = 0.0;
    double damageMultiplier = 1.0;
    double bulletSpeedMultiplier = 1.0;
    double gunDistanceMultiplier = 1.0;
    double meleeDamageMultiplier = 1.0;
    double aiCombatFactor = 1.0;
    std::array<double, 7> elementFactors{1.0, 1.0, 1.0, 1.0, 1.0, 1.0, 1.0};
};

/// Weighted preset choice for a spawner.
struct SpawnPresetDef {
    std::uint32_t monsterId = 0;
    double weight = 1.0;
};

/// One absolute spawn position (centimeters) inside a spawner.
struct SpawnPointDef {
    std::int32_t x = 0;
    std::int32_t y = 0;
    std::int32_t z = 0;
    std::uint16_t yaw = 0;
    std::uint32_t monsterId = 0; // non-zero: exact-mode monster at this point
};

/// Native spawner group. Random mode uses presets+points; exact mode pins each point.
struct SpawnerDef {
    std::string id;
    std::string path;
    std::string scene;
    std::int32_t x = 0;
    std::int32_t y = 0;
    std::int32_t z = 0;
    std::uint16_t yaw = 0;
    std::uint16_t countMin = 1;
    std::uint16_t countMax = 1;
    bool enabled = true;
    std::string mode = "random"; // random | exact

    // Raw root conditions (preserved for later; ignored by M2 full spawn).
    double whenToSpawn = 0.0;
    double spawnChance = 1.0;
    double delayTime = 0.0;
    double spawnTimeSpace = 0.0;
    bool needTrigger = false;
    std::int32_t minDistanceToPlayer = 0;
    std::int32_t maxDistanceToPlayer = 0;

    std::vector<SpawnPresetDef> presets;
    std::vector<SpawnPointDef> points;
};

/// Hand-authored exact placement (does not participate in random selection).
struct ManualSpawnDef {
    std::string id;
    std::uint32_t monsterId = 0;
    std::int32_t x = 0;
    std::int32_t y = 0;
    std::int32_t z = 0;
    std::uint16_t yaw = 0;
    std::uint16_t count = 1;
};

/// Legacy flat row kept for old --data / tests during the dual-read period.
struct MonsterSpawnDef {
    std::uint32_t monsterId = 0;
    std::int32_t x = 0;
    std::int32_t y = 0;
    std::int32_t z = 0;
    std::uint16_t yaw = 0;
    std::uint16_t count = 1;
};

struct SpawnTableDef {
    std::string id;
    std::uint16_t mapId = 0;
    std::string scene;
    std::uint16_t activationRadius = 5000;
    std::uint8_t weather = 0;             // game Weather value; 0 Sunny
    std::uint16_t timeOfDayMinutes = 720; // 0..1439
    std::uint32_t weatherSeed = 0;        // 0 => server random
    std::vector<SpawnerDef> spawners;
    std::vector<ManualSpawnDef> manualSpawns;
    std::vector<MonsterSpawnDef> legacySpawns; // v1 <spawn> rows
};

/// v1 map container type. v1 reuses world_box until category loot tables are authored.
struct ContainerTypeDef {
    std::string id;
    std::string displayNameKey;
    std::uint16_t capacity = 8;
    std::uint16_t rollMin = 0;
    std::uint16_t rollMax = 0;
    double activeChance = 1.0;
    std::string lootTableId;
    std::uint16_t nativeCount = 0;
};

/// One authored container placement on a map.
struct ContainerSpawnDef {
    std::string id;
    std::uint16_t mapId = 0;
    std::string scene;
    std::int32_t x = 0;
    std::int32_t y = 0;
    std::int32_t z = 0;
    std::uint16_t yaw = 0;
    std::string typeId;
};

/// One authored player entry point. `seat` is an ordering hint only.
struct PlayerSpawnDef {
    std::string id;
    std::uint16_t mapId = 0;
    std::string scene;
    std::int32_t x = 0;
    std::int32_t y = 0;
    std::int32_t z = 0;
    std::uint16_t yaw = 0;
    std::uint16_t seat = 0;
};

/// One extraction zone. `radius` is centimeters.
struct ExitDef {
    std::string id;
    std::uint16_t mapId = 0;
    std::string scene;
    std::int32_t x = 0;
    std::int32_t y = 0;
    std::int32_t z = 0;
    std::uint16_t radius = 500;
    std::string nameKey;
};

/// One authored ground item seed (P2). Positions are centimeters; count is per stack.
struct WorldItemSpawnDef {
    std::string id;
    std::uint16_t mapId = 0;
    std::string scene;
    std::int32_t x = 0;
    std::int32_t y = 0;
    std::int32_t z = 0;
    std::uint16_t yaw = 0;
    std::uint32_t itemId = 0;
    std::uint32_t count = 1;
};

/// D5 gun table (weapons.xml). Values are native item-setting values, not multipliers.
struct WeaponDef {
    std::uint32_t id = 0;
    std::string name;
    std::string caliber;
    std::string element = "physics";
    double damage = 0.0;
    std::uint16_t shotCount = 1;
    double critRate = 0.0;
    double critDamageFactor = 1.0;
    double armorPiercing = 0.0;
    double armorBreak = 0.0;
    double bulletDistance = 0.0;
    double dmgOverDistance = 0.5;
    double shootSpeed = 0.0;
    double penetrate = 0.0;
};

/// D5 ammo table (ammo.xml): gains added to the weapon values.
struct AmmoDef {
    std::uint32_t id = 0;
    std::string name;
    std::string caliber;
    double damageMultiplier = 1.0;
    double critRateGain = 0.0;
    double critDamageFactorGain = 0.0;
    double armorPiercingGain = 0.0;
    double armorBreakGain = 0.0;
    double explosionRange = 0.0;
    double explosionDamage = 0.0;
    double bleedChance = 0.0;
    double buffChanceMultiplier = 1.0;
    double durabilityCost = 0.0;
};

/// D5 melee table (melee.xml).
struct MeleeDef {
    std::uint32_t id = 0;
    std::string name;
    std::string element = "physics";
    double damage = 0.0;
    double critRate = 0.0;
    double critDamageFactor = 1.0;
    double armorPiercing = 0.0;
    double attackRange = 0.0;
    double attackSpeed = 0.0;
    double bleedChance = 0.0;
};

/// D5 armor table (armors.xml): values are character-stat additions.
struct ArmorDef {
    std::uint32_t id = 0;
    std::string name;
    std::int32_t quality = 0;
    double bodyArmor = 0.0;
    double headArmor = 0.0;
    double maxDurability = 0.0;
};

/// One item stack inside a loadout ticket bag (backpack / pet bag).
struct LoadoutItemDef {
    std::uint32_t itemId = 0;
    std::uint32_t count = 1;
};

/// One attachment pre-installed on an equipment slot item. `slotKey` is the native
/// `ItemStatsSystem.Item.Slots` key (e.g. the weapon's scope/muzzle slot), not the
/// player equipment slot index.
struct LoadoutModDef {
    std::string slotKey;
    std::uint32_t itemId = 0;
};

/// One equipment slot entry of a loadout ticket. `slot` uses the PlayerComponent.loadout
/// slot semantics: 0 primary / 1 secondary / 2 melee / 3 armor / 4 helmet / 5 face mask /
/// 6 backpack / 7 headset / 8-9 totem.
struct LoadoutSlotDef {
    std::uint8_t slot = 0;
    std::uint32_t itemId = 0;
    std::uint32_t count = 1;
    std::vector<LoadoutModDef> mods;
};

/// One complete starting kit (T1): equipment slots + backpack contents + pet bag contents.
/// Every ticket is self-contained: nothing is inherited from another ticket or a default.
struct LoadoutTicketDef {
    std::string id;
    std::string nameKey; // localization key, unique per ticket
    std::vector<LoadoutSlotDef> equipment;
    std::vector<LoadoutItemDef> backpack;
    std::vector<LoadoutItemDef> petBag;
};

struct ContentStats {
    std::size_t items = 0;
    std::size_t scenes = 0;
    std::size_t monsters = 0;
    std::size_t spawnTables = 0;
    std::size_t spawners = 0;
    std::size_t spawnPoints = 0;
    std::size_t manualSpawns = 0;
    std::size_t legacySpawns = 0;
    std::size_t spawns = 0; // spawners + manual + legacy (compat summary)
    std::size_t weapons = 0;
    std::size_t ammo = 0;
    std::size_t melee = 0;
    std::size_t armors = 0;
    std::size_t containerTypes = 0;
    std::size_t containerSpawns = 0;
    std::size_t playerSpawns = 0;
    std::size_t exits = 0;
    std::size_t loadoutTickets = 0;
    std::size_t worldItemSpawns = 0;
};

} // namespace coop
