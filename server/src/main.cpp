#include <atomic>
#include <chrono>
#include <cstdlib>
#include <memory>
#include <set>
#include <string>
#include <thread>
#include <unordered_map>

#if defined(_WIN32)
#include <windows.h>
#else
#include <csignal>
#endif

#include "combat/BuffModel.h"
#include "combat/StatModel.h"
#include "content/ContentStore.h"
#include "core/Json.h"
#include "core/Log.h"
#include "gate/Gate.h"
#include "storage/StorageFactory.h"
#include "world/World.h"

namespace {

std::atomic<bool> g_stop{false};

#if defined(_WIN32)
BOOL WINAPI ConsoleHandler(DWORD type)
{
    if (type == CTRL_C_EVENT || type == CTRL_CLOSE_EVENT || type == CTRL_BREAK_EVENT) {
        g_stop = true;
        return TRUE;
    }
    return FALSE;
}
#else
extern "C" void PosixSignalHandler(int)
{
    g_stop = true;
}
#endif

} // namespace

int main(int argc, char** argv)
{
    coop::GateConfig config;
    std::string dataDir = "data";
    std::string storageName = "file";
    std::string spawnTableId = "groundzero_main";
    std::string mapFile = "Level_GroundOne_1";
    // T1: the starting kit selected for this server run. It is only validated/logged here;
    // inventory issuance (and its protocol) lands with T3. An empty value disables selection.
    std::string loadoutTicketId = "admin_light";
    bool loadoutTicketRequested = false;
    std::string allowAccounts;
    std::uint32_t debugGroundItemId = 0;
    // M2-v11: the native client cannot juggle 20+ moving AI in one area; keep a per-player
    // budget of active (streamed-in) AI. 0 disables the cap.
    int aiMaxActive = 0;

    for (int i = 1; i < argc; ++i) {
        const std::string arg = argv[i];
        if (arg == "--port" && i + 1 < argc) {
            config.port = static_cast<std::uint16_t>(std::atoi(argv[++i]));
        } else if (arg == "--tick" && i + 1 < argc) {
            config.tickRate = static_cast<std::uint8_t>(std::atoi(argv[++i]));
        } else if (arg == "--max" && i + 1 < argc) {
            config.maxPlayers = static_cast<std::size_t>(std::atoi(argv[++i]));
        } else if (arg == "--data" && i + 1 < argc) {
            dataDir = argv[++i];
        } else if (arg.rfind("--storage=", 0) == 0) {
            storageName = arg.substr(std::string("--storage=").size());
        } else if (arg == "--spawn-table" && i + 1 < argc) {
            spawnTableId = argv[++i];
        } else if (arg.rfind("--spawn-table=", 0) == 0) {
            spawnTableId = arg.substr(std::string("--spawn-table=").size());
        } else if (arg == "--map-file" && i + 1 < argc) {
            mapFile = argv[++i];
        } else if (arg.rfind("--map-file=", 0) == 0) {
            mapFile = arg.substr(std::string("--map-file=").size());
        } else if (arg == "--loadout-ticket" && i + 1 < argc) {
            loadoutTicketId = argv[++i];
            loadoutTicketRequested = true;
        } else if (arg.rfind("--loadout-ticket=", 0) == 0) {
            loadoutTicketId = arg.substr(std::string("--loadout-ticket=").size());
            loadoutTicketRequested = true;
        } else if (arg == "--allow-accounts" && i + 1 < argc) {
            allowAccounts = argv[++i];
        } else if (arg.rfind("--allow-accounts=", 0) == 0) {
            allowAccounts = arg.substr(std::string("--allow-accounts=").size());
        } else if (arg == "--debug-ground-item" && i + 1 < argc) {
            debugGroundItemId = static_cast<std::uint32_t>(std::strtoul(argv[++i], nullptr, 10));
        } else if (arg.rfind("--debug-ground-item=", 0) == 0) {
            debugGroundItemId = static_cast<std::uint32_t>(
                std::strtoul(arg.substr(std::string("--debug-ground-item=").size()).c_str(),
                             nullptr, 10));
        } else if (arg == "--storage" && i + 1 < argc) {
            storageName = argv[++i];
        } else if (arg == "--ai-max-active" && i + 1 < argc) {
            aiMaxActive = std::atoi(argv[++i]);
        } else if (arg.rfind("--ai-max-active=", 0) == 0) {
            aiMaxActive = std::atoi(arg.substr(std::string("--ai-max-active=").size()).c_str());
        } else if (arg == "--verbose") {
            coop::Log::SetVerbose(true);
        } else {
            coop::Log::Warn("unknown argument: " + arg);
        }
    }

#if defined(_WIN32)
    SetConsoleCtrlHandler(ConsoleHandler, TRUE);
#else
    std::signal(SIGINT, PosixSignalHandler);
    std::signal(SIGTERM, PosixSignalHandler);
#endif

    if (!allowAccounts.empty()) {
        std::size_t start = 0;
        while (start <= allowAccounts.size()) {
            const std::size_t comma = allowAccounts.find(',', start);
            std::string name = allowAccounts.substr(
                start, comma == std::string::npos ? std::string::npos : comma - start);
            const auto first = name.find_first_not_of(" \t");
            const auto last = name.find_last_not_of(" \t");
            name = first == std::string::npos ? std::string() : name.substr(first, last - first + 1);
            if (!name.empty())
                config.allowedAccounts.push_back(name);
            if (comma == std::string::npos)
                break;
            start = comma + 1;
        }
        std::string joined;
        for (const auto& name : config.allowedAccounts)
            joined += (joined.empty() ? "" : ",") + name;
        coop::Log::Info("account allowlist: " + joined);
    } else {
        coop::Log::Warn("account allowlist is empty: anyone who knows this address can join");
    }

    coop::Log::Info("DuckovCoop server (C++ skeleton) starting, protocol v" +
                    std::to_string(coop::wire::kProtocolVersion) + " client " +
                    std::string(coop::wire::kClientVersion));

    coop::StorageKind storageKind;
    std::string storageError;
    if (!coop::ParseStorageKind(storageName, storageKind, storageError)) {
        coop::Log::Error(storageError);
        return 1;
    }

    // content tables (ADR-014): fail-fast on parse/duplicate errors, warnings for missing
    // tables or filtered references.
    std::set<std::string> knownLootTables;
    std::unordered_map<std::string, coop::LootTable> lootTables;
    {
        std::string lootError;
        if (coop::LoadAllLootTables(dataDir + "/config/loot_tables.json", lootTables, lootError)) {
            for (const auto& [id, table] : lootTables)
                knownLootTables.insert(id);
        } else {
            coop::Log::Warn("loot tables not loaded (" + lootError + "), using defaults");
        }
    }

    // T7: optional per-monster AI corpse loot tables. Missing/empty is valid: AI corpses
    // stay empty until content is configured.
    std::unordered_map<std::uint32_t, std::string> aiLootTables;
    {
        std::string aiLootError;
        if (coop::LoadAiLootTables(dataDir + "/config/ai_loot_tables.json", aiLootTables,
                                   aiLootError)) {
            coop::Log::Info("ai loot tables loaded: " + std::to_string(aiLootTables.size()));
        } else {
            coop::Log::Warn("ai loot tables not loaded (" + aiLootError + ")");
        }
    }

    coop::ContentStore content;
    content.SetMapFile(mapFile);
    std::string contentError;
    if (!content.Load(dataDir + "/config", knownLootTables, contentError)) {
        coop::Log::Error("content load failed: " + contentError);
        return 1;
    }
    for (const auto& warning : content.Warnings())
        coop::Log::Warn("content: " + warning);

    // v2 loot config: the loot table owns the item count, the container type's native
    // capacity is the hard cap. Surface the types where the cap truncates the table.
    {
        int capped = 0;
        std::string sample;
        for (const auto& type : content.ContainerTypes()) {
            if (type.lootTableId.empty())
                continue;
            const auto tableIt = lootTables.find(type.lootTableId);
            if (tableIt == lootTables.end())
                continue;
            if (tableIt->second.MaxRolls() > static_cast<int>(type.capacity)) {
                ++capped;
                if (sample.size() < 160)
                    sample += (sample.empty() ? "" : ",") + type.id;
            }
        }
        if (capped > 0) {
            coop::Log::Info("loot config: " + std::to_string(capped) +
                            " container type(s) have native capacity below the table's max rolls "
                            "(extra items are dropped): " + sample);
        }
    }

    coop::combat::StatModel statModel;
    std::string statError;
    if (!statModel.Load(dataDir + "/config", statError)) {
        coop::Log::Error("stat model load failed: " + statError);
        return 1;
    }

    coop::combat::BuffModel buffModel;
    if (!buffModel.Load(dataDir + "/config", statError)) {
        coop::Log::Error("buff model load failed: " + statError);
        return 1;
    }

    coop::Log::Info("content tables: items=" + std::to_string(content.Stats().items) +
                    " scenes=" + std::to_string(content.Stats().scenes) +
                    " monsters=" + std::to_string(content.Stats().monsters) +
                    " spawnTables=" + std::to_string(content.Stats().spawnTables) +
                    " spawners=" + std::to_string(content.Stats().spawners) +
                    " points=" + std::to_string(content.Stats().spawnPoints) +
                    " manual=" + std::to_string(content.Stats().manualSpawns) +
                    " spawns=" + std::to_string(content.Stats().spawns) +
                    " weapons=" + std::to_string(content.Stats().weapons) +
                    " ammo=" + std::to_string(content.Stats().ammo) +
                    " melee=" + std::to_string(content.Stats().melee) +
                    " armors=" + std::to_string(content.Stats().armors) +
                    " containerTypes=" + std::to_string(content.Stats().containerTypes) +
                    " containerSpawns=" + std::to_string(content.Stats().containerSpawns) +
                    " playerSpawns=" + std::to_string(content.Stats().playerSpawns) +
                    " exits=" + std::to_string(content.Stats().exits) +
                    " loadoutTickets=" + std::to_string(content.Stats().loadoutTickets) +
                    " worldItemSpawns=" + std::to_string(content.Stats().worldItemSpawns));
    coop::Log::Info("content hash: " + content.ContentHash());
    coop::Log::Info("monster catalog hash: " + content.MonsterCatalogHash());
    for (const char* table : {"items", "scenes", "monsters", "spawn_tables", "weapons", "ammo",
                              "melee", "armors", "containers", "container_spawns",
                              "player_spawns", "exits", "loadout_tickets", "world_item_spawns"})
        coop::Log::Info(std::string("content hash ") + table + ": " + content.TableHash(table));

    config.enforceContentHash = true;
    config.contentHash = content.MonsterCatalogHashBytes();

    coop::ItemRegistry registry;
    std::string configError;
    if (content.HasItems()) {
        for (const auto& def : content.Items())
            registry.Add(def);
        coop::Log::Info("item registry loaded from items.xml: " +
                        std::to_string(registry.Count()) + " items");
    } else if (registry.LoadFromFile(dataDir + "/config/items.json", configError)) {
        coop::Log::Warn("items.xml missing, using items.json fallback: " +
                        std::to_string(registry.Count()) + " items");
    } else {
        coop::Log::Warn("item registry not loaded (" + configError + "), using defaults");
    }

    // T1/T3: validate the selected starting kit and hand it to the world. The world seeds
    // every player's equipment/backpack/petBag from this ticket (T3A); an explicitly
    // requested ticket must exist (fail-fast), the built-in default only warns so old
    // --data directories without the table still start (legacy stash flow).
    // A comma-separated list becomes a pool: every player draws one kit at random per raid.
    std::vector<const coop::LoadoutTicketDef*> loadoutTickets;
    {
        std::size_t start = 0;
        while (start <= loadoutTicketId.size()) {
            const std::size_t comma = loadoutTicketId.find(',', start);
            std::string id = loadoutTicketId.substr(
                start, comma == std::string::npos ? std::string::npos : comma - start);
            const auto first = id.find_first_not_of(" \t");
            const auto last = id.find_last_not_of(" \t");
            id = first == std::string::npos ? std::string() : id.substr(first, last - first + 1);

            if (!id.empty()) {
                const coop::LoadoutTicketDef* ticket = content.FindLoadoutTicket(id);
                if (ticket == nullptr) {
                    const std::string message = "loadout ticket not found: '" + id + "'";
                    if (loadoutTicketRequested) {
                        coop::Log::Error(message);
                        return 1;
                    }
                    coop::Log::Warn(message + " (skipped), continuing without that kit");
                } else {
                    loadoutTickets.push_back(ticket);
                    coop::Log::Info("loadout ticket '" + ticket->id +
                                    "' loaded: equipment=" +
                                    std::to_string(ticket->equipment.size()) + " backpack=" +
                                    std::to_string(ticket->backpack.size()) + " petBag=" +
                                    std::to_string(ticket->petBag.size()));
                }
            }

            if (comma == std::string::npos)
                break;
            start = comma + 1;
        }
    }
    const coop::LoadoutTicketDef* loadoutTicket =
        loadoutTickets.empty() ? nullptr : loadoutTickets.front();
    if (loadoutTickets.size() > 1) {
        coop::Log::Info("loadout pool: " + std::to_string(loadoutTickets.size()) +
                        " kits, one drawn per player per raid");
    }

    coop::LootTable lootTable;
    const auto worldBoxIt = lootTables.find("world_box");
    const bool hasLootTable = worldBoxIt != lootTables.end();
    if (hasLootTable) {
        lootTable = worldBoxIt->second;
        const std::string rollRange =
            lootTable.MinRolls() == lootTable.MaxRolls()
                ? std::to_string(lootTable.MaxRolls())
                : std::to_string(lootTable.MinRolls()) + ".." + std::to_string(lootTable.MaxRolls());
        coop::Log::Info("loot tables loaded: " + std::to_string(lootTables.size()) +
                        " (world_box entries=" + std::to_string(lootTable.entries.size()) +
                        " rolls=" + rollRange + ")");
    } else {
        coop::Log::Warn("loot table 'world_box' missing, using defaults");
    }

    auto storage = coop::CreateStorage(storageKind, dataDir, storageError);
    if (storage == nullptr) {
        coop::Log::Error("storage init failed: " + storageError);
        return 1;
    }
    coop::Log::Info(std::string("storage backend: ") + coop::StorageKindName(storageKind));

    coop::World world(registry.Count() > 0 ? &registry : nullptr, hasLootTable ? &lootTable : nullptr,
                      12345, storage.get());

    const coop::SpawnTableDef* spawnTable = content.FindSpawnTable(spawnTableId);
    if (spawnTable == nullptr) {
        coop::Log::Error("spawn table not found: " + spawnTableId);
        return 1;
    }
    world.SetContentStore(&content);
    world.SetLootTables(&lootTables);
    world.SetAiLootTables(&aiLootTables);
    world.SetStatModel(&statModel);
    world.SetBuffModel(&buffModel);
    world.SetSpawnTableId(spawnTableId);
    world.SetLoadoutTickets(loadoutTickets);
    world.SetDebugGroundItem(debugGroundItemId);
    world.SetAiMaxActive(aiMaxActive);
    coop::Log::Info("ai streaming cap: " + std::to_string(aiMaxActive) +
                    " active per player (0 = unlimited, default)");
    if (debugGroundItemId != 0)
        coop::Log::Info("debug ground item enabled: item=" + std::to_string(debugGroundItemId));
    coop::Log::Info("spawn table '" + spawnTableId + "' loaded: " +
                    std::to_string(spawnTable->spawners.size()) + " spawners, " +
                    std::to_string(spawnTable->manualSpawns.size()) + " manual");

    coop::Json aiJson;
    std::vector<coop::AiSpawnDef> aiSpawns;
    if (coop::LoadJsonFile(dataDir + "/config/ai_spawns.json", aiJson, configError)) {
        if (const auto* spawns = aiJson.Find("spawns"); spawns != nullptr && spawns->IsArray()) {
            for (const auto& entry : spawns->items) {
                coop::AiSpawnDef def;
                if (const auto* value = entry.Find("aiId")) def.aiId = static_cast<std::uint32_t>(value->AsNumber());
                if (const auto* value = entry.Find("dx")) def.dx = static_cast<std::int32_t>(value->AsNumber());
                if (const auto* value = entry.Find("dz")) def.dz = static_cast<std::int32_t>(value->AsNumber());
                if (const auto* value = entry.Find("yaw")) def.yaw = static_cast<std::uint16_t>(value->AsNumber());
                if (const auto* value = entry.Find("hp")) def.hp = static_cast<std::int32_t>(value->AsNumber());
                if (def.aiId != 0)
                    aiSpawns.push_back(def);
            }
        }
        coop::Log::Info("ai spawns loaded: " + std::to_string(aiSpawns.size()));
    } else {
        coop::Log::Warn("ai spawns not loaded (" + configError + ")");
    }
    world.SetAiSpawns(std::move(aiSpawns));

    coop::Gate gate(world, config);

    std::string error;
    if (!gate.Start(error)) {
        coop::Log::Error("failed to start gate: " + error);
        return 1;
    }
    const auto interval = std::chrono::microseconds(1000000 / config.tickRate);
    auto nextTick = std::chrono::steady_clock::now();

    std::int32_t tick = 0;
    while (!g_stop.load()) {
        ++tick;
        gate.Tick(tick);

        if (tick % (config.tickRate * 5) == 0) {
            coop::Log::Info("tick=" + std::to_string(tick) +
                            " sessions=" + std::to_string(gate.SessionCount()) +
                            " joined=" + std::to_string(gate.JoinedCount()) +
                            " players=" + std::to_string(world.PlayerCount()));
        }

        nextTick += interval;
        std::this_thread::sleep_until(nextTick);
    }

    coop::Log::Info("shutting down");
    gate.Stop();
    return 0;
}
