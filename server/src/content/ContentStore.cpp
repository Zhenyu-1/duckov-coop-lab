#include "content/ContentStore.h"

#include <algorithm>
#include <cmath>
#include <cstdint>
#include <filesystem>
#include <fstream>
#include <unordered_set>

#include "content/ContentHash.h"
#include "content/XmlDoc.h"
#include "protocol/Messages.h"

namespace fs = std::filesystem;

namespace coop {

namespace {

std::string Where(const std::string& path, const tinyxml2::XMLElement* element)
{
    return path + ":" + std::to_string(element->GetLineNum());
}

std::array<std::uint8_t, 32> HexToBytes(const std::string& hex)
{
    std::array<std::uint8_t, 32> bytes{};
    if (hex.size() != 64)
        return bytes;
    auto nibble = [](char c) -> std::uint8_t {
        if (c >= '0' && c <= '9')
            return static_cast<std::uint8_t>(c - '0');
        if (c >= 'a' && c <= 'f')
            return static_cast<std::uint8_t>(10 + c - 'a');
        if (c >= 'A' && c <= 'F')
            return static_cast<std::uint8_t>(10 + c - 'A');
        return 0;
    };
    for (std::size_t i = 0; i < bytes.size(); ++i)
        bytes[i] = static_cast<std::uint8_t>((nibble(hex[i * 2]) << 4) | nibble(hex[i * 2 + 1]));
    return bytes;
}

/// ADR-020 option B: stable, client-reproducible catalog hash. The wire carries only this
/// hash; items/scenes/spawn tables are server-side concerns and are not part of it.
///
/// Canonical format (UTF-8, sorted by monster id, one line per entry):
///   monster-catalog-v1\n
///   id \t assetName \t characterModelKey \t animatorControllerKey \t team \t maxHp \t scaleMilli \t nativeId \n
/// scaleMilli = round(scale * 1000), so no cross-language float formatting is involved.
std::string CanonicalMonsterCatalog(const std::vector<MonsterDef>& monsters)
{
    std::vector<const MonsterDef*> sorted;
    sorted.reserve(monsters.size());
    for (const auto& monster : monsters)
        sorted.push_back(&monster);
    std::sort(sorted.begin(), sorted.end(), [](const MonsterDef* a, const MonsterDef* b) {
        return a->id < b->id;
    });

    std::string out = "monster-catalog-v1\n";
    for (const MonsterDef* monster : sorted) {
        const std::string& asset = monster->key.empty() ? monster->prefabKey : monster->key;
        const long long scaleMilli = static_cast<long long>(std::llround(monster->scale * 1000.0));
        out += std::to_string(monster->id);
        out += '\t';
        out += asset;
        out += '\t';
        out += monster->characterModelKey;
        out += '\t';
        out += monster->animatorControllerKey;
        out += '\t';
        out += monster->team;
        out += '\t';
        out += std::to_string(monster->hp);
        out += '\t';
        out += std::to_string(scaleMilli);
        out += '\t';
        out += std::to_string(monster->nativeId);
        out += '\n';
    }
    return out;
}

} // namespace

bool ContentStore::Load(const std::string& configDir, const std::set<std::string>& knownLootTables,
                        std::string& error)
{
    _items.clear();
    _scenes.clear();
    _monsters.clear();
    _spawnTables.clear();
    _weapons.clear();
    _ammo.clear();
    _melee.clear();
    _armors.clear();
    _containerTypes.clear();
    _containerSpawns.clear();
    _playerSpawns.clear();
    _exits.clear();
    _loadoutTickets.clear();
    _worldItemSpawns.clear();
    _warnings.clear();
    _tableHashes.clear();
    _contentHash.clear();
    _stats = ContentStats{};

    if (!LoadItems(configDir + "/items.xml", error))
        return false;
    // Loadout tickets reference item ids, so they are loaded right after the item table.
    if (!LoadLoadoutTickets(configDir + "/loadout_tickets.xml", error))
        return false;
    if (!LoadMonsters(configDir + "/monsters.xml", knownLootTables, error))
        return false;
    if (!LoadContainers(configDir + "/containers.xml", knownLootTables, error))
        return false;

    bool hasMapFiles = false;
    if (fs::exists(configDir + "/maps") && fs::is_directory(configDir + "/maps")) {
        for (const auto& entry : fs::directory_iterator(configDir + "/maps")) {
            if (entry.is_regular_file() && entry.path().extension() == ".xml") {
                hasMapFiles = true;
                break;
            }
        }
    }
    if (hasMapFiles) {
        if (!LoadMaps(configDir, error))
            return false;
    } else {
        // Legacy per-table files (kept for old fixtures / one transition cycle).
        if (!LoadSpawnTables(configDir + "/spawn_tables.xml", error))
            return false;
        if (!LoadScenes(configDir + "/scenes.xml", error))
            return false;
    }

    if (!LoadWeapons(configDir + "/weapons.xml", error))
        return false;
    if (!LoadAmmo(configDir + "/ammo.xml", error))
        return false;
    if (!LoadMelee(configDir + "/melee.xml", error))
        return false;
    if (!LoadArmors(configDir + "/armors.xml", error))
        return false;
    if (!hasMapFiles) {
        if (!LoadContainerSpawns(configDir + "/container_spawns.xml", error))
            return false;
        if (!LoadPlayerSpawns(configDir + "/player_spawns.xml", error))
            return false;
        if (!LoadExits(configDir + "/exits.xml", error))
            return false;
    }

    // World-item seeds are validated against the scene table, so load them after maps/scenes.
    if (!LoadWorldItemSpawns(configDir + "/world_item_spawns.xml", error))
        return false;

    Finish();
    return true;
}

bool ContentStore::LoadMaps(const std::string& configDir, std::string& error)
{
    // A map file is the single source for scene bindings, AI spawn tables and
    // map-owned entity placements. The existing section loaders still own the
    // validation; this adapter feeds each section as an in-memory-shaped temp
    // table so old fixtures keep working while the on-disk config stays one file.
    const fs::path mapsDir = fs::path(configDir) / "maps";
    std::uint64_t mapLoadSerial = 0;
    std::string mapFilter = _mapFile;
    if (mapFilter.size() > 4 && mapFilter.substr(mapFilter.size() - 4) == ".xml")
        mapFilter.resize(mapFilter.size() - 4);
    std::vector<fs::path> files;
    for (const auto& entry : fs::directory_iterator(mapsDir)) {
        if (!entry.is_regular_file() || entry.path().extension() != ".xml")
            continue;
        if (!mapFilter.empty() && entry.path().stem().string() != mapFilter)
            continue;
        files.push_back(entry.path());
    }
    if (files.empty()) {
        error = mapsDir.string() + ": no map file selected (" + mapFilter + ")";
        return false;
    }
    std::sort(files.begin(), files.end());

    for (const auto& file : files) {
        tinyxml2::XMLDocument doc;
        if (!xml::LoadFile(file.string(), doc, error))
            return false;
        const tinyxml2::XMLElement* root = xml::TableRoot(doc, file.string(), "level", error);
        if (root == nullptr)
            return false;

        std::string mapIdText;
        std::string primaryScene;
        if (!xml::RequiredString(root, "mapId", mapIdText, error, file.string()) ||
            !xml::RequiredString(root, "primaryScene", primaryScene, error, file.string()))
            return false;
        (void)mapIdText;
        (void)primaryScene;

        const auto* scenes = root->FirstChildElement("scenes");
        const auto* ai = root->FirstChildElement("ai");
        const auto* containers = root->FirstChildElement("containers");
        const auto* players = root->FirstChildElement("playerSpawns");
        const auto* exits = root->FirstChildElement("exits");
        if (scenes == nullptr || ai == nullptr || containers == nullptr ||
            players == nullptr || exits == nullptr) {
            error = file.string() + ": level requires scenes/ai/containers/playerSpawns/exits";
            return false;
        }

        const auto tempDir = fs::temp_directory_path() /
                             ("duckovcoop_map_" + file.stem().string() + "_" +
                              std::to_string(++mapLoadSerial));
        std::error_code ec;
        fs::remove_all(tempDir, ec);
        fs::create_directories(tempDir, ec);

        auto writeSection = [&](const tinyxml2::XMLElement* section, const char* rootName,
                                const char* version) -> bool {
            tinyxml2::XMLDocument out;
            auto* outRoot = out.NewElement(rootName);
            outRoot->SetAttribute("version", version);
            out.InsertFirstChild(outRoot);
            for (const auto* child = section->FirstChildElement(); child != nullptr;
                 child = child->NextSiblingElement()) {
                outRoot->InsertEndChild(child->DeepClone(&out));
            }
            tinyxml2::XMLPrinter printer;
            outRoot->Accept(&printer);
            std::ofstream stream(tempDir / (std::string(rootName) + ".xml"), std::ios::binary);
            if (!stream)
                return false;
            stream << printer.CStr();
            return stream.good();
        };

        if (!writeSection(scenes, "scenes", "1") ||
            !writeSection(ai, "spawn_tables", "2") ||
            !writeSection(containers, "containerSpawns", "1") ||
            !writeSection(players, "playerSpawns", "1") ||
            !writeSection(exits, "exits", "1")) {
            error = file.string() + ": failed to stage map sections";
            fs::remove_all(tempDir, ec);
            return false;
        }

        const bool ok =
            LoadSpawnTables((tempDir / "spawn_tables.xml").string(), error) &&
            LoadScenes((tempDir / "scenes.xml").string(), error) &&
            LoadContainerSpawns((tempDir / "containerSpawns.xml").string(), error) &&
            LoadPlayerSpawns((tempDir / "playerSpawns.xml").string(), error) &&
            LoadExits((tempDir / "exits.xml").string(), error);
        fs::remove_all(tempDir, ec);
        if (!ok)
            return false;
    }
    return true;
}

void ContentStore::Finish()
{
    _stats.items = _items.size();
    _stats.scenes = _scenes.size();
    _stats.monsters = _monsters.size();
    _stats.spawnTables = _spawnTables.size();
    _stats.spawners = 0;
    _stats.spawnPoints = 0;
    _stats.manualSpawns = 0;
    _stats.legacySpawns = 0;
    for (const auto& table : _spawnTables) {
        _stats.spawners += table.spawners.size();
        _stats.manualSpawns += table.manualSpawns.size();
        _stats.legacySpawns += table.legacySpawns.size();
        for (const auto& spawner : table.spawners)
            _stats.spawnPoints += spawner.points.size();
    }
    _stats.spawns = _stats.spawners + _stats.manualSpawns + _stats.legacySpawns;
    _stats.weapons = _weapons.size();
    _stats.ammo = _ammo.size();
    _stats.melee = _melee.size();
    _stats.armors = _armors.size();
    _stats.containerTypes = _containerTypes.size();
    _stats.containerSpawns = _containerSpawns.size();
    _stats.playerSpawns = _playerSpawns.size();
    _stats.exits = _exits.size();
    _stats.loadoutTickets = _loadoutTickets.size();
    _stats.worldItemSpawns = _worldItemSpawns.size();

    _contentHash = CombineTableHashes(_tableHashes);
    const std::string canonicalCatalog = CanonicalMonsterCatalog(_monsters);
    _monsterCatalogHash = Sha256Hex(canonicalCatalog.data(), canonicalCatalog.size());
}

void ContentStore::Warn(const std::string& message)
{
    _warnings.push_back(message);
}

void ContentStore::RecordTable(const std::string& table, const std::string& hash)
{
    _tableHashes[table] = hash;
}

std::string ContentStore::TableHash(const std::string& table) const
{
    const auto it = _tableHashes.find(table);
    return it != _tableHashes.end() ? it->second : std::string{};
}

std::array<std::uint8_t, 32> ContentStore::ContentHashBytes() const
{
    return HexToBytes(_contentHash);
}

std::array<std::uint8_t, 32> ContentStore::MonsterCatalogHashBytes() const
{
    return HexToBytes(_monsterCatalogHash);
}

const SceneDef* ContentStore::FindScene(const std::string& name) const
{
    for (const auto& scene : _scenes)
        if (scene.name == name)
            return &scene;
    return nullptr;
}

const MonsterDef* ContentStore::FindMonster(std::uint32_t id) const
{
    for (const auto& monster : _monsters)
        if (monster.id == id)
            return &monster;
    return nullptr;
}

const SpawnTableDef* ContentStore::FindSpawnTable(const std::string& id) const
{
    for (const auto& table : _spawnTables)
        if (table.id == id)
            return &table;
    return nullptr;
}

const WeaponDef* ContentStore::FindWeapon(std::uint32_t id) const
{
    for (const auto& weapon : _weapons)
        if (weapon.id == id)
            return &weapon;
    return nullptr;
}

const AmmoDef* ContentStore::FindAmmo(std::uint32_t id) const
{
    for (const auto& ammo : _ammo)
        if (ammo.id == id)
            return &ammo;
    return nullptr;
}

const MeleeDef* ContentStore::FindMelee(std::uint32_t id) const
{
    for (const auto& melee : _melee)
        if (melee.id == id)
            return &melee;
    return nullptr;
}

const ArmorDef* ContentStore::FindArmor(std::uint32_t id) const
{
    for (const auto& armor : _armors)
        if (armor.id == id)
            return &armor;
    return nullptr;
}

const ContainerTypeDef* ContentStore::FindContainerType(const std::string& id) const
{
    for (const auto& type : _containerTypes)
        if (type.id == id)
            return &type;
    return nullptr;
}

const LoadoutTicketDef* ContentStore::FindLoadoutTicket(const std::string& id) const
{
    for (const auto& ticket : _loadoutTickets)
        if (ticket.id == id)
            return &ticket;
    return nullptr;
}

const ItemDef* ContentStore::FindItemDef(std::uint32_t itemId) const
{
    for (const auto& item : _items)
        if (item.id == itemId)
            return &item;
    return nullptr;
}

bool ContentStore::LoadItems(const std::string& path, std::string& error)
{
    if (!fs::exists(path)) {
        Warn("items.xml missing (" + path + "), item table is empty");
        RecordTable("items", MissingTableHash("items"));
        return true;
    }

    tinyxml2::XMLDocument doc;
    if (!xml::LoadFile(path, doc, error))
        return false;
    const tinyxml2::XMLElement* root = xml::TableRoot(doc, path, "items", error);
    if (root == nullptr)
        return false;

    std::uint32_t version = 0;
    if (!xml::OptionalVersion(root, version, error, path))
        return false;
    if (version != 1)
        Warn(path + ": unsupported table version " + std::to_string(version) + " (parsed as v1)");

    std::unordered_set<std::uint32_t> seen;
    for (const tinyxml2::XMLElement* element = root->FirstChildElement(); element != nullptr;
         element = element->NextSiblingElement()) {
        if (std::string(element->Name()) != "item") {
            Warn(Where(path, element) + ": unknown element <" + element->Name() + "> (skipped)");
            continue;
        }

        const std::string where = Where(path, element);
        ItemDef def;
        if (!xml::RequiredU32(element, "id", def.id, error, where))
            return false;
        if (!xml::RequiredString(element, "name", def.name, error, where))
            return false;
        if (!xml::OptionalU16(element, "size", 1, def.size, error, where))
            return false;
        if (!xml::OptionalU32(element, "stack", 1, def.stack, error, where))
            return false;
        if (!xml::OptionalDouble(element, "weight", 0.0, def.weight, error, where))
            return false;
        if (!xml::OptionalI32(element, "quality", 0, def.quality, error, where))
            return false;
        if (!xml::OptionalI32(element, "value", 0, def.value, error, where))
            return false;
        if (!xml::OptionalI32(element, "displayQuality", 0, def.displayQuality, error, where))
            return false;

        // T1 display metadata: native Item.Quality is a small tier value, but the dump also
        // carries sentinel rows (99/999/9999 for perks and quest items), so the bound is u16.
        if (def.quality < 0 || def.quality > 65535) {
            error = where + ": item " + std::to_string(def.id) + " quality " +
                    std::to_string(def.quality) + " outside 0..65535";
            return false;
        }
        // Native Item.Value is a price; guard against overflow/garbage in hand-edited tables.
        if (def.value < 0 || def.value > 1000000000) {
            error = where + ": item " + std::to_string(def.id) + " value " +
                    std::to_string(def.value) + " outside 0..1000000000";
            return false;
        }
        // DisplayQuality enum: 0 None / 1 White / 2 Green / 3 Blue / 4 Purple / 5 Orange /
        // 6 LightRed / 7 Red. Larger values would index outside the native color table.
        if (def.displayQuality < 0 || def.displayQuality > 7) {
            error = where + ": item " + std::to_string(def.id) + " displayQuality " +
                    std::to_string(def.displayQuality) + " outside 0..7";
            return false;
        }

        // ADR-017: the game table is authoritative and counts are u32 since wire v2, so this
        // is an informational warning (the value is kept as-is, never clipped).
        if (def.stack > 65535u) {
            Warn(where + ": item " + std::to_string(def.id) + " stack " +
                 std::to_string(def.stack) +
                 " exceeds u16 (65535); kept as u32 (ADR-017, wire v2)");
        }

        if (!seen.insert(def.id).second) {
            error = where + ": duplicate item id " + std::to_string(def.id);
            return false;
        }
        _items.push_back(std::move(def));
    }

    RecordTable("items", CanonicalTableHash(doc));
    return true;
}

bool ContentStore::LoadMonsters(const std::string& path,
                                const std::set<std::string>& knownLootTables, std::string& error)
{
    if (!fs::exists(path)) {
        Warn("monsters.xml missing (" + path + "), monster table is empty");
        RecordTable("monsters", MissingTableHash("monsters"));
        return true;
    }

    tinyxml2::XMLDocument doc;
    if (!xml::LoadFile(path, doc, error))
        return false;
    const tinyxml2::XMLElement* root = xml::TableRoot(doc, path, "monsters", error);
    if (root == nullptr)
        return false;

    std::uint32_t version = 0;
    if (!xml::OptionalVersion(root, version, error, path))
        return false;
    if (version > 2)
        Warn(path + ": unsupported table version " + std::to_string(version) + " (parsed as v2)");

    std::unordered_set<std::uint32_t> seen;
    for (const tinyxml2::XMLElement* element = root->FirstChildElement(); element != nullptr;
         element = element->NextSiblingElement()) {
        if (std::string(element->Name()) != "monster") {
            Warn(Where(path, element) + ": unknown element <" + element->Name() + "> (skipped)");
            continue;
        }

        const std::string where = Where(path, element);
        MonsterDef def;
        if (!xml::RequiredU32(element, "id", def.id, error, where))
            return false;
        if (!xml::RequiredString(element, "prefabKey", def.prefabKey, error, where))
            return false;

        def.name = xml::OptionalString(element, "name", "monster_" + std::to_string(def.id));
        def.key = xml::OptionalString(element, "key", def.prefabKey);
        def.characterModelKey = xml::OptionalString(element, "characterModelKey", "");
        def.animatorControllerKey = xml::OptionalString(element, "animatorControllerKey", "");
        def.faction = xml::OptionalString(element, "faction", "");
        def.team = xml::OptionalString(element, "team", "");
        def.isBoss = xml::OptionalString(element, "isBoss", "false") == "true";
        def.nativeId = 0;
        if (!xml::OptionalU32(element, "nativeId", 0, def.nativeId, error, where))
            return false;
        def.lootTableId = xml::OptionalString(element, "lootTableId", "");
        if (!xml::OptionalI32(element, "hp", 100, def.hp, error, where))
            return false;
        if (!xml::OptionalDouble(element, "scale", 1.0, def.scale, error, where))
            return false;
        if (!xml::OptionalU16(element, "activationRadius", 0, def.activationRadius, error, where))
            return false;
        if (!xml::OptionalDouble(element, "gunCritRateGain", 0.0, def.gunCritRateGain, error, where))
            return false;
        if (!xml::OptionalDouble(element, "damageMultiplier", 1.0, def.damageMultiplier, error, where))
            return false;
        if (!xml::OptionalDouble(element, "bulletSpeedMultiplier", 1.0, def.bulletSpeedMultiplier,
                                 error, where))
            return false;
        if (!xml::OptionalDouble(element, "gunDistanceMultiplier", 1.0, def.gunDistanceMultiplier,
                                 error, where))
            return false;
        if (!xml::OptionalDouble(element, "meleeDamageMultiplier", 1.0, def.meleeDamageMultiplier,
                                 error, where))
            return false;
        if (!xml::OptionalDouble(element, "aiCombatFactor", 1.0, def.aiCombatFactor, error, where))
            return false;

        const char* elementNames[] = {"Physics", "Fire", "Ice", "Poison", "Electricity", "Space", "Ghost"};
        for (std::size_t i = 0; i < def.elementFactors.size(); ++i) {
            const std::string attr = std::string("elementFactor_") + elementNames[i];
            if (!xml::OptionalDouble(element, attr.c_str(), 1.0, def.elementFactors[i], error, where))
                return false;
        }

        if (!def.lootTableId.empty() && !knownLootTables.empty() &&
            knownLootTables.find(def.lootTableId) == knownLootTables.end()) {
            Warn(Where(path, element) + ": unknown lootTableId '" + def.lootTableId +
                 "' for monster " + std::to_string(def.id) + " (cleared)");
            def.lootTableId.clear();
        }

        if (!seen.insert(def.id).second) {
            error = where + ": duplicate monster id " + std::to_string(def.id);
            return false;
        }
        _monsters.push_back(std::move(def));
    }

    RecordTable("monsters", CanonicalTableHash(doc));
    return true;
}

bool ContentStore::LoadSpawnTables(const std::string& path, std::string& error)
{
    if (!fs::exists(path)) {
        Warn("spawn_tables.xml missing (" + path + "), spawn table is empty");
        RecordTable("spawn_tables", MissingTableHash("spawn_tables"));
        return true;
    }

    tinyxml2::XMLDocument doc;
    if (!xml::LoadFile(path, doc, error))
        return false;
    const tinyxml2::XMLElement* root = xml::TableRoot(doc, path, "spawn_tables", error);
    if (root == nullptr)
        return false;

    std::uint32_t version = 0;
    if (!xml::OptionalVersion(root, version, error, path))
        return false;
    if (version > 2)
        Warn(path + ": unsupported table version " + std::to_string(version) + " (parsed as v2)");

    std::unordered_set<std::string> seenTables;
    for (const tinyxml2::XMLElement* element = root->FirstChildElement(); element != nullptr;
         element = element->NextSiblingElement()) {
        if (std::string(element->Name()) != "spawnTable") {
            Warn(Where(path, element) + ": unknown element <" + element->Name() + "> (skipped)");
            continue;
        }

        const std::string where = Where(path, element);
        SpawnTableDef table;
        if (!xml::RequiredString(element, "id", table.id, error, where))
            return false;
        if (!xml::RequiredU16(element, "mapId", table.mapId, error, where))
            return false;
        table.scene = xml::OptionalString(element, "scene", "");
        if (!xml::OptionalU16(element, "activationRadius", 5000, table.activationRadius, error, where))
            return false;
        std::uint32_t weather = 0;
        if (!xml::OptionalU32(element, "weather", 0, weather, error, where))
            return false;
        const bool knownWeather = weather == 0 || weather == 1 || weather == 2 ||
                                  weather == 3 || weather == 4 || weather == 22;
        if (!knownWeather) {
            Warn(where + ": unknown weather " + std::to_string(weather) +
                 " (fallback Sunny)");
            weather = 0;
        }
        table.weather = static_cast<std::uint8_t>(weather);
        if (!xml::OptionalU16(element, "timeOfDayMinutes", 720, table.timeOfDayMinutes, error, where))
            return false;
        if (table.timeOfDayMinutes >= 1440) {
            error = where + ": timeOfDayMinutes must be 0..1439";
            return false;
        }
        if (!xml::OptionalU32(element, "weatherSeed", 0, table.weatherSeed, error, where))
            return false;

        if (!seenTables.insert(table.id).second) {
            error = where + ": duplicate spawn table id '" + table.id + "'";
            return false;
        }

        std::unordered_set<std::string> seenSpawners;
        std::unordered_set<std::string> seenManuals;

        for (const tinyxml2::XMLElement* child = element->FirstChildElement(); child != nullptr;
             child = child->NextSiblingElement()) {
            const std::string childName = child->Name() != nullptr ? child->Name() : "";
            const std::string childWhere = Where(path, child);

            if (childName == "spawn") {
                // v1 legacy flat row: keep loading so old --data/e2e directories still work.
                MonsterSpawnDef def;
                if (!xml::RequiredU32(child, "monsterId", def.monsterId, error, childWhere))
                    return false;
                if (!xml::RequiredI32(child, "x", def.x, error, childWhere))
                    return false;
                if (!xml::RequiredI32(child, "y", def.y, error, childWhere))
                    return false;
                if (!xml::RequiredI32(child, "z", def.z, error, childWhere))
                    return false;
                if (!xml::OptionalU16(child, "yaw", 0, def.yaw, error, childWhere))
                    return false;
                if (!xml::OptionalU16(child, "count", 1, def.count, error, childWhere))
                    return false;
                if (FindMonster(def.monsterId) == nullptr) {
                    Warn(childWhere + ": unknown monsterId " + std::to_string(def.monsterId) +
                         " in legacy spawn table '" + table.id + "' (filtered)");
                    continue;
                }
                table.legacySpawns.push_back(def);
                continue;
            }

            if (childName == "manualSpawn") {
                ManualSpawnDef def;
                if (!xml::RequiredString(child, "id", def.id, error, childWhere))
                    return false;
                if (!xml::RequiredU32(child, "monsterId", def.monsterId, error, childWhere))
                    return false;
                if (!xml::RequiredI32(child, "x", def.x, error, childWhere))
                    return false;
                if (!xml::RequiredI32(child, "y", def.y, error, childWhere))
                    return false;
                if (!xml::RequiredI32(child, "z", def.z, error, childWhere))
                    return false;
                if (!xml::OptionalU16(child, "yaw", 0, def.yaw, error, childWhere))
                    return false;
                if (!xml::OptionalU16(child, "count", 1, def.count, error, childWhere))
                    return false;
                if (!seenManuals.insert(def.id).second) {
                    error = childWhere + ": duplicate manualSpawn id '" + def.id + "'";
                    return false;
                }
                if (FindMonster(def.monsterId) == nullptr) {
                    Warn(childWhere + ": unknown monsterId " + std::to_string(def.monsterId) +
                         " in manualSpawn '" + def.id + "' (filtered)");
                    continue;
                }
                table.manualSpawns.push_back(std::move(def));
                continue;
            }

            if (childName != "spawner") {
                Warn(childWhere + ": unknown element <" + childName + "> (skipped)");
                continue;
            }

            SpawnerDef spawner;
            if (!xml::RequiredString(child, "id", spawner.id, error, childWhere))
                return false;
            if (!seenSpawners.insert(spawner.id).second) {
                error = childWhere + ": duplicate spawner id '" + spawner.id + "'";
                return false;
            }
            spawner.path = xml::OptionalString(child, "path", "");
            spawner.scene = xml::OptionalString(child, "scene", "");
            spawner.mode = xml::OptionalString(child, "mode", "random");
            if (spawner.mode != "random" && spawner.mode != "exact") {
                error = childWhere + ": spawner '" + spawner.id + "' has unknown mode '" +
                        spawner.mode + "'";
                return false;
            }
            spawner.enabled = xml::OptionalString(child, "enabled", "true") != "false";
            if (!xml::OptionalI32(child, "x", 0, spawner.x, error, childWhere))
                return false;
            if (!xml::OptionalI32(child, "y", 0, spawner.y, error, childWhere))
                return false;
            if (!xml::OptionalI32(child, "z", 0, spawner.z, error, childWhere))
                return false;
            if (!xml::OptionalU16(child, "yaw", 0, spawner.yaw, error, childWhere))
                return false;
            if (!xml::OptionalU16(child, "countMin", 1, spawner.countMin, error, childWhere))
                return false;
            if (!xml::OptionalU16(child, "countMax", 1, spawner.countMax, error, childWhere))
                return false;
            if (spawner.countMin > spawner.countMax) {
                error = childWhere + ": spawner '" + spawner.id + "' has countMin > countMax";
                return false;
            }
            if (!xml::OptionalDouble(child, "whenToSpawn", 0.0, spawner.whenToSpawn, error, childWhere))
                return false;
            if (!xml::OptionalDouble(child, "spawnChance", 1.0, spawner.spawnChance, error, childWhere))
                return false;
            if (!xml::OptionalDouble(child, "delayTime", 0.0, spawner.delayTime, error, childWhere))
                return false;
            if (!xml::OptionalDouble(child, "spawnTimeSpace", 0.0, spawner.spawnTimeSpace, error,
                                     childWhere))
                return false;
            spawner.needTrigger = xml::OptionalString(child, "needTrigger", "false") == "true";
            if (!xml::OptionalI32(child, "minDistanceToPlayer", 0, spawner.minDistanceToPlayer, error,
                                  childWhere))
                return false;
            if (!xml::OptionalI32(child, "maxDistanceToPlayer", 0, spawner.maxDistanceToPlayer, error,
                                  childWhere))
                return false;

            for (const tinyxml2::XMLElement* part = child->FirstChildElement(); part != nullptr;
                 part = part->NextSiblingElement()) {
                const std::string partName = part->Name() != nullptr ? part->Name() : "";
                const std::string partWhere = Where(path, part);

                if (partName == "preset") {
                    SpawnPresetDef preset;
                    if (!xml::RequiredU32(part, "monsterId", preset.monsterId, error, partWhere))
                        return false;
                    if (!xml::OptionalDouble(part, "weight", 1.0, preset.weight, error, partWhere))
                        return false;
                    if (preset.weight <= 0.0) {
                        error = partWhere + ": preset weight must be > 0";
                        return false;
                    }
                    if (FindMonster(preset.monsterId) == nullptr) {
                        Warn(partWhere + ": unknown monsterId " +
                             std::to_string(preset.monsterId) + " in spawner '" + spawner.id +
                             "' (filtered)");
                        continue;
                    }
                    spawner.presets.push_back(preset);
                    continue;
                }

                if (partName == "point") {
                    SpawnPointDef point;
                    if (!xml::RequiredI32(part, "x", point.x, error, partWhere))
                        return false;
                    if (!xml::RequiredI32(part, "y", point.y, error, partWhere))
                        return false;
                    if (!xml::RequiredI32(part, "z", point.z, error, partWhere))
                        return false;
                    if (!xml::OptionalU16(part, "yaw", 0, point.yaw, error, partWhere))
                        return false;
                    if (!xml::OptionalU32(part, "monsterId", 0, point.monsterId, error, partWhere))
                        return false;
                    if (point.monsterId != 0 && FindMonster(point.monsterId) == nullptr) {
                        Warn(partWhere + ": unknown point monsterId " +
                             std::to_string(point.monsterId) + " in spawner '" + spawner.id +
                             "' (cleared)");
                        point.monsterId = 0;
                    }
                    spawner.points.push_back(point);
                    continue;
                }

                Warn(partWhere + ": unknown element <" + partName + "> (skipped)");
            }

            if (spawner.mode == "random" && spawner.presets.empty()) {
                Warn(childWhere + ": spawner '" + spawner.id +
                     "' has no presets in random mode (skipped)");
                continue;
            }
            if (spawner.countMax > spawner.points.size())
                Warn(childWhere + ": spawner '" + spawner.id + "' countMax " +
                     std::to_string(spawner.countMax) + " > point count " +
                     std::to_string(spawner.points.size()) + " (will cap at runtime)");

            table.spawners.push_back(std::move(spawner));
        }

        _spawnTables.push_back(std::move(table));
    }

    RecordTable("spawn_tables", CanonicalTableHash(doc));
    return true;
}

bool ContentStore::LoadWeapons(const std::string& path, std::string& error)
{
    if (!fs::exists(path)) {
        Warn("weapons.xml missing (" + path + "), weapon table is empty");
        RecordTable("weapons", MissingTableHash("weapons"));
        return true;
    }
    tinyxml2::XMLDocument doc;
    if (!xml::LoadFile(path, doc, error))
        return false;
    const tinyxml2::XMLElement* root = xml::TableRoot(doc, path, "weapons", error);
    if (root == nullptr)
        return false;

    std::uint32_t version = 0;
    if (!xml::OptionalVersion(root, version, error, path))
        return false;
    if (version > 1)
        Warn(path + ": unsupported table version " + std::to_string(version) + " (parsed as v1)");

    std::unordered_set<std::uint32_t> seen;
    for (const tinyxml2::XMLElement* element = root->FirstChildElement(); element != nullptr;
         element = element->NextSiblingElement()) {
        if (std::string(element->Name()) != "weapon") {
            Warn(Where(path, element) + ": unknown element <" + element->Name() + "> (skipped)");
            continue;
        }
        const std::string where = Where(path, element);
        WeaponDef def;
        if (!xml::RequiredU32(element, "id", def.id, error, where))
            return false;
        def.name = xml::OptionalString(element, "name", "weapon_" + std::to_string(def.id));
        def.caliber = xml::OptionalString(element, "caliber", "");
        def.element = xml::OptionalString(element, "element", "physics");
        if (!xml::OptionalDouble(element, "damage", 0.0, def.damage, error, where))
            return false;
        if (!xml::OptionalU16(element, "shotcount", 1, def.shotCount, error, where))
            return false;
        if (!xml::OptionalDouble(element, "critrate", 0.0, def.critRate, error, where))
            return false;
        if (!xml::OptionalDouble(element, "critdamagefactor", 1.0, def.critDamageFactor, error,
                                 where))
            return false;
        if (!xml::OptionalDouble(element, "armorpiercing", 0.0, def.armorPiercing, error, where))
            return false;
        if (!xml::OptionalDouble(element, "armorbreak", 0.0, def.armorBreak, error, where))
            return false;
        if (!xml::OptionalDouble(element, "bulletdistance", 0.0, def.bulletDistance, error, where))
            return false;
        if (!xml::OptionalDouble(element, "dmgoverdistance", 0.5, def.dmgOverDistance, error, where))
            return false;
        if (!xml::OptionalDouble(element, "shootspeed", 0.0, def.shootSpeed, error, where))
            return false;
        if (!xml::OptionalDouble(element, "penetrate", 0.0, def.penetrate, error, where))
            return false;
        if (!seen.insert(def.id).second) {
            error = where + ": duplicate weapon id " + std::to_string(def.id);
            return false;
        }
        _weapons.push_back(std::move(def));
    }
    RecordTable("weapons", CanonicalTableHash(doc));
    return true;
}

bool ContentStore::LoadAmmo(const std::string& path, std::string& error)
{
    if (!fs::exists(path)) {
        Warn("ammo.xml missing (" + path + "), ammo table is empty");
        RecordTable("ammo", MissingTableHash("ammo"));
        return true;
    }
    tinyxml2::XMLDocument doc;
    if (!xml::LoadFile(path, doc, error))
        return false;
    const tinyxml2::XMLElement* root = xml::TableRoot(doc, path, "ammo", error);
    if (root == nullptr)
        return false;

    std::unordered_set<std::uint32_t> seen;
    for (const tinyxml2::XMLElement* element = root->FirstChildElement(); element != nullptr;
         element = element->NextSiblingElement()) {
        if (std::string(element->Name()) != "ammo") {
            Warn(Where(path, element) + ": unknown element <" + element->Name() + "> (skipped)");
            continue;
        }
        const std::string where = Where(path, element);
        AmmoDef def;
        if (!xml::RequiredU32(element, "id", def.id, error, where))
            return false;
        def.name = xml::OptionalString(element, "name", "ammo_" + std::to_string(def.id));
        def.caliber = xml::OptionalString(element, "caliber", "");
        if (!xml::OptionalDouble(element, "damagemultiplier", 1.0, def.damageMultiplier, error,
                                 where))
            return false;
        if (!xml::OptionalDouble(element, "critrategain", 0.0, def.critRateGain, error, where))
            return false;
        if (!xml::OptionalDouble(element, "critdamagefactorgain", 0.0, def.critDamageFactorGain,
                                 error, where))
            return false;
        if (!xml::OptionalDouble(element, "armorpiercinggain", 0.0, def.armorPiercingGain, error,
                                 where))
            return false;
        if (!xml::OptionalDouble(element, "armorbreakgain", 0.0, def.armorBreakGain, error, where))
            return false;
        if (!xml::OptionalDouble(element, "explosionrange", 0.0, def.explosionRange, error, where))
            return false;
        if (!xml::OptionalDouble(element, "explosiondamage", 0.0, def.explosionDamage, error, where))
            return false;
        if (!xml::OptionalDouble(element, "bleedchance", 0.0, def.bleedChance, error, where))
            return false;
        if (!xml::OptionalDouble(element, "buffchancemultiplier", 1.0, def.buffChanceMultiplier,
                                 error, where))
            return false;
        if (!xml::OptionalDouble(element, "durabilitycost", 0.0, def.durabilityCost, error, where))
            return false;
        if (!seen.insert(def.id).second) {
            error = where + ": duplicate ammo id " + std::to_string(def.id);
            return false;
        }
        _ammo.push_back(std::move(def));
    }
    RecordTable("ammo", CanonicalTableHash(doc));
    return true;
}

bool ContentStore::LoadMelee(const std::string& path, std::string& error)
{
    if (!fs::exists(path)) {
        Warn("melee.xml missing (" + path + "), melee table is empty");
        RecordTable("melee", MissingTableHash("melee"));
        return true;
    }
    tinyxml2::XMLDocument doc;
    if (!xml::LoadFile(path, doc, error))
        return false;
    const tinyxml2::XMLElement* root = xml::TableRoot(doc, path, "melee", error);
    if (root == nullptr)
        return false;

    std::unordered_set<std::uint32_t> seen;
    for (const tinyxml2::XMLElement* element = root->FirstChildElement(); element != nullptr;
         element = element->NextSiblingElement()) {
        if (std::string(element->Name()) != "melee") {
            Warn(Where(path, element) + ": unknown element <" + element->Name() + "> (skipped)");
            continue;
        }
        const std::string where = Where(path, element);
        MeleeDef def;
        if (!xml::RequiredU32(element, "id", def.id, error, where))
            return false;
        def.name = xml::OptionalString(element, "name", "melee_" + std::to_string(def.id));
        def.element = xml::OptionalString(element, "element", "physics");
        if (!xml::OptionalDouble(element, "damage", 0.0, def.damage, error, where))
            return false;
        if (!xml::OptionalDouble(element, "critrate", 0.0, def.critRate, error, where))
            return false;
        if (!xml::OptionalDouble(element, "critdamagefactor", 1.0, def.critDamageFactor, error,
                                 where))
            return false;
        if (!xml::OptionalDouble(element, "armorpiercing", 0.0, def.armorPiercing, error, where))
            return false;
        if (!xml::OptionalDouble(element, "attackrange", 0.0, def.attackRange, error, where))
            return false;
        if (!xml::OptionalDouble(element, "attackspeed", 0.0, def.attackSpeed, error, where))
            return false;
        if (!xml::OptionalDouble(element, "bleedchance", 0.0, def.bleedChance, error, where))
            return false;
        if (!seen.insert(def.id).second) {
            error = where + ": duplicate melee id " + std::to_string(def.id);
            return false;
        }
        _melee.push_back(std::move(def));
    }
    RecordTable("melee", CanonicalTableHash(doc));
    return true;
}

bool ContentStore::LoadArmors(const std::string& path, std::string& error)
{
    if (!fs::exists(path)) {
        Warn("armors.xml missing (" + path + "), armor table is empty");
        RecordTable("armors", MissingTableHash("armors"));
        return true;
    }
    tinyxml2::XMLDocument doc;
    if (!xml::LoadFile(path, doc, error))
        return false;
    const tinyxml2::XMLElement* root = xml::TableRoot(doc, path, "armors", error);
    if (root == nullptr)
        return false;

    std::unordered_set<std::uint32_t> seen;
    for (const tinyxml2::XMLElement* element = root->FirstChildElement(); element != nullptr;
         element = element->NextSiblingElement()) {
        if (std::string(element->Name()) != "armor") {
            Warn(Where(path, element) + ": unknown element <" + element->Name() + "> (skipped)");
            continue;
        }
        const std::string where = Where(path, element);
        ArmorDef def;
        if (!xml::RequiredU32(element, "id", def.id, error, where))
            return false;
        def.name = xml::OptionalString(element, "name", "armor_" + std::to_string(def.id));
        if (!xml::OptionalI32(element, "quality", 0, def.quality, error, where))
            return false;
        if (!xml::OptionalDouble(element, "bodyArmor", 0.0, def.bodyArmor, error, where))
            return false;
        if (!xml::OptionalDouble(element, "headArmor", 0.0, def.headArmor, error, where))
            return false;
        if (!xml::OptionalDouble(element, "maxDurability", 0.0, def.maxDurability, error, where))
            return false;
        if (!seen.insert(def.id).second) {
            error = where + ": duplicate armor id " + std::to_string(def.id);
            return false;
        }
        _armors.push_back(std::move(def));
    }
    RecordTable("armors", CanonicalTableHash(doc));
    return true;
}

bool ContentStore::LoadScenes(const std::string& path, std::string& error)
{
    if (!fs::exists(path)) {
        Warn("scenes.xml missing (" + path + "), scene table is empty");
        RecordTable("scenes", MissingTableHash("scenes"));
        return true;
    }

    tinyxml2::XMLDocument doc;
    if (!xml::LoadFile(path, doc, error))
        return false;
    const tinyxml2::XMLElement* root = xml::TableRoot(doc, path, "scenes", error);
    if (root == nullptr)
        return false;

    std::uint32_t version = 0;
    if (!xml::OptionalVersion(root, version, error, path))
        return false;
    if (version != 1)
        Warn(path + ": unsupported table version " + std::to_string(version) + " (parsed as v1)");

    std::unordered_set<std::string> seenKeys;
    for (const tinyxml2::XMLElement* element = root->FirstChildElement(); element != nullptr;
         element = element->NextSiblingElement()) {
        if (std::string(element->Name()) != "scene") {
            Warn(Where(path, element) + ": unknown element <" + element->Name() + "> (skipped)");
            continue;
        }

        const std::string where = Where(path, element);
        SceneDef def;
        if (!xml::RequiredU16(element, "mapId", def.mapId, error, where))
            return false;
        if (!xml::RequiredString(element, "name", def.name, error, where))
            return false;
        if (!xml::OptionalU32(element, "index", 0, def.index, error, where))
            return false;
        if (!xml::OptionalU16(element, "activationRadius", 10000, def.activationRadius, error, where))
            return false;
        def.spawnTable = xml::OptionalString(element, "spawnTable", "");

        const std::string key = std::to_string(def.mapId) + "/" + def.name;
        if (!seenKeys.insert(key).second) {
            error = where + ": duplicate scene '" + def.name + "' for mapId " +
                    std::to_string(def.mapId);
            return false;
        }

        if (!def.spawnTable.empty()) {
            const SpawnTableDef* table = FindSpawnTable(def.spawnTable);
            if (table == nullptr) {
                Warn(where + ": unknown spawnTable '" + def.spawnTable + "' for scene '" +
                     def.name + "' (cleared)");
                def.spawnTable.clear();
            } else if (table->mapId != def.mapId) {
                Warn(where + ": spawnTable '" + def.spawnTable + "' has mapId " +
                     std::to_string(table->mapId) + " but scene '" + def.name + "' has mapId " +
                     std::to_string(def.mapId));
            }
        }

        _scenes.push_back(std::move(def));
    }

    RecordTable("scenes", CanonicalTableHash(doc));
    return true;
}

bool ContentStore::LoadContainers(const std::string& path,
                                  const std::set<std::string>& knownLootTables,
                                  std::string& error)
{
    if (!fs::exists(path)) {
        Warn("containers.xml missing (" + path + "), container type table is empty");
        RecordTable("containers", MissingTableHash("containers"));
        return true;
    }
    tinyxml2::XMLDocument doc;
    if (!xml::LoadFile(path, doc, error))
        return false;
    const tinyxml2::XMLElement* root = xml::TableRoot(doc, path, "containers", error);
    if (root == nullptr)
        return false;
    std::uint32_t version = 0;
    if (!xml::OptionalVersion(root, version, error, path))
        return false;
    if (version != 1)
        Warn(path + ": unsupported containers version " + std::to_string(version));

    std::unordered_set<std::string> seen;
    for (const tinyxml2::XMLElement* element = root->FirstChildElement(); element != nullptr;
         element = element->NextSiblingElement()) {
        if (std::string(element->Name()) != "containerType") {
            Warn(Where(path, element) + ": unknown element <" + element->Name() + "> (skipped)");
            continue;
        }
        const std::string where = Where(path, element);
        ContainerTypeDef def;
        if (!xml::RequiredString(element, "id", def.id, error, where))
            return false;
        def.displayNameKey = xml::OptionalString(element, "displayNameKey", def.id);
        if (!xml::OptionalU16(element, "capacity", 8, def.capacity, error, where))
            return false;
        if (!xml::OptionalU16(element, "rollMin", 0, def.rollMin, error, where))
            return false;
        if (!xml::OptionalU16(element, "rollMax", def.rollMin, def.rollMax, error, where))
            return false;
        if (!xml::OptionalDouble(element, "activeChance", 1.0, def.activeChance, error, where))
            return false;
        if (!xml::OptionalU16(element, "nativeCount", 0, def.nativeCount, error, where))
            return false;
        def.lootTableId = xml::OptionalString(element, "lootTable", "world_box");
        if (!def.lootTableId.empty() && !knownLootTables.empty() &&
            knownLootTables.find(def.lootTableId) == knownLootTables.end()) {
            Warn(where + ": unknown lootTable '" + def.lootTableId + "' (cleared)");
            def.lootTableId.clear();
        }
        if (def.rollMax < def.rollMin)
            def.rollMax = def.rollMin;
        if (def.capacity == 0)
            def.capacity = 1;
        if (!seen.insert(def.id).second) {
            error = where + ": duplicate container type '" + def.id + "'";
            return false;
        }
        _containerTypes.push_back(std::move(def));
    }
    RecordTable("containers", CanonicalTableHash(doc));
    return true;
}

bool ContentStore::LoadContainerSpawns(const std::string& path, std::string& error)
{
    if (!fs::exists(path)) {
        Warn("container_spawns.xml missing (" + path + "), container spawn table is empty");
        RecordTable("container_spawns", MissingTableHash("container_spawns"));
        return true;
    }
    tinyxml2::XMLDocument doc;
    if (!xml::LoadFile(path, doc, error))
        return false;
    const tinyxml2::XMLElement* root = xml::TableRoot(doc, path, "containerSpawns", error);
    if (root == nullptr)
        return false;
    std::uint32_t version = 0;
    if (!xml::OptionalVersion(root, version, error, path))
        return false;
    if (version != 1)
        Warn(path + ": unsupported containerSpawns version " + std::to_string(version));

    std::unordered_set<std::string> seen;
    for (const tinyxml2::XMLElement* element = root->FirstChildElement(); element != nullptr;
         element = element->NextSiblingElement()) {
        if (std::string(element->Name()) != "spawn") {
            Warn(Where(path, element) + ": unknown element <" + element->Name() + "> (skipped)");
            continue;
        }
        const std::string where = Where(path, element);
        ContainerSpawnDef def;
        if (!xml::RequiredString(element, "id", def.id, error, where))
            return false;
        if (!xml::RequiredU16(element, "mapId", def.mapId, error, where))
            return false;
        def.scene = xml::OptionalString(element, "scene", "");
        if (!xml::RequiredI32(element, "x", def.x, error, where))
            return false;
        if (!xml::RequiredI32(element, "y", def.y, error, where))
            return false;
        if (!xml::RequiredI32(element, "z", def.z, error, where))
            return false;
        if (!xml::OptionalU16(element, "yaw", 0, def.yaw, error, where))
            return false;
        if (!xml::RequiredString(element, "typeId", def.typeId, error, where))
            return false;
        if (FindContainerType(def.typeId) == nullptr) {
            Warn(where + ": unknown container type '" + def.typeId + "' (skipped)");
            continue;
        }
        if (!seen.insert(def.id).second) {
            error = where + ": duplicate container spawn '" + def.id + "'";
            return false;
        }
        _containerSpawns.push_back(std::move(def));
    }
    RecordTable("container_spawns", CanonicalTableHash(doc));
    return true;
}

bool ContentStore::LoadPlayerSpawns(const std::string& path, std::string& error)
{
    if (!fs::exists(path)) {
        Warn("player_spawns.xml missing (" + path + "), player spawn table is empty");
        RecordTable("player_spawns", MissingTableHash("player_spawns"));
        return true;
    }
    tinyxml2::XMLDocument doc;
    if (!xml::LoadFile(path, doc, error))
        return false;
    const tinyxml2::XMLElement* root = xml::TableRoot(doc, path, "playerSpawns", error);
    if (root == nullptr)
        return false;
    std::uint32_t version = 0;
    if (!xml::OptionalVersion(root, version, error, path))
        return false;
    if (version != 1)
        Warn(path + ": unsupported playerSpawns version " + std::to_string(version));

    std::unordered_set<std::string> seen;
    std::unordered_set<std::uint16_t> seats;
    for (const tinyxml2::XMLElement* element = root->FirstChildElement(); element != nullptr;
         element = element->NextSiblingElement()) {
        if (std::string(element->Name()) != "spawn") {
            Warn(Where(path, element) + ": unknown element <" + element->Name() + "> (skipped)");
            continue;
        }
        const std::string where = Where(path, element);
        PlayerSpawnDef def;
        if (!xml::RequiredString(element, "id", def.id, error, where))
            return false;
        if (!xml::RequiredU16(element, "mapId", def.mapId, error, where))
            return false;
        def.scene = xml::OptionalString(element, "scene", "");
        if (!xml::RequiredI32(element, "x", def.x, error, where))
            return false;
        if (!xml::RequiredI32(element, "y", def.y, error, where))
            return false;
        if (!xml::RequiredI32(element, "z", def.z, error, where))
            return false;
        if (!xml::OptionalU16(element, "yaw", 0, def.yaw, error, where))
            return false;
        if (!xml::OptionalU16(element, "seat", 0, def.seat, error, where))
            return false;
        if (!seen.insert(def.id).second) {
            error = where + ": duplicate player spawn '" + def.id + "'";
            return false;
        }
        if (!seats.insert(def.seat).second) {
            error = where + ": duplicate player spawn seat " + std::to_string(def.seat);
            return false;
        }
        _playerSpawns.push_back(std::move(def));
    }
    std::sort(_playerSpawns.begin(), _playerSpawns.end(),
              [](const PlayerSpawnDef& a, const PlayerSpawnDef& b) { return a.seat < b.seat; });
    RecordTable("player_spawns", CanonicalTableHash(doc));
    return true;
}

bool ContentStore::LoadExits(const std::string& path, std::string& error)
{
    if (!fs::exists(path)) {
        Warn("exits.xml missing (" + path + "), exit table is empty");
        RecordTable("exits", MissingTableHash("exits"));
        return true;
    }
    tinyxml2::XMLDocument doc;
    if (!xml::LoadFile(path, doc, error))
        return false;
    const tinyxml2::XMLElement* root = xml::TableRoot(doc, path, "exits", error);
    if (root == nullptr)
        return false;
    std::uint32_t version = 0;
    if (!xml::OptionalVersion(root, version, error, path))
        return false;
    if (version != 1)
        Warn(path + ": unsupported exits version " + std::to_string(version));

    std::unordered_set<std::string> seen;
    for (const tinyxml2::XMLElement* element = root->FirstChildElement(); element != nullptr;
         element = element->NextSiblingElement()) {
        if (std::string(element->Name()) != "exit") {
            Warn(Where(path, element) + ": unknown element <" + element->Name() + "> (skipped)");
            continue;
        }
        const std::string where = Where(path, element);
        ExitDef def;
        if (!xml::RequiredString(element, "id", def.id, error, where))
            return false;
        if (!xml::RequiredU16(element, "mapId", def.mapId, error, where))
            return false;
        def.scene = xml::OptionalString(element, "scene", "");
        if (!xml::RequiredI32(element, "x", def.x, error, where))
            return false;
        if (!xml::RequiredI32(element, "y", def.y, error, where))
            return false;
        if (!xml::RequiredI32(element, "z", def.z, error, where))
            return false;
        if (!xml::OptionalU16(element, "radius", 500, def.radius, error, where))
            return false;
        def.nameKey = xml::OptionalString(element, "nameKey", def.id);
        if (!seen.insert(def.id).second) {
            error = where + ": duplicate exit '" + def.id + "'";
            return false;
        }
        _exits.push_back(std::move(def));
    }
    RecordTable("exits", CanonicalTableHash(doc));
    return true;
}

bool ContentStore::LoadLoadoutTickets(const std::string& path, std::string& error)
{
    if (!fs::exists(path)) {
        Warn("loadout_tickets.xml missing (" + path + "), loadout ticket table is empty");
        RecordTable("loadout_tickets", MissingTableHash("loadout_tickets"));
        return true;
    }

    tinyxml2::XMLDocument doc;
    if (!xml::LoadFile(path, doc, error))
        return false;
    const tinyxml2::XMLElement* root = xml::TableRoot(doc, path, "loadoutTickets", error);
    if (root == nullptr)
        return false;

    std::uint32_t version = 0;
    if (!xml::OptionalVersion(root, version, error, path))
        return false;
    if (version != 1)
        Warn(path + ": unsupported loadoutTickets version " + std::to_string(version) +
             " (parsed as v1)");

    // One referenced stack: the item id must exist in items.xml and 1 <= count <= item.stack.
    // Equipment slots follow the same rule (equipment is stack 1 in the game data, so a
    // count of 2 there is a table bug).
    auto resolveItem = [&](const std::string& where, std::uint32_t itemId, std::uint32_t count,
                           const char* section) -> bool {
        const ItemDef* item = FindItemDef(itemId);
        if (item == nullptr) {
            error = where + ": " + section + " references unknown itemId " +
                    std::to_string(itemId);
            return false;
        }
        if (count == 0 || count > item->stack) {
            error = where + ": " + section + " count " + std::to_string(count) + " for item " +
                    std::to_string(itemId) + " outside 1.." + std::to_string(item->stack);
            return false;
        }
        return true;
    };

    std::unordered_set<std::string> seenIds;
    std::unordered_set<std::string> seenNameKeys;
    for (const tinyxml2::XMLElement* element = root->FirstChildElement(); element != nullptr;
         element = element->NextSiblingElement()) {
        if (std::string(element->Name()) != "ticket") {
            Warn(Where(path, element) + ": unknown element <" + element->Name() + "> (skipped)");
            continue;
        }

        const std::string where = Where(path, element);
        LoadoutTicketDef ticket;
        if (!xml::RequiredString(element, "id", ticket.id, error, where))
            return false;
        if (!xml::RequiredString(element, "nameKey", ticket.nameKey, error, where))
            return false;
        if (!seenIds.insert(ticket.id).second) {
            error = where + ": duplicate loadout ticket id '" + ticket.id + "'";
            return false;
        }
        if (!seenNameKeys.insert(ticket.nameKey).second) {
            error = where + ": duplicate loadout ticket nameKey '" + ticket.nameKey + "'";
            return false;
        }

        std::unordered_set<std::uint32_t> seenSlots;
        for (const tinyxml2::XMLElement* section = element->FirstChildElement();
             section != nullptr; section = section->NextSiblingElement()) {
            const std::string sectionName = section->Name() != nullptr ? section->Name() : "";
            const std::string sectionWhere = Where(path, section);

            if (sectionName == "equipment") {
                for (const tinyxml2::XMLElement* row = section->FirstChildElement(); row != nullptr;
                     row = row->NextSiblingElement()) {
                    if (std::string(row->Name()) != "slot") {
                        Warn(Where(path, row) + ": unknown element <" + row->Name() +
                             "> (skipped)");
                        continue;
                    }
                    const std::string rowWhere = Where(path, row);
                    LoadoutSlotDef entry;
                    std::uint32_t slotIndex = 0;
                    if (!xml::RequiredU32(row, "index", slotIndex, error, rowWhere))
                        return false;
                    // PlayerComponent.loadout is a 10-entry array (0..9).
                    if (slotIndex >= 10) {
                        error = rowWhere + ": equipment slot " + std::to_string(slotIndex) +
                                " outside 0..9 in loadout ticket '" + ticket.id + "'";
                        return false;
                    }
                    entry.slot = static_cast<std::uint8_t>(slotIndex);
                    if (!xml::RequiredU32(row, "itemId", entry.itemId, error, rowWhere))
                        return false;
                    if (!xml::OptionalU32(row, "count", 1, entry.count, error, rowWhere))
                        return false;
                    if (!seenSlots.insert(slotIndex).second) {
                        error = rowWhere + ": duplicate equipment slot " +
                                std::to_string(slotIndex) + " in loadout ticket '" + ticket.id +
                                "'";
                        return false;
                    }
                    if (!resolveItem(rowWhere, entry.itemId, entry.count, "equipment"))
                        return false;
                    // Optional attachments: <mod slot="Scope" itemId="1234"/> children use the
                    // native Item.Slots key of the parent item.
                    std::unordered_set<std::string> seenModSlots;
                    for (const tinyxml2::XMLElement* modRow = row->FirstChildElement();
                         modRow != nullptr; modRow = modRow->NextSiblingElement()) {
                        if (std::string(modRow->Name()) != "mod") {
                            Warn(Where(path, modRow) + ": unknown element <" + modRow->Name() +
                                 "> (skipped)");
                            continue;
                        }
                        const std::string modWhere = Where(path, modRow);
                        LoadoutModDef mod;
                        if (!xml::RequiredString(modRow, "slot", mod.slotKey, error, modWhere))
                            return false;
                        if (mod.slotKey.empty() || mod.slotKey.size() > msg::kMaxModSlotKeyBytes) {
                            error = modWhere + ": attachment slot key must be 1.." +
                                    std::to_string(msg::kMaxModSlotKeyBytes) + " bytes";
                            return false;
                        }
                        if (!xml::RequiredU32(modRow, "itemId", mod.itemId, error, modWhere))
                            return false;
                        if (!seenModSlots.insert(mod.slotKey).second) {
                            error = modWhere + ": duplicate attachment slot '" + mod.slotKey +
                                    "' on equipment slot " + std::to_string(slotIndex);
                            return false;
                        }
                        if (entry.mods.size() >= msg::kMaxItemMods) {
                            error = modWhere + ": more than " + std::to_string(msg::kMaxItemMods) +
                                    " attachments on equipment slot " +
                                    std::to_string(slotIndex);
                            return false;
                        }
                        if (FindItemDef(mod.itemId) == nullptr) {
                            error = modWhere + ": attachment references unknown itemId " +
                                    std::to_string(mod.itemId);
                            return false;
                        }
                        entry.mods.push_back(std::move(mod));
                    }
                    ticket.equipment.push_back(entry);
                }
                continue;
            }

            if (sectionName == "backpack" || sectionName == "petBag") {
                std::vector<LoadoutItemDef>& bag =
                    sectionName == "backpack" ? ticket.backpack : ticket.petBag;
                for (const tinyxml2::XMLElement* row = section->FirstChildElement(); row != nullptr;
                     row = row->NextSiblingElement()) {
                    if (std::string(row->Name()) != "item") {
                        Warn(Where(path, row) + ": unknown element <" + row->Name() +
                             "> (skipped)");
                        continue;
                    }
                    const std::string rowWhere = Where(path, row);
                    LoadoutItemDef entry;
                    if (!xml::RequiredU32(row, "itemId", entry.itemId, error, rowWhere))
                        return false;
                    if (!xml::OptionalU32(row, "count", 1, entry.count, error, rowWhere))
                        return false;
                    // Duplicate item ids are allowed in bags (M2 may stack/split later), but
                    // every row still has to be a legal stack.
                    if (!resolveItem(rowWhere, entry.itemId, entry.count, sectionName.c_str()))
                        return false;
                    bag.push_back(entry);
                }
                continue;
            }

            Warn(sectionWhere + ": unknown element <" + sectionName + "> (skipped)");
        }

        _loadoutTickets.push_back(std::move(ticket));
    }

    // T1/M2 requires the three complete kits (light/medium/heavy). A table that defines
    // fewer than three is treated as a configuration error, not a warning.
    if (_loadoutTickets.size() < 3) {
        error = path + ": at least 3 loadout tickets required (found " +
                std::to_string(_loadoutTickets.size()) + ")";
        return false;
    }

    RecordTable("loadout_tickets", CanonicalTableHash(doc));
    return true;
}

bool ContentStore::LoadWorldItemSpawns(const std::string& path, std::string& error)
{
    if (!fs::exists(path)) {
        Warn("world_item_spawns.xml missing (" + path + "), ground item table is empty");
        RecordTable("world_item_spawns", MissingTableHash("world_item_spawns"));
        return true;
    }

    tinyxml2::XMLDocument doc;
    if (!xml::LoadFile(path, doc, error))
        return false;
    const tinyxml2::XMLElement* root = xml::TableRoot(doc, path, "worldItemSpawns", error);
    if (root == nullptr)
        return false;

    std::uint32_t version = 0;
    if (!xml::OptionalVersion(root, version, error, path))
        return false;
    if (version != 1)
        Warn(path + ": unsupported worldItemSpawns version " + std::to_string(version) +
             " (parsed as v1)");

    std::uint16_t rootMapId = 1;
    if (!xml::OptionalU16(root, "mapId", 1, rootMapId, error, path))
        return false;

    std::unordered_set<std::string> seen;
    for (const tinyxml2::XMLElement* element = root->FirstChildElement(); element != nullptr;
         element = element->NextSiblingElement()) {
        if (std::string(element->Name()) != "spawn") {
            Warn(Where(path, element) + ": unknown element <" + element->Name() + "> (skipped)");
            continue;
        }

        const std::string where = Where(path, element);
        WorldItemSpawnDef def;
        if (!xml::RequiredString(element, "id", def.id, error, where))
            return false;
        if (!xml::OptionalU16(element, "mapId", rootMapId, def.mapId, error, where))
            return false;
        if (!xml::RequiredString(element, "scene", def.scene, error, where))
            return false;
        if (!xml::RequiredI32(element, "x", def.x, error, where))
            return false;
        if (!xml::RequiredI32(element, "y", def.y, error, where))
            return false;
        if (!xml::RequiredI32(element, "z", def.z, error, where))
            return false;
        if (!xml::OptionalU16(element, "yaw", 0, def.yaw, error, where))
            return false;
        if (!xml::RequiredU32(element, "itemId", def.itemId, error, where))
            return false;
        if (!xml::OptionalU32(element, "count", 1, def.count, error, where))
            return false;

        if (!seen.insert(def.id).second) {
            error = where + ": duplicate world item spawn id '" + def.id + "'";
            return false;
        }
        const ItemDef* item = FindItemDef(def.itemId);
        if (item == nullptr) {
            error = where + ": unknown itemId " + std::to_string(def.itemId);
            return false;
        }
        if (def.count == 0 || def.count > item->stack) {
            error = where + ": count " + std::to_string(def.count) + " for item " +
                    std::to_string(def.itemId) + " outside 1.." +
                    std::to_string(item->stack);
            return false;
        }
        const SceneDef* scene = FindScene(def.scene);
        if (scene == nullptr) {
            error = where + ": unknown scene '" + def.scene + "'";
            return false;
        }
        if (scene->mapId != def.mapId)
            Warn(where + ": world item mapId " + std::to_string(def.mapId) +
                 " differs from scene mapId " + std::to_string(scene->mapId));

        _worldItemSpawns.push_back(std::move(def));
    }

    RecordTable("world_item_spawns", CanonicalTableHash(doc));
    return true;
}

} // namespace coop
