#pragma once

// Content-table boundary (ADR-014/016): the loader is the only code that knows the file
// format. Simulated code depends on this interface so a database/proto-backed store can be
// plugged in later without touching World/Gate.

#include <cstdint>
#include <set>
#include <string>
#include <vector>

#include "content/ContentTypes.h"
#include "world/ItemRegistry.h"

namespace coop {

class IContentStore {
public:
    virtual ~IContentStore() = default;

    virtual bool Load(const std::string& configDir,
                      const std::set<std::string>& knownLootTables, std::string& error) = 0;

    virtual bool HasItems() const = 0;
    virtual const std::vector<ItemDef>& Items() const = 0;
    virtual const std::vector<SceneDef>& Scenes() const = 0;
    virtual const std::vector<MonsterDef>& Monsters() const = 0;
    virtual const std::vector<SpawnTableDef>& SpawnTables() const = 0;
    virtual const std::vector<WeaponDef>& Weapons() const = 0;
    virtual const std::vector<AmmoDef>& Ammo() const = 0;
    virtual const std::vector<MeleeDef>& Melee() const = 0;
    virtual const std::vector<ArmorDef>& Armors() const = 0;
    virtual const std::vector<ContainerTypeDef>& ContainerTypes() const = 0;
    virtual const std::vector<ContainerSpawnDef>& ContainerSpawns() const = 0;
    virtual const std::vector<PlayerSpawnDef>& PlayerSpawns() const = 0;
    virtual const std::vector<ExitDef>& Exits() const = 0;
    virtual const std::vector<LoadoutTicketDef>& LoadoutTickets() const = 0;
    virtual const std::vector<WorldItemSpawnDef>& WorldItemSpawns() const = 0;

    virtual const SceneDef* FindScene(const std::string& name) const = 0;
    virtual const MonsterDef* FindMonster(std::uint32_t id) const = 0;
    virtual const SpawnTableDef* FindSpawnTable(const std::string& id) const = 0;
    virtual const WeaponDef* FindWeapon(std::uint32_t id) const = 0;
    virtual const AmmoDef* FindAmmo(std::uint32_t id) const = 0;
    virtual const MeleeDef* FindMelee(std::uint32_t id) const = 0;
    virtual const ArmorDef* FindArmor(std::uint32_t id) const = 0;
    virtual const ContainerTypeDef* FindContainerType(const std::string& id) const = 0;
    virtual const LoadoutTicketDef* FindLoadoutTicket(const std::string& id) const = 0;

    virtual const ContentStats& Stats() const = 0;
    virtual const std::vector<std::string>& Warnings() const = 0;
    virtual std::string TableHash(const std::string& table) const = 0;
    virtual std::string ContentHash() const = 0;
};

} // namespace coop
