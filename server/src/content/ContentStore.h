#pragma once

// Unified content-table loader (ADR-014): scenes/monsters/spawn_tables/items/loadout
// tickets.
//
// Validation policy:
//   * parse errors, duplicate ids and missing required attributes are fail-fast (Load is false)
//   * unknown references are filtered with a warning (monster/loot table, scene/spawn table)
//   * a missing table file is a warning and an empty table; its hash is the "missing" marker
//   * every table gets a canonical content hash; the combined hash is exposed for the wire
//     handshake (the handshake itself is a later protocol change).

#include <array>
#include <cstdint>
#include <map>
#include <set>
#include <string>
#include <vector>

#include "content/IContentStore.h"
#include "world/ItemRegistry.h"

namespace coop {

class ContentStore final : public IContentStore {
public:
    void SetMapFile(const std::string& mapFile) { _mapFile = mapFile; }
    bool Load(const std::string& configDir, const std::set<std::string>& knownLootTables,
              std::string& error) override;

    bool HasItems() const override { return !_items.empty(); }
    const std::vector<ItemDef>& Items() const override { return _items; }
    const std::vector<SceneDef>& Scenes() const override { return _scenes; }
    const std::vector<MonsterDef>& Monsters() const override { return _monsters; }
    const std::vector<SpawnTableDef>& SpawnTables() const override { return _spawnTables; }
    const std::vector<WeaponDef>& Weapons() const override { return _weapons; }
    const std::vector<AmmoDef>& Ammo() const override { return _ammo; }
    const std::vector<MeleeDef>& Melee() const override { return _melee; }
    const std::vector<ArmorDef>& Armors() const override { return _armors; }
    const std::vector<ContainerTypeDef>& ContainerTypes() const override { return _containerTypes; }
    const std::vector<ContainerSpawnDef>& ContainerSpawns() const override { return _containerSpawns; }
    const std::vector<PlayerSpawnDef>& PlayerSpawns() const override { return _playerSpawns; }
    const std::vector<ExitDef>& Exits() const override { return _exits; }
    const std::vector<LoadoutTicketDef>& LoadoutTickets() const override { return _loadoutTickets; }
    const std::vector<WorldItemSpawnDef>& WorldItemSpawns() const override { return _worldItemSpawns; }

    const SceneDef* FindScene(const std::string& name) const override;
    const MonsterDef* FindMonster(std::uint32_t id) const override;
    const SpawnTableDef* FindSpawnTable(const std::string& id) const override;
    const WeaponDef* FindWeapon(std::uint32_t id) const override;
    const AmmoDef* FindAmmo(std::uint32_t id) const override;
    const MeleeDef* FindMelee(std::uint32_t id) const override;
    const ArmorDef* FindArmor(std::uint32_t id) const override;
    const ContainerTypeDef* FindContainerType(const std::string& id) const override;
    const LoadoutTicketDef* FindLoadoutTicket(const std::string& id) const override;

    const ContentStats& Stats() const override { return _stats; }
    const std::vector<std::string>& Warnings() const override { return _warnings; }

    /// Canonical hash of one table ("items"/"scenes"/"monsters"/"spawn_tables"/
    /// "loadout_tickets"); empty when unknown.
    std::string TableHash(const std::string& table) const override;
    /// SHA-256 over the sorted per-table hashes (diagnostics; not the login handshake).
    std::string ContentHash() const override { return _contentHash; }
    /// Binary form of ContentHash().
    std::array<std::uint8_t, 32> ContentHashBytes() const;
    /// SHA-256 of the canonical monster-catalog entries (ADR-020 option B): this is the
    /// value carried by CLogin.contentHash; it deliberately excludes items/scenes/spawns.
    std::string MonsterCatalogHash() const { return _monsterCatalogHash; }
    std::array<std::uint8_t, 32> MonsterCatalogHashBytes() const;

private:
    bool LoadItems(const std::string& path, std::string& error);
    bool LoadScenes(const std::string& path, std::string& error);
    bool LoadMaps(const std::string& configDir, std::string& error);
    bool LoadMonsters(const std::string& path, const std::set<std::string>& knownLootTables,
                      std::string& error);
    bool LoadSpawnTables(const std::string& path, std::string& error);
    bool LoadWeapons(const std::string& path, std::string& error);
    bool LoadAmmo(const std::string& path, std::string& error);
    bool LoadMelee(const std::string& path, std::string& error);
    bool LoadArmors(const std::string& path, std::string& error);
    bool LoadContainers(const std::string& path, const std::set<std::string>& knownLootTables,
                        std::string& error);
    bool LoadContainerSpawns(const std::string& path, std::string& error);
    bool LoadPlayerSpawns(const std::string& path, std::string& error);
    bool LoadExits(const std::string& path, std::string& error);
    bool LoadLoadoutTickets(const std::string& path, std::string& error);
    bool LoadWorldItemSpawns(const std::string& path, std::string& error);

    /// Item lookup used by cross-table validation (linear; the item table is small).
    const ItemDef* FindItemDef(std::uint32_t itemId) const;

    void Warn(const std::string& message);
    void RecordTable(const std::string& table, const std::string& hash);
    void Finish();

    std::vector<ItemDef> _items;
    std::vector<SceneDef> _scenes;
    std::vector<MonsterDef> _monsters;
    std::vector<SpawnTableDef> _spawnTables;
    std::vector<WeaponDef> _weapons;
    std::vector<AmmoDef> _ammo;
    std::vector<MeleeDef> _melee;
    std::vector<ArmorDef> _armors;
    std::vector<ContainerTypeDef> _containerTypes;
    std::vector<ContainerSpawnDef> _containerSpawns;
    std::vector<PlayerSpawnDef> _playerSpawns;
    std::vector<ExitDef> _exits;
    std::vector<LoadoutTicketDef> _loadoutTickets;
    std::vector<WorldItemSpawnDef> _worldItemSpawns;
    std::vector<std::string> _warnings;
    std::map<std::string, std::string> _tableHashes;
    std::string _contentHash;
    std::string _mapFile = "Level_GroundOne_1";
    std::string _monsterCatalogHash;
    ContentStats _stats;
};

} // namespace coop

