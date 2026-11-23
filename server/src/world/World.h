#pragma once

#include <array>
#include <cstdint>
#include <map>
#include <random>
#include <string>
#include <unordered_map>
#include <unordered_set>
#include <vector>

#include "core/Ecs.h"
#include "combat/BuffModel.h"
#include "combat/StatModel.h"
#include "content/ContentTypes.h"
#include "content/IContentStore.h"
#include "protocol/Messages.h"
#include "world/Components.h"
#include "world/Events.h"
#include "world/ItemLedger.h"
#include "world/ItemRegistry.h"
#include "storage/IStorage.h"

namespace coop {

struct AiSpawnDef {
    std::uint32_t aiId = 0;
    std::int32_t dx = 0; // meters relative to the spawn anchor
    std::int32_t dz = 0;
    std::uint16_t yaw = 0;
    std::int32_t hp = 100;
};

/// A message the world wants the gate to deliver. playerId == 0 means broadcast.
struct Outbound {
    Op op = Op::SPlayerEvent;
    std::uint16_t playerId = 0;
    std::vector<std::uint8_t> payload;
    std::uint8_t channel = 1; // 0 unreliable, 1 reliable
};

/// Authoritative world state and the fixed 8-phase tick pipeline.
///
///   1 CommandDrain  2 InputApply  3 AiSync  4 Combat
///   5 Interaction   6 RaidPhase   7 EventDispatch  8 Snapshot
///
/// Systems are plain functions for now; the phase order is enforced by Tick().
class World {
    /// Authoritative ground item record (T3B). The ledger item stays alive with no slot
    /// location while the world item exists, so its uid/count survive pickup/drop.
    struct WorldItemRecord {
        std::uint32_t worldItemId = 0;
        ItemUid itemUid = 0;
        std::uint32_t itemId = 0;
        std::uint32_t count = 0;
        std::string scene;
        std::int32_t x = 0;
        std::int32_t y = 0;
        std::int32_t z = 0;
        std::uint16_t yaw = 0;
        std::uint8_t source = 0;
    };

public:
    static constexpr std::int64_t kMaxStepCm = 2500;
    static constexpr std::int32_t kMaxHitRangeCm = 4000;
    static constexpr std::int32_t kMaxHitDamage = 50;
    /// Contract 2.16.0: a client may report the damage it already took locally (explosion,
    /// fall, environment). Self-damage can only hurt the reporter, so it gets a wider bound
    /// than a hit against someone else: one report may not exceed a full health bar.
    static constexpr std::int32_t kMaxSelfReportDamage = 100;
    static constexpr std::int32_t kHitCooldownTicks = 2;
    /// Contract 2.16.0: every player has the same max HP. Perk trees, equipment and the
    /// client-reported CPlayerStats.maxHealth are presentation only; the server owns the
    /// value so both sides render the same health-bar percentage.
    static constexpr std::int32_t kPlayerMaxHp = 100;
    /// Contract 2.16.0: upper bound for the HP one CItemConsume may restore. Anything above
    /// is a bogus report and is ignored (the consume itself still settles normally).
    static constexpr std::uint32_t kMaxHealPerUse = 500;
    static constexpr std::uint32_t kWorldLootContainer = 1;
    static constexpr std::uint32_t kInventoryBase = 1000;
    static constexpr std::uint16_t kInventoryCapacity = 20;
    static constexpr std::uint16_t kBackpackCapacity = kInventoryCapacity;
    static constexpr std::uint32_t kEquipmentBase = 2000;
    static constexpr std::uint16_t kEquipmentCapacity = 10;
    static constexpr std::uint32_t kPetBagBase = 3000;
    static constexpr std::uint16_t kPetBagCapacity = 6;
    /// D6/T7 corpse containers: one per player per death (stride 16 within the player range).
    static constexpr std::uint32_t kCorpseContainerBase = 400000;
    static constexpr std::uint32_t kCorpsePlayerStride = 16;
    /// AI corpses use a separate monotonic id range (500000+) because aiIds are reused per raid.
    static constexpr std::uint32_t kAiCorpseContainerBase = 500000;
    static constexpr std::uint16_t kCorpseMinCapacity = 6;
    /// D9/B: dead players stay in the raid and revive after this delay (20 Hz ticks).
    static constexpr std::int32_t kRespawnDelayTicks = 100;
    static std::uint32_t CorpseContainerId(std::uint16_t playerId)
    {
        return kCorpseContainerBase + playerId * kCorpsePlayerStride;
    }
    static std::uint32_t CorpseContainerId(std::uint16_t playerId, std::uint16_t sequence)
    {
        return kCorpseContainerBase + playerId * kCorpsePlayerStride +
               (sequence % kCorpsePlayerStride);
    }
    /// Equipment slot 6 holds the backpack item (CLoadout/v5 slot semantics).
    static constexpr std::uint16_t kBackpackEquipSlot = 6;
    /// v9: ticket-seeded inventories start at revision 1.
    static constexpr std::uint32_t kInventorySeedRevision = 1;
    static constexpr std::int32_t kRaidDurationTicks = 20 * 60 * 15; // 15 minutes at 20 Hz

    World(const ItemRegistry* registry = nullptr, const LootTable* lootTable = nullptr,
          std::uint32_t seed = 12345, IStorage* storage = nullptr);

    Entity AddPlayer(std::uint16_t playerId, std::uint64_t endpointKey, std::uint64_t identity = 0);
    void RemovePlayer(Entity entity);
    bool IsAlive(Entity entity) const;
    std::int32_t HealthOf(Entity entity) const;
    std::int32_t MaxHealthOf(Entity entity) const;

    /// Phase 1: commands are queued by the gate and applied on the next tick.
    void SubmitInput(Entity entity, const msg::Input& input);
    void SubmitDamage(Entity target, std::int32_t amount);
    void SubmitHit(Entity source, std::uint16_t playerId, const msg::HitReport& report);
    void SubmitHitEvent(std::uint16_t reporterPlayerId, const msg::HitEvent& hit);
    // M2-v10 scene objects + explosion facts.
    void SubmitSceneObjectState(std::uint16_t fromPlayerId, const msg::SceneObjectState& state);
    void SubmitExplosion(std::uint16_t fromPlayerId, const msg::ExplosionEvent& explosion);
    // M2-v12 bullet impact presentation relay.
    void SubmitImpactFx(std::uint16_t fromPlayerId, const msg::ImpactFx& impact);
    void SubmitAiAnim(std::uint16_t fromPlayerId, const msg::AiAnim& anim);
    void SubmitAiAim(std::uint16_t fromPlayerId, const msg::AiAim& aim);
    void SubmitAiStateSample(std::uint16_t fromPlayerId, const msg::AiStateSample& sample);
    void SubmitAiLoadoutV7(std::uint16_t fromPlayerId, const msg::AiLoadoutV7& loadout);
    void SubmitAiShot(std::uint16_t fromPlayerId, const msg::AiShot& shot);
    void SubmitPlayerStateSample(std::uint16_t fromPlayerId, const msg::PlayerStateSample& sample);
    void SubmitPlayerLoadoutV8(std::uint16_t fromPlayerId, const msg::PlayerLoadoutV8& loadout);
    void SubmitPlayerShot(std::uint16_t fromPlayerId, const msg::PlayerShot& shot);
    void SubmitPlayerFaceV8(std::uint16_t fromPlayerId, const msg::PlayerFaceV8& face);
    void SubmitShootFact(std::uint16_t fromPlayerId, const msg::ShootFact& fact);
    void SubmitUseConsumable(std::uint16_t fromPlayerId, const msg::UseConsumable& use);
    void SubmitAiState(std::uint16_t fromPlayerId, const msg::AiState& state);
    void SubmitLoot(Entity player, std::uint16_t playerId, const msg::LootRequest& request);
    void SubmitExtract(Entity player, std::uint16_t playerId, const msg::ExtractRequest& request);
    void SubmitContainerView(std::uint16_t playerId, const msg::ContainerViewRequest& request);

    // ---- v9 item contract (T3A) ------------------------------------------------
    /// Starting kit used to seed every player's three inventories. nullptr keeps the legacy
    /// stash-restore behavior (old tests / data dirs without a ticket).
    void SetLoadoutTicket(const LoadoutTicketDef* ticket)
    {
        _loadoutTickets.clear();
        _loadoutTicket = ticket;
        if (ticket != nullptr)
            _loadoutTickets.push_back(ticket);
    }
    /// Several kits share the pool; every player is assigned one at random per raid.
    void SetLoadoutTickets(std::vector<const LoadoutTicketDef*> tickets)
    {
        _loadoutTickets = std::move(tickets);
        _loadoutTicket = _loadoutTickets.empty() ? nullptr : _loadoutTickets.front();
        _playerTicketIndex.clear();
        _raidTicketOrder.clear();
        _ticketDealCursor = 0;
    }
    const LoadoutTicketDef* LoadoutTicket() const { return _loadoutTicket; }
    /// The kit this player draws from the pool (stable within one raid).
    const LoadoutTicketDef* TicketForPlayer(std::uint16_t playerId);

    /// Test-only: seed one extra WorldItem near the first authored player spawn.
    /// 0 disables it; this is not part of the production world-item table.
    void SetDebugGroundItem(std::uint32_t itemId) { _debugGroundItemId = itemId; }
    std::uint32_t DebugGroundItem() const { return _debugGroundItemId; }

    /// SInitialInventory for one player (join / late join / reconnect, sent by the gate).
    void SendInitialInventory(std::uint16_t playerId);
    void SubmitItemTransfer(std::uint16_t playerId, const msg::ItemTransfer& transfer);
    /// Contract 2.15.0: turn one attachment of a server-owned item into a backpack item.
    void SubmitItemModDetach(std::uint16_t playerId, const msg::ItemModDetach& request);
    /// Contract 2.17.0: plug one backpack item into an attachment slot of a server-owned item.
    void SubmitItemModAttach(std::uint16_t playerId, const msg::ItemModAttach& request);
    void SubmitItemArrange(std::uint16_t playerId, const msg::ItemArrange& arrange);
    void SubmitItemConsume(std::uint16_t playerId, const msg::ItemConsume& consume);
    void SubmitItemReload(std::uint16_t playerId, const msg::ItemReload& reload);
    void SubmitItemWeaponFire(std::uint16_t playerId, const msg::ItemWeaponFire& fire);
    void SubmitItemDurability(std::uint16_t playerId, const msg::ItemDurability& durability);
    void SubmitBackpackCapacity(std::uint16_t playerId, const msg::BackpackCapacity& capacity);
    void SubmitInventorySync(std::uint16_t playerId, const msg::InventorySyncRequest& request);

    /// Stable inventory container ids (disjoint from map containers 100000+ / world items).
    static std::uint32_t EquipmentId(std::uint16_t playerId) { return kEquipmentBase + playerId; }
    static std::uint32_t PetBagId(std::uint16_t playerId) { return kPetBagBase + playerId; }

    // ---- v9 item contract (T3B): containers + ground items ---------------------
    static constexpr std::uint32_t kWorldItemBase = 1000000;
    static constexpr std::int32_t kWorldItemPickupRangeCm = 500;

    /// Server-side ground item (T3B test seed / future loose-item tables). The ledger item
    /// keeps its uid; the world record stores position/source.
    std::uint32_t SpawnWorldItem(std::uint32_t itemId, std::uint32_t count,
                                 const std::string& scene, std::int32_t x, std::int32_t y,
                                 std::int32_t z, std::uint16_t yaw, std::uint8_t source = 0);
    std::size_t WorldItemCount() const { return _worldItems.size(); }
    bool FindWorldItem(std::uint32_t worldItemId, msg::WorldItemInfo& out) const;
    /// Sorted map-container ids of the current raid (T3B tests/diagnostics).
    std::vector<std::uint32_t> MapContainerIds() const;
    /// Chunked SWorldItemSnapshot (<= 8 entries per chunk, 0-based chunkIndex).
    void SendWorldItemSnapshot(std::uint16_t playerId);
    /// Replay current corpse containers to one joining player.
    void SendCorpseSnapshot(std::uint16_t playerId);
    /// P1 phase 3: emit SItemDetail for non-default items in one container.
    void SendItemDetails(std::uint16_t playerId, std::uint32_t containerId);
    void SubmitContainerOpen(std::uint16_t playerId, const msg::ContainerOpen& request);
    void SubmitWorldItemPickup(std::uint16_t playerId, const msg::WorldItemPickup& request);
    void SubmitWorldItemDrop(std::uint16_t playerId, const msg::WorldItemDrop& request);

    // ---- T3.5: map-container access guard --------------------------------------
    /// True when the player may open/take/put from this map container:
    /// in raid + authored container + same scene + 2D distance <= 500 cm.
    /// The legacy kWorldLootContainer (1) is allowed in-raid without scene/range checks.
    bool CanUseMapContainer(std::uint16_t playerId, std::uint32_t containerId) const;
    /// D6/T7: a corpse container is usable by any in-raid player in the same scene within 5 m.
    bool CanUseCorpseContainer(std::uint16_t playerId, std::uint32_t containerId) const;
    /// Current raid corpse container ids (tests/diagnostics).
    std::vector<std::uint32_t> CorpseContainerIds() const;
    /// Authored spawn of a map container (tests/diagnostics). False when unknown.
    bool MapContainerSpawn(std::uint32_t containerId, std::int32_t& x, std::int32_t& y,
                           std::int32_t& z, std::string& scene) const;

    /// Server-authoritative teleport (SSpawnAssign): move the entity immediately and
    /// let the next input near the target through; in-flight inputs from the old
    /// position are ignored instead of rewinding the Position.
    void SetPlayerTeleportTarget(std::uint16_t playerId, std::int32_t x, std::int32_t y,
                                 std::int32_t z);

    /// Called on scene change / spawn assignment: the next input may jump far.
    void ResetInputBaselines();

    /// A1: a new session took the entity over (top-account / resume). The client restarts
    /// its input sequence at 1, so the stale-sequence guard must be dropped for this player.
    void ResetInputBaseline(std::uint16_t playerId);

    /// A new player joined a closed raid: reopen it.
    void ReopenRaid();

    /// Tear the current raid down before starting another one (pre-start cleanup).
    void EndRaid();

    /// Player gave up / died and left the raid: drop their AI and check for settle.
    void LeaveRaid(std::uint16_t playerId);

    /// Session expired / disconnected without extracting: lost in the raid.
    void OnPlayerDisconnected(std::uint16_t playerId);

    /// Trusted spawn report: teleport the server-side entity and accept the next input.
    /// The report must carry the current raid epoch; returns false when it is stale or the
    /// player already left the raid (dead / extracted / disconnected).
    bool SetPlayerPosition(Entity entity, std::int32_t x, std::int32_t y, std::int32_t z,
                           const std::string& scene, std::uint32_t raidEpoch);

    bool GetPlayerPosition(std::uint16_t playerId, std::int32_t& x, std::int32_t& y, std::int32_t& z) const;

    /// True while the player is an active participant of the running raid.
    bool IsInRaid(std::uint16_t playerId) const;
    std::string PlayerScene(std::uint16_t playerId) const;

    /// v1 spawn tables: pick an authored player entry point for a scene/seat.
    /// Returns false when the map has no player_spawns entry (caller falls back
    /// to the legacy first-report anchor).
    bool PickPlayerSpawn(const std::string& scene, std::uint16_t seat,
                         std::int32_t& x, std::int32_t& y, std::int32_t& z,
                         std::uint16_t& yaw) const;
    /// v1 exits: true when the player's current position is inside an exit radius.
    /// When the map has no exits, returns true (legacy any-point extraction).
    bool IsPlayerAtExit(std::uint16_t playerId) const;

    /// v1 spawn protection: ignore incoming damage for `ticks` server ticks.
    void ProtectPlayer(std::uint16_t playerId, std::int32_t ticks);

    /// B2: server-validated equipment snapshot (slot -> itemId, 0 = empty).
    bool SetLoadout(std::uint16_t playerId, const std::vector<msg::LoadoutEntry>& entries);
    bool GetLoadout(std::uint16_t playerId, std::vector<msg::LoadoutEntry>& out) const;

    /// B4: stash contents for the client mirror + a version that changes on settle.
    std::vector<StoredItem> GetStoredItems(std::uint16_t playerId) const;
    std::uint64_t StashVersion() const { return _stashVersion; }

    /// Incremented by EndRaid(); spawn reports from another raid are rejected.
    std::uint32_t RaidEpoch() const { return _raidEpoch; }

    /// AI is spawned by the server (spawn table); the simulator only reports transform.
    /// `state` is 0 (dormant, M2-v11 AOI) or 1 (active); manual spawns default to active.
    Entity SpawnAi(std::uint32_t aiId, std::uint16_t ownerPlayerId, std::int32_t x, std::int32_t y, std::int32_t z,
                   std::uint32_t monsterId = 0, std::uint8_t state = 1);
    Entity FindAi(std::uint32_t aiId) const;

    void SetAiSpawns(std::vector<AiSpawnDef> spawns);
    void SetContentStore(const IContentStore* content);
    /// M2-v11: disable distance streaming; BeginRaid then spawns + broadcasts everything
    /// (legacy behavior, used by tests and for debugging).
    void SetAiStreamingEnabled(bool enabled) { _aiStreamingEnabled = enabled; }
    /// M2-v11: max simultaneously active AI per player (0 = unlimited). The native client
    /// cannot render/update 20+ moving AI in one area, so the server keeps a budget.
    void SetAiMaxActive(int value) { _aiMaxActivePerPlayer = value; }
    void SetLootTables(const std::unordered_map<std::string, LootTable>* tables) { _lootTables = tables; }
    void SetAiLootTables(const std::unordered_map<std::uint32_t, std::string>* tables)
    {
        _aiLootTables = tables;
    }
    void SetStatModel(const combat::StatModel* stats);
    void SetBuffModel(const combat::BuffModel* buffs);
    void SetWeather(std::uint8_t weather) { _weather = weather; }
    void SetSpawnTableId(std::string id);
    const std::string& SpawnTableId() const { return _spawnTableId; }
    /// D5 caches: player stat snapshot and host-reported AI equipment ids.
    void SetPlayerStats(std::uint16_t playerId, const msg::PlayerStats& stats);
    bool GetPlayerStats(std::uint16_t playerId, msg::PlayerStats& out) const;
    bool SetAiLoadout(std::uint16_t reporterPlayerId, const msg::AiLoadout& loadout);
    bool GetAiLoadout(std::uint32_t aiId, msg::AiLoadout& out) const;
    int PlayerBuffLayers(std::uint16_t playerId, std::uint32_t buffId) const;
    void ResetAiSpawn();
    void BeginRaid(std::int32_t anchorX, std::int32_t anchorY, std::int32_t anchorZ, std::uint16_t ownerPlayerId = 0);
    /// Late join/reconnect: replay every alive AI to one player.
    void ReplayActiveAi(std::uint16_t playerId);
    void ReplayPlayerMirror(std::uint16_t joiningPlayerId);
    /// Send one player's full 10-slot mirror to everyone else (session resume baseline).
    void BroadcastPlayerMirror(std::uint16_t playerId);
    void SendRaidRulesTo(std::uint16_t playerId);

    ItemLedger& Ledger() { return _ledger; }

    void Tick(std::int32_t tick);

    const msg::Snapshot& LastSnapshot() const { return _lastSnapshot; }
    std::vector<WorldEvent> TakeEvents();
    std::vector<Outbound> TakeOutbound();

    std::uint8_t RaidPhase() const { return _phase; }
    /// True from BeginRaid() until EndRaid() (phase may already be settling/closed).
    bool RaidStarted() const { return _raidStarted; }
    static std::uint32_t InventoryId(std::uint16_t playerId) { return kInventoryBase + playerId; }

    Ecs& Entities() { return _ecs; }
    std::size_t PlayerCount() const { return _ecs.All<PlayerComponent>().size(); }

private:
    void ApplyInputs();
    void ApplyAiSync();
    void ApplyAiDirector();
    /// M2-v11: activate AI near players / sleep AI far from them (budgeted per tick).
    void ApplyAiStreaming();
    void ApplyHits();
    void ApplyHitEvents();
    void ApplyCombat();
    void ApplyInteractions();
    void ApplyContainerViews();
    void ApplyItemTransfers();
    void ApplyItemModDetaches();
    void ApplyItemModAttaches();
    void ApplyItemArranges();
    void ApplyItemConsumes();
    /// Contract 2.16.0: settle the HP a consumed healing item restores and broadcast it as
    /// CombatEventV4 type=2 so every client (including the consumer) shows the same value.
    void ApplyConsumeHeal(std::uint16_t playerId, std::uint32_t healAmount);
    void ApplyItemReloads();
    void ApplyItemWeaponFires();
    void ApplyItemDurabilities();
    void ApplyBackpackCapacities();
    void ApplyRespawns();
    /// D9/B: create the corpse and schedule a respawn instead of removing the player from the raid.
    void HandlePlayerDeath(Entity entity, std::uint16_t playerId);
    void ApplyInventorySyncs();
    void ApplyExtract();
    void ApplyRaidPhase();
    void BuildSnapshot(std::int32_t tick);

    void Emit(Op op, std::uint16_t playerId, const std::vector<std::uint8_t>& payload,
              std::uint8_t channel = 1);
    void BroadcastPlayerEvent(std::uint16_t playerId, std::uint8_t type);
    void EmitCombat(std::uint8_t type, std::uint8_t targetKind, std::uint32_t targetId,
                    std::uint16_t playerId, std::int32_t value);
    Entity FindPlayer(std::uint16_t playerId) const;
    bool BuildServerPlayerStats(std::uint16_t playerId, combat::CombatStats& out) const;
    void RefreshPlayerMaxHealth(std::uint16_t playerId);
    void SendBuffsTo(std::uint16_t playerId) const;
    void SendAiLoadout(const msg::AiLoadout& loadout, std::uint16_t playerId);
    void SendAiLoadoutV7(const msg::AiLoadoutV7& loadout, std::uint16_t playerId);
    void SendPlayerBuff(std::uint16_t playerId, std::uint32_t buffId, std::uint8_t layers,
                        std::uint32_t remainingMs, std::uint8_t op) const;
    bool ApplyBuff(std::uint16_t playerId, std::uint32_t buffId, std::uint8_t layers,
                   std::uint16_t sourcePlayerId, std::uint32_t weaponId, int depth);
    bool RemoveBuff(std::uint16_t playerId, std::uint32_t buffId, std::uint8_t layers,
                    bool removeAll, int depth);
    void ExecuteBuffEffect(std::uint16_t playerId, const combat::BuffDef& def,
                           const combat::BuffEffect& effect, std::uint16_t sourcePlayerId,
                           std::uint32_t weaponId, int depth);
    bool BuffFiltersPass(std::uint16_t playerId, const combat::BuffEffect& effect) const;
    void ApplyWeather(combat::CombatStats& stats) const;
    void TriggerPlayerBuffs(std::uint16_t playerId, combat::BuffTriggerKind trigger,
                            std::int32_t damage = 0, int depth = 0, bool attack = false);
    void TickBuffs();
    void ApplyBuffDamage(std::uint16_t playerId, std::uint32_t buffId,
                         const combat::BuffAction& action, std::uint8_t layers);

    /// Removes a player from the raid: clears the scene, drops the input baseline and
    /// removes them from the participant set. `dead` also zeroes health.
    void MarkPlayerLeft(Entity entity, bool dead);

    void PersistStash(std::uint16_t playerId);
    void SettleStash(std::uint16_t playerId, bool extracted);
    std::vector<StoredItem> CollectInventory(std::uint16_t playerId) const;
    std::uint64_t IdentityOf(std::uint16_t playerId) const;

    /// v9 item helpers: seed the three player inventories from the ticket, resolve wire
    /// inventory refs, build/send snapshots and enforce the backpack rule.
    void SeedPlayerInventories(std::uint16_t playerId);
    bool ResolveInventory(std::uint16_t playerId, std::uint8_t kind, std::uint32_t ownerId,
                          std::uint32_t& containerId) const;
    std::uint32_t MaxStackForItem(std::uint32_t itemId) const;
    msg::InventorySnapshot BuildInventorySnapshot(std::uint8_t kind, std::uint32_t ownerId,
                                                  std::uint32_t containerId) const;
    void EmitInventorySnapshot(std::uint16_t playerId, std::uint8_t kind, std::uint32_t ownerId,
                               std::uint32_t containerId, std::uint64_t correlationId);
    bool IsBackpackUnequipBlocked(std::uint16_t playerId, std::uint32_t sourceId,
                                  std::uint16_t sourceSlot) const;

    // T3B container/world-item helpers.
    void ApplyContainerOpens();
    void ApplyWorldItemPickups();
    void ApplyWorldItemDrops();
    bool IsMapContainerId(std::uint32_t containerId) const;
    std::size_t OccupiedCount(std::uint32_t containerId) const;
    msg::WorldItemInfo ToWorldItemInfo(const WorldItemRecord& record) const;
    void BroadcastWorldItemSpawn(const WorldItemRecord& record);
    void BroadcastWorldItemDespawn(std::uint32_t worldItemId);
    struct CorpseRecord;
    void BroadcastCorpseSpawn(const CorpseRecord& record);
    void BroadcastCorpseDespawn(std::uint32_t containerId);
    void SendCorpseSpawn(std::uint16_t playerId, const CorpseRecord& record);

    /// T3.5 access result: distinguishes the wire error code of a rejected access.
    enum class ContainerAccess : std::uint8_t {
        Ok = 0,
        NotInRaid,
        Unknown,
        OutOfRange, // different scene or farther than 500 cm
    };
    ContainerAccess CheckMapContainerAccess(std::uint16_t playerId,
                                            std::uint32_t containerId) const;
    ContainerAccess CheckCorpseAccess(std::uint16_t playerId,
                                      std::uint32_t containerId) const;
    /// Moves the player's equipment + backpack entries into a new corpse container and
    /// records its scene/pose. Pet bag is intentionally kept on the player entity.
    std::uint32_t CreatePlayerCorpse(std::uint16_t playerId, const std::string& scene,
                                     std::int32_t x, std::int32_t y, std::int32_t z,
                                     std::uint16_t yaw);
    /// AI death container: empty until an AI loot table is configured; persists for the raid.
    std::uint32_t CreateAiCorpse(Entity ai, std::uint16_t killerPlayerId);
    /// 0 for Ok, otherwise the ErrorCode value for the rejection (NotInRaid/InvalidArgument/Conflict).
    std::uint8_t MapContainerErrorCode(ContainerAccess access) const;
    /// Scenes of one map share world coordinates (scenes.xml aliases Level_GroundZero_Main
    /// and Level_GroundZero_1 to mapId 1): treat exact scene names or equal mapId as the
    /// same scene group. Falls back to exact string comparison when content is absent.
    bool SameSceneGroup(const std::string& playerScene,
                        const std::string& containerScene) const;

    Ecs _ecs;
    ItemLedger _ledger;
    IStorage* _storage = nullptr;
    const ItemRegistry* _registry = nullptr;
    const LoadoutTicketDef* _loadoutTicket = nullptr;
    std::vector<const LoadoutTicketDef*> _loadoutTickets;
    std::unordered_map<std::uint16_t, std::size_t> _playerTicketIndex;
    /// Per-raid deal order: shuffled once so simultaneous players get different kits.
    std::vector<std::size_t> _raidTicketOrder;
    std::size_t _ticketDealCursor = 0;
    std::uint32_t _debugGroundItemId = 0;
    std::uint64_t _stashVersion = 0;
    std::vector<AiSpawnDef> _aiSpawns;
    const IContentStore* _content = nullptr;
    const combat::StatModel* _stats = nullptr;
    const combat::BuffModel* _buffModel = nullptr;
    struct ActiveBuff {
        std::uint32_t buffId = 0;
        std::uint8_t layers = 1;
        std::int32_t expireTick = -1;
        std::uint16_t sourcePlayerId = 0;
        std::uint32_t weaponId = 0;
        std::vector<double> tickAccumulators;
    };
    std::unordered_map<std::uint16_t, std::unordered_map<std::uint32_t, ActiveBuff>> _playerBuffs;
    std::map<std::pair<std::uint16_t, std::uint64_t>, std::int32_t> _consumableDedup;
    std::unordered_map<std::uint16_t, std::int32_t> _consumableNextTick;
    std::unordered_map<std::uint16_t, std::int32_t> _lastPlayerHurtTick;
    std::uint8_t _weather = 0;
    std::uint16_t _timeOfDayMinutes = 720;
    std::uint32_t _weatherSeed = 0;
    std::string _spawnTableId = "groundzero_main";
    std::uint32_t _nextAiInstanceId = 1;
    bool _aiSpawned = false;
    std::unordered_map<std::uint32_t, Entity> _aiByExternalId;
    std::unordered_map<std::uint16_t, msg::PlayerStats> _playerStats;
    /// Contract 2.16.0: one "max hp pinned" warning per player instead of one per stat rebuild.
    std::unordered_set<std::uint16_t> _maxHpMismatchLogged;
    std::unordered_map<std::uint32_t, msg::AiLoadout> _aiLoadouts;
    std::vector<std::pair<Entity, msg::Input>> _pendingInputs;
    std::vector<std::pair<std::uint16_t, msg::AiState>> _pendingAiState;
    std::vector<std::pair<Entity, std::int32_t>> _pendingDamage;
    struct PendingHit {
        Entity source = kInvalidEntity;
        std::uint16_t playerId = 0;
        msg::HitReport report;
    };
    std::vector<PendingHit> _pendingHits;
    struct PendingHitEvent {
        std::uint16_t reporterPlayerId = 0;
        msg::HitEvent hit;
    };
    std::vector<PendingHitEvent> _pendingHitEvents;
    std::unordered_map<std::uint64_t, std::int32_t> _hitEventDedup;
    // One native blast can report the same victim twice (the client's remote mirror carries
    // more than one damageable Health under one player id). Remember which explosion already
    // damaged a target so a single blast applies once per victim.
    std::unordered_map<std::uint64_t, std::int32_t> _explosionHitTick;
    std::unordered_map<std::uint32_t, std::int32_t> _hitDropLogTick;

    // M2-v10: scene object states keyed by the client-computed opaque id.
    struct SceneObjectRecord {
        std::uint8_t kind = 0;
        std::uint8_t state = 0;
    };
    std::map<std::uint32_t, SceneObjectRecord> _sceneObjects;
    void SendSceneObjectsTo(std::uint16_t playerId);

    // Latest explosion per reporter; used to give explosion-flagged hits a damage value
    // when their weapon id has no table entry (grenades, breakable barrels).
    struct PendingExplosion {
        std::int32_t x = 0;
        std::int32_t y = 0;
        std::int32_t z = 0;
        std::uint16_t radiusCm = 0;
        std::uint16_t damage = 0;
        std::int32_t tick = 0;
    };
    std::unordered_map<std::uint16_t, PendingExplosion> _pendingExplosions;
    std::unordered_map<std::uint16_t, std::int32_t> _nextExplosionLogTick;
    std::uint32_t _nextExplosionId = 1;
    std::uint16_t FindPendingExplosionDamage(std::uint16_t reporterPlayerId,
                                             const msg::HitEvent& hit) const;
    const PendingExplosion* FindPendingExplosion(std::uint16_t reporterPlayerId,
                                                 const msg::HitEvent& hit) const;
    std::unordered_map<std::uint32_t, std::uint16_t> _aiAnimSeq;
    std::unordered_map<std::uint32_t, std::uint16_t> _aiAimSeq;
    std::unordered_map<std::uint32_t, std::uint16_t> _aiStateSampleSeq;
    std::unordered_map<std::uint32_t, std::uint32_t> _aiShotSeq;
    // (sourceKind << 32 | sourceId) -> last impact shotId, monotonic per source.
    std::unordered_map<std::uint64_t, std::uint32_t> _impactSeq;
    std::unordered_map<std::uint32_t, msg::AiLoadoutV7> _aiLoadoutSnapshots;
    struct PlayerMirrorState {
        bool hasFace = false;
        std::uint8_t activeSlot = 0;
        std::vector<std::uint8_t> face;
        std::array<std::vector<std::uint8_t>, 10> slots;
    };
    std::unordered_map<std::uint16_t, PlayerMirrorState> _playerMirror;
    void SendPlayerMirrorTo(std::uint16_t targetPlayerId, std::uint16_t sourcePlayerId,
                            const PlayerMirrorState& mirror);
    std::unordered_map<std::uint16_t, std::uint16_t> _playerStateSeq;
    std::unordered_map<std::uint16_t, std::int32_t> _spawnProtectUntil;
    std::unordered_map<std::uint16_t, std::uint32_t> _playerShotSeq;
    std::unordered_map<std::uint64_t, std::int32_t> _lastHitTick;
    std::int32_t _currentTick = 0;
    struct PendingLoot {
        Entity entity = kInvalidEntity;
        std::uint16_t playerId = 0;
        msg::LootRequest request;
    };
    struct PendingExtract {
        Entity entity = kInvalidEntity;
        std::uint16_t playerId = 0;
        msg::ExtractRequest request;
    };
    struct PendingView {
        std::uint16_t playerId = 0;
        msg::ContainerViewRequest request;
    };
    struct PendingTransfer {
        std::uint16_t playerId = 0;
        msg::ItemTransfer request;
    };
    struct PendingItemModDetach {
        std::uint16_t playerId = 0;
        msg::ItemModDetach request;
    };
    struct PendingItemModAttach {
        std::uint16_t playerId = 0;
        msg::ItemModAttach request;
    };
    struct PendingArrange {
        std::uint16_t playerId = 0;
        msg::ItemArrange request;
    };
    struct PendingConsume {
        std::uint16_t playerId = 0;
        msg::ItemConsume request;
    };
    struct PendingItemReload {
        std::uint16_t playerId = 0;
        msg::ItemReload request;
    };
    struct PendingItemWeaponFire {
        std::uint16_t playerId = 0;
        msg::ItemWeaponFire request;
    };
    struct PendingItemDurability {
        std::uint16_t playerId = 0;
        msg::ItemDurability request;
    };
    struct PendingBackpackCapacity {
        std::uint16_t playerId = 0;
        msg::BackpackCapacity request;
    };
    struct PendingInventorySync {
        std::uint16_t playerId = 0;
        msg::InventorySyncRequest request;
    };
    struct PendingContainerOpen {
        std::uint16_t playerId = 0;
        msg::ContainerOpen request;
    };
    struct PendingWorldPickup {
        std::uint16_t playerId = 0;
        msg::WorldItemPickup request;
    };
    struct PendingWorldDrop {
        std::uint16_t playerId = 0;
        msg::WorldItemDrop request;
    };
    std::vector<PendingLoot> _pendingLoot;
    std::vector<PendingExtract> _pendingExtract;
    std::vector<PendingView> _pendingView;
    std::vector<PendingTransfer> _pendingTransfers;
    std::vector<PendingItemModDetach> _pendingModDetaches;
    std::vector<PendingItemModAttach> _pendingModAttaches;
    std::vector<PendingArrange> _pendingArranges;
    std::vector<PendingConsume> _pendingConsumes;
    std::vector<PendingItemReload> _pendingItemReloads;
    std::vector<PendingItemWeaponFire> _pendingItemWeaponFires;
    std::vector<PendingItemDurability> _pendingItemDurabilities;
    std::vector<PendingBackpackCapacity> _pendingBackpackCapacities;
    std::unordered_map<std::uint16_t, std::int32_t> _respawnAtTick;
    std::vector<PendingInventorySync> _pendingInventorySyncs;
    std::vector<PendingContainerOpen> _pendingContainerOpens;
    std::vector<PendingWorldPickup> _pendingWorldPickups;
    std::vector<PendingWorldDrop> _pendingWorldDrops;
    std::vector<WorldEvent> _events;
    std::vector<Outbound> _outbound;
    std::unordered_map<std::uint64_t, msg::ItemTransferResult> _transferCache;

    std::map<std::uint32_t, WorldItemRecord> _worldItems;
    std::uint32_t _nextWorldItemId = kWorldItemBase;
    std::uint32_t _nextWorldSnapshotId = 1;
    std::unordered_map<std::uint64_t, msg::LootResult> _lootCache;
    struct MapContainerInstance {
        ContainerSpawnDef spawn;
        ContainerTypeDef type;
    };
    std::unordered_map<std::uint32_t, MapContainerInstance> _mapContainers;
    std::unordered_set<std::uint32_t> _rolledMapContainers;
    struct CorpseRecord {
        std::uint32_t containerId = 0;
        std::uint16_t playerId = 0; // 0 for AI corpses
        std::uint32_t aiId = 0;
        std::uint32_t monsterId = 0;
        bool isAi = false;
        std::string scene;
        std::int32_t x = 0;
        std::int32_t y = 0;
        std::int32_t z = 0;
        std::uint16_t yaw = 0;
    };
    std::unordered_map<std::uint32_t, CorpseRecord> _corpses;
    std::unordered_map<std::uint16_t, std::uint16_t> _corpseSequence;
    std::uint32_t _nextAiCorpseId = kAiCorpseContainerBase;
    static constexpr std::uint32_t kMapContainerBase = 100000;
    void SpawnMapContainers();
    void SpawnWorldItems();
    void SpawnDebugGroundItem();
    void EnsureMapContainerRolled(std::uint32_t containerId);
    std::uint64_t _nextEventId = 1;
    std::uint8_t _phase = 1;
    std::int32_t _raidStartTick = 0;
    bool _raidStarted = false;
    std::uint32_t _raidEpoch = 0;
    std::unordered_set<std::uint16_t> _raidParticipants;
    const LootTable* _lootTable = nullptr;
    const std::unordered_map<std::string, LootTable>* _lootTables = nullptr;
    const std::unordered_map<std::uint32_t, std::string>* _aiLootTables = nullptr;
    std::mt19937 _rng{20261103u};
    struct AiTargetAssign {
        std::uint16_t playerId = 0;
        std::int32_t switchTick = 0;
    };
    std::unordered_map<std::uint32_t, AiTargetAssign> _aiTargets;
    std::unordered_map<std::uint32_t, std::pair<std::uint16_t, std::int32_t>> _lastAiHit;
    // M2-v11 spawn AOI: radii are resolved once at spawn (monster override -> spawn table).
    std::unordered_map<std::uint32_t, std::uint16_t> _aiActivationRadius;
    std::unordered_map<std::uint32_t, std::uint16_t> _aiDeactivationRadius;
    bool _aiStreamingEnabled = true;
    // 2026-11-16: default unlimited. AOI (50 m) + the 40% density already bound the active
    // set, and the cap was per-nearest-player so it could not bound the single owner's total
    // anyway. Keep SetAiMaxActive/--ai-max-active as an emergency valve only.
    int _aiMaxActivePerPlayer = 0;
    void DropAiLoot(Entity ai, std::uint16_t killerPlayerId);
    void DespawnAiOwnedBy(std::uint16_t ownerPlayerId);
    /// Owner extract/disconnect/LeaveRaid: move active AI to the smallest remaining
    /// in-raid player and despawn+respawn it on the wire; with no successor, drop it.
    void ReassignAiOwnership(std::uint16_t oldOwnerPlayerId);
    void SendAiSpawn(std::uint32_t aiId, std::uint16_t ownerPlayerId, std::uint32_t monsterId,
                     const Position& position, std::int32_t hp, std::uint16_t targetPlayer = 0,
                     std::uint16_t activationRadius = 0, std::uint16_t deactivationRadius = 0);
    void SendAiDespawn(std::uint32_t aiId, std::uint8_t reason);
    void MaybeFinishRaid();
    int _settleTicks = 0;
    msg::Snapshot _lastSnapshot;
};

} // namespace coop
