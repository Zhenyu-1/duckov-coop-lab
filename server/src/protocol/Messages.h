#pragma once

#include <array>
#include <cstdint>
#include <string>
#include <vector>

#include "protocol/Opcodes.h"
#include "protocol/Wire.h"

namespace coop::msg {

struct LoginRequest {
    std::string account;
    std::string passwordHash;
    std::string clientVersion;
    std::array<std::uint8_t, 32> contentHash{};

    void Write(wire::Writer& w) const;
    static LoginRequest Read(wire::Reader& r);
};

struct LoginResult {
    std::uint8_t code = 0;
    std::string token;
    std::int64_t expiresAtMs = 0;

    void Write(wire::Writer& w) const;
    static LoginResult Read(wire::Reader& r);
};

struct JoinRequest {
    std::string token;

    void Write(wire::Writer& w) const;
    static JoinRequest Read(wire::Reader& r);
};

struct JoinResult {
    std::uint8_t code = 0;
    std::uint16_t playerId = 0;
    std::uint64_t raidId = 0;
    std::string sceneId;
    std::int32_t startTick = 0;
    std::uint8_t tickRate = 20;

    void Write(wire::Writer& w) const;
    static JoinResult Read(wire::Reader& r);
};

struct Input {
    std::uint32_t seq = 0;
    std::int32_t x = 0;
    std::int32_t y = 0;
    std::int32_t z = 0;
    std::uint16_t yaw = 0;
    std::int16_t pitch = 0;
    std::uint8_t flags = 0;

    void Write(wire::Writer& w) const;
    static Input Read(wire::Reader& r);
};

struct PlayerSnapshot {
    std::uint16_t playerId = 0;
    std::int32_t x = 0;
    std::int32_t y = 0;
    std::int32_t z = 0;
    std::uint16_t yaw = 0;
    std::uint8_t flags = 0;
    std::uint16_t hp = 0;
    /// Contract 2.16.0 / wire 14: the server's max HP for this player (always 100 today).
    /// Clients render remote health bars as hp / maxHp; 0 means "unknown, keep the local one".
    std::uint16_t maxHp = 0;
    std::string scene;
};

/// Client -> server: the simulator reports AI transform (never health).
struct AiState {
    std::uint32_t aiId = 0;
    std::uint16_t ownerPlayerId = 0;
    std::int32_t x = 0;
    std::int32_t y = 0;
    std::int32_t z = 0;
    std::uint16_t yaw = 0;
    std::uint8_t flags = 0;

    void Write(wire::Writer& w) const;
    static AiState Read(wire::Reader& r);
};

/// Loot transaction. action: 0 = take from container, 1 = put into container.
struct LootRequest {
    std::uint64_t correlationId = 0;
    std::uint32_t containerId = 0;
    std::uint16_t slot = 0;
    std::uint8_t action = 0;
    std::uint32_t itemUid = 0;
    std::uint32_t count = 0;

    void Write(wire::Writer& w) const;
    static LootRequest Read(wire::Reader& r);
};

struct LootResult {
    std::uint64_t correlationId = 0;
    std::uint8_t code = 0;
    std::uint32_t itemUid = 0;
    std::uint32_t itemId = 0;
    std::uint32_t count = 0;
    std::uint16_t slot = 0;

    void Write(wire::Writer& w) const;
    static LootResult Read(wire::Reader& r);
};

struct ExtractRequest {
    std::uint64_t correlationId = 0;

    void Write(wire::Writer& w) const;
    static ExtractRequest Read(wire::Reader& r);
};

struct ExtractResult {
    std::uint64_t correlationId = 0;
    std::uint8_t code = 0;

    void Write(wire::Writer& w) const;
    static ExtractResult Read(wire::Reader& r);
};

/// CPlayerReady (47): one non-host player toggles readiness for the next raid.
struct PlayerReady {
    bool ready = false;

    void Write(wire::Writer& w) const;
    static PlayerReady Read(wire::Reader& r);
};

/// SPlayerReady (195): the server's authoritative ready flag for one player.
struct PlayerReadyState {
    std::uint16_t playerId = 0;
    bool ready = false;

    void Write(wire::Writer& w) const;
    static PlayerReadyState Read(wire::Reader& r);
};

/// CForceResetRaid (48): empty payload; only the host may send it.
struct ForceResetRaid {
    void Write(wire::Writer& w) const;
    static ForceResetRaid Read(wire::Reader& r);
};

/// SForceResetRaid (196): the current raid was force-ended; every client returns to base.
struct ForceResetRaidState {
    std::uint16_t byPlayerId = 0;

    void Write(wire::Writer& w) const;
    static ForceResetRaidState Read(wire::Reader& r);
};

/// Which control request an SRaidControlResult answers.
enum class RaidControlAction : std::uint8_t {
    StartRaid = 0,
    ForceReset = 1,
};

/// SRaidControlResult (197): explicit accept/reject for a host lobby command.
struct RaidControlResult {
    std::uint8_t action = 0; // RaidControlAction
    std::uint8_t code = 0;   // ErrorCode
    std::uint16_t playerId = 0;

    void Write(wire::Writer& w) const;
    static RaidControlResult Read(wire::Reader& r);
};

/// Broadcast notification about a player (join/leave/died/extracted).
struct PlayerEvent {
    std::uint64_t eventId = 0;
    std::uint16_t playerId = 0;
    std::uint8_t type = 0; // 0 joined, 1 left, 2 died, 3 extracted

    void Write(wire::Writer& w) const;
    static PlayerEvent Read(wire::Reader& r);
};

struct RaidPhaseMessage {
    std::uint8_t phase = 0; // 0 loading, 1 active, 2 settling, 3 closed
    std::int32_t tick = 0;

    void Write(wire::Writer& w) const;
    static RaidPhaseMessage Read(wire::Reader& r);
};

/// Client asks to see a container's contents (after opening/searching).
struct ContainerViewRequest {
    std::uint64_t correlationId = 0;
    std::uint32_t containerId = 0;

    void Write(wire::Writer& w) const;
    static ContainerViewRequest Read(wire::Reader& r);
};

struct ContainerViewEntry {
    std::uint16_t slot = 0;
    std::uint32_t itemUid = 0;
    std::uint32_t itemId = 0;
    std::uint32_t count = 0;
};

struct ContainerView {
    std::uint64_t correlationId = 0;
    std::uint32_t containerId = 0;
    std::uint16_t capacity = 0;
    std::vector<ContainerViewEntry> entries;

    void Write(wire::Writer& w) const;
    static ContainerView Read(wire::Reader& r);
};

/// Client asks the server to start a raid on a scene; the server broadcasts it to everyone.
struct RaidStart {
    std::uint32_t sceneIndex = 0;
    std::string sceneName;
    std::uint32_t raidEpoch = 0; // filled by the server; spawn reports must echo it

    void Write(wire::Writer& w) const;
    static RaidStart Read(wire::Reader& r);
};

/// Client claims it hit something. The server validates range/cooldown/damage.
struct HitReport {
    std::uint64_t correlationId = 0;
    std::uint8_t targetKind = 0; // 0 = player, 1 = ai
    std::uint32_t targetId = 0;
    std::int32_t damage = 0;

    void Write(wire::Writer& w) const;
    static HitReport Read(wire::Reader& r);
};

struct CombatEvent {
    std::uint64_t eventId = 0;
    std::uint8_t type = 0; // 0 damaged, 1 died
    std::uint8_t targetKind = 0;
    std::uint32_t targetId = 0;
    std::uint16_t playerId = 0;
    std::int32_t value = 0;

    void Write(wire::Writer& w) const;
    static CombatEvent Read(wire::Reader& r);
};

/// Server-spawned AI: every client creates a proxy at this position.
struct AiSpawn {
    std::uint32_t aiId = 0;
    std::uint16_t ownerPlayerId = 0;
    std::uint32_t monsterId = 0;
    std::int32_t x = 0;
    std::int32_t y = 0;
    std::int32_t z = 0;
    std::uint16_t yaw = 0;
    std::uint16_t hp = 0;
    std::uint32_t raidEpoch = 0;
    // M2-v11 spawn AOI: per-AI streaming radii (cm). 0 means "client default".
    std::uint16_t activationRadius = 0;
    std::uint16_t deactivationRadius = 0;

    void Write(wire::Writer& w) const;
    static AiSpawn Read(wire::Reader& r);
};

/// Server-authoritative AI lifecycle removal (reliable).
enum class AiDespawnReason : std::uint8_t {
    Death = 0,
    OwnerLeft = 1,
    RaidEnd = 2,
    Cleanup = 3,
};

struct AiDespawn {
    std::uint32_t aiId = 0;
    std::uint32_t raidEpoch = 0;
    std::uint8_t reason = 0; // AiDespawnReason

    void Write(wire::Writer& w) const;
    static AiDespawn Read(wire::Reader& r);
};

/// D5 hit facts (C→S, opcode 20). Never carries damage.
enum class HitFlags : std::uint8_t {
    Explosion = 1 << 0,
    Melee = 1 << 1,
    HasImpactCenter = 1 << 2,
    HasRadius = 1 << 3,
    Headshot = 1 << 4,
    RealDamage = 1 << 5,
};

struct HitEvent {
    std::uint32_t hitId = 0;
    std::uint8_t sourceKind = 0; // 0 player, 1 ai, 2 environment
    std::uint32_t sourceId = 0;
    std::uint16_t reporterPlayerId = 0;
    std::uint8_t targetKind = 0;
    std::uint32_t targetId = 0;
    std::uint32_t weaponId = 0;
    std::uint32_t ammoId = 0;
    std::uint32_t attackId = 0;
    std::int32_t hitX = 0;
    std::int32_t hitY = 0;
    std::int32_t hitZ = 0;
    std::uint8_t flags = 0; // HitFlags
    std::int32_t impactX = 0;
    std::int32_t impactY = 0;
    std::int32_t impactZ = 0;
    std::uint16_t radius = 0;

    void Write(wire::Writer& w) const;
    static HitEvent Read(wire::Reader& r);
};

/// D5 authoritative combat result (S→C, opcode 161 for v4).
struct CombatEventV4 {
    std::uint64_t eventId = 0;
    std::uint32_t causeId = 0;
    std::uint8_t type = 0; // 0 damaged, 1 died, 2 healed
    std::uint8_t sourceKind = 0;
    std::uint32_t sourceId = 0;
    std::uint16_t reporterPlayerId = 0;
    std::uint8_t targetKind = 0;
    std::uint32_t targetId = 0;
    std::uint32_t damage = 0;
    std::int32_t hpAfter = 0;
    std::int32_t maxHp = 0;
    std::uint8_t element = 0;
    std::uint8_t crit = 0;
    std::int32_t hitX = 0;
    std::int32_t hitY = 0;
    std::int32_t hitZ = 0;
    std::uint32_t weaponId = 0;
    std::uint8_t flags = 0;

    void Write(wire::Writer& w) const;
    static CombatEventV4 Read(wire::Reader& r);
};

/// CAiAnim/SAiAnim payload (opcodes 21/165).
struct AiAnim {
    std::uint32_t aiId = 0;
    std::uint32_t raidEpoch = 0;
    std::uint16_t seq = 0;
    std::uint16_t moveSpeed = 0;
    std::int8_t moveDirX = 0;
    std::int8_t moveDirY = 0;
    std::uint8_t handState = 0;
    std::uint8_t attackIndex = 0;
    std::uint8_t flags = 0;
    std::uint8_t attackSeq = 0;

    void Write(wire::Writer& w) const;
    static AiAnim Read(wire::Reader& r);
};

/// SAiHealth (S→C, opcode 166): non-damage HP changes.
struct AiHealth {
    std::uint32_t aiId = 0;
    std::uint32_t raidEpoch = 0;
    std::int32_t hp = 0;
    std::int32_t maxHp = 0;
    std::uint8_t flags = 0;
    std::uint16_t seq = 0;

    void Write(wire::Writer& w) const;
    static AiHealth Read(wire::Reader& r);
};

/// CAiLoadout (C→S, opcode 22): host-reported generated AI equipment ids.
struct AiLoadout {
    std::uint32_t aiId = 0;
    std::uint32_t raidEpoch = 0;
    std::uint32_t weaponId = 0;
    std::uint32_t bulletId = 0;
    std::uint32_t armorId = 0;
    std::uint32_t helmetId = 0;

    void Write(wire::Writer& w) const;
    static AiLoadout Read(wire::Reader& r);
};

/// v6 CAiAim/SAiAim: host-authored AI aim; server relays without consuming it in damage.
struct AiAim {
    std::uint32_t aiId = 0;
    std::uint32_t raidEpoch = 0;
    std::uint16_t seq = 0;
    std::uint16_t aimYaw = 0; // raw 0..65535, same convention as SAiState.yaw
    std::int16_t aimPitch = 0; // 0.01 degrees, -9000..9000
    std::uint8_t flags = 0;

    void Write(wire::Writer& w) const;
    static AiAim Read(wire::Reader& r);
};

/// v7 CAiStateSample/SAiStateSample: animator state hash + normalized time.
struct AiStateSample {
    std::uint32_t aiId = 0;
    std::uint32_t raidEpoch = 0;
    std::uint16_t seq = 0;
    std::uint32_t stateHash = 0; // AnimatorStateInfo.fullPathHash (layer 0)
    std::uint16_t normTime = 0;  // fractional normalizedTime * 65535
    std::uint8_t flags = 0;      // bit0 valid, bit1 inTransition

    void Write(wire::Writer& w) const;
    static AiStateSample Read(wire::Reader& r);
};

struct AiLoadoutSlot {
    std::uint8_t slot = 0;
    std::vector<std::uint8_t> blob;
};

/// v7 CAiLoadoutV7/SAiLoadoutV7: opaque host ItemSnapshot/ItemTreeData blobs.
struct AiLoadoutV7 {
    std::uint32_t aiId = 0;
    std::uint32_t raidEpoch = 0;
    std::uint8_t activeSlot = 0;
    std::vector<AiLoadoutSlot> slots;

    void Write(wire::Writer& w) const;
    static AiLoadoutV7 Read(wire::Reader& r);
};

/// v7 CAiShot/SAiShot: host-authored shot/projectile presentation facts.
struct AiShot {
    std::uint32_t aiId = 0;
    std::uint32_t raidEpoch = 0;
    std::uint32_t shotId = 0;
    std::uint32_t weaponId = 0;
    std::uint32_t bulletId = 0;
    std::int32_t originX = 0;
    std::int32_t originY = 0;
    std::int32_t originZ = 0;
    std::int16_t dirX = 0;
    std::int16_t dirY = 0;
    std::int16_t dirZ = 0;
    std::uint32_t seed = 0;
    std::uint8_t flags = 0;

    void Write(wire::Writer& w) const;
    static AiShot Read(wire::Reader& r);
};

/// v8 CPlayerStateSample; SPlayerStateSample prepends playerId on the wire.
struct PlayerStateSample {
    std::uint32_t raidEpoch = 0;
    std::uint16_t seq = 0;
    std::uint32_t stateHash = 0;
    std::uint16_t normTime = 0;
    std::uint8_t flags = 0;
    std::uint8_t attackSeq = 0;
    std::uint8_t handState = 0;

    void Write(wire::Writer& w) const;
    static PlayerStateSample Read(wire::Reader& r);
};

/// v8 CPlayerLoadoutV8; SPlayerLoadoutV8 prepends playerId and uses the same payload.
struct PlayerLoadoutV8 {
    std::uint32_t raidEpoch = 0;
    std::uint8_t activeSlot = 0;
    std::uint8_t slot = 0;
    std::vector<std::uint8_t> blob;

    void Write(wire::Writer& w) const;
    static PlayerLoadoutV8 Read(wire::Reader& r);
};

/// v8 CPlayerShot; SPlayerShot prepends playerId.
struct PlayerShot {
    std::uint32_t raidEpoch = 0;
    std::uint32_t shotId = 0;
    std::uint32_t weaponId = 0;
    std::uint32_t bulletId = 0;
    std::int32_t originX = 0;
    std::int32_t originY = 0;
    std::int32_t originZ = 0;
    std::int16_t dirX = 0;
    std::int16_t dirY = 0;
    std::int16_t dirZ = 0;
    std::uint32_t seed = 0;
    std::uint8_t flags = 0;

    void Write(wire::Writer& w) const;
    static PlayerShot Read(wire::Reader& r);
};

/// v8 CPlayerFaceV8; SPlayerFaceV8 prepends playerId.
struct PlayerFaceV8 {
    std::uint32_t raidEpoch = 0;
    std::vector<std::uint8_t> face;

    void Write(wire::Writer& w) const;
    static PlayerFaceV8 Read(wire::Reader& r);
};

// ---------------------------------------------------------------------------
// v9 item contract (SPEC-2026-11-13-item-protocol-v9).
//
// Bounded fields are clamped to the SPEC maxima on write and on read, so a decoded message
// never exceeds the documented limits and frames stay under wire::kMaxPayload without
// relying on automatic fragmentation (world items use explicit chunks instead).
// ---------------------------------------------------------------------------

inline constexpr std::size_t kMaxTicketIdBytes = 32;
inline constexpr std::size_t kMaxSceneBytes = 64;
inline constexpr std::size_t kMaxInitialInventoryEntries = 64;
inline constexpr std::size_t kMaxInventorySnapshotEntries = 64;
inline constexpr std::size_t kMaxContainerEntries = 32;
inline constexpr std::size_t kMaxWorldItemChunkEntries = 8;

/// Inventory kinds on the wire. CItemTransfer source/target only use PlayerEquipment(0)
/// .. MapContainer(3); WorldItem(4) is a WorldItemInfo-only kind and CorpseContainer(5)
/// is reserved for D6.
enum class InventoryKind : std::uint8_t {
    PlayerEquipment = 0,
    PlayerBackpack = 1,
    PlayerPetBag = 2,
    MapContainer = 3,
    WorldItem = 4,
    CorpseContainer = 5,
};

/// WorldItemInfo.source: how the ground item came into existence.
enum class WorldItemSource : std::uint8_t {
    Spawned = 0,
    Dropped = 1,
    Corpse = 2,
};

/// 14B: slot u16, itemUid u32, itemId u32, count u32.
struct InventoryEntry {
    std::uint16_t slot = 0;
    std::uint32_t itemUid = 0;
    std::uint32_t itemId = 0;
    std::uint32_t count = 0;

    void Write(wire::Writer& w) const;
    static InventoryEntry Read(wire::Reader& r);
};

/// Inventory snapshot: kind u8, ownerId u32, revision u32, capacity u16, count u16 (<= 64),
/// then `count` InventoryEntry. The count is derived from `entries` when writing.
struct InventorySnapshot {
    std::uint8_t kind = 0; // InventoryKind
    std::uint32_t ownerId = 0;
    std::uint32_t revision = 0;
    std::uint16_t capacity = 0;
    std::vector<InventoryEntry> entries;

    void Write(wire::Writer& w) const;
    static InventorySnapshot Read(wire::Reader& r);
};

/// One flat row of SInitialInventory: the owning kind plus the entry.
struct InitialInventoryEntry {
    std::uint8_t kind = 0; // InventoryKind 0/1/2
    InventoryEntry entry;

    void Write(wire::Writer& w) const;
    static InitialInventoryEntry Read(wire::Reader& r);
};

/// SInitialInventory (178): one message, flat entries, explicit capacities for all three
/// player inventories (empty inventories still report their capacity).
struct InitialInventory {
    std::string ticketId; // <= 32 bytes
    std::uint32_t revision = 0;
    std::uint16_t equipmentCapacity = 0;
    std::uint16_t backpackCapacity = 0;
    std::uint16_t petBagCapacity = 0;
    std::vector<InitialInventoryEntry> entries; // <= 64

    void Write(wire::Writer& w) const;
    static InitialInventory Read(wire::Reader& r);
};

/// CItemTransfer (34).
struct ItemTransfer {
    std::uint64_t correlationId = 0;
    std::uint8_t sourceKind = 0; // InventoryKind 0..3
    std::uint32_t sourceId = 0;
    std::uint16_t sourceSlot = 0;
    std::uint8_t targetKind = 0; // InventoryKind 0..3
    std::uint32_t targetId = 0;
    std::uint16_t targetSlot = 0;
    std::uint32_t itemUid = 0;
    std::uint32_t count = 0;
    std::uint32_t expectedItemId = 0;
    std::uint32_t expectedRevision = 0; // source inventory revision

    void Write(wire::Writer& w) const;
    static ItemTransfer Read(wire::Reader& r);
};

/// SItemTransfer (179). On failure itemUid is 0; revisions carry the current values.
struct ItemTransferResult {
    std::uint64_t correlationId = 0;
    std::uint8_t code = 0; // ErrorCode
    std::uint32_t itemUid = 0;
    std::uint32_t sourceRevision = 0;
    std::uint32_t targetRevision = 0;

    void Write(wire::Writer& w) const;
    static ItemTransferResult Read(wire::Reader& r);
};

/// CItemArrange (39): final slot layout for a container sort/merge/reorder. The server
/// validates uid identity, per-itemId count conservation and max stack before applying.
struct ItemArrange {
    std::uint64_t correlationId = 0;
    std::uint8_t kind = 0; // InventoryKind 1/2/3 (equipment is fixed-slot and rejected)
    std::uint32_t ownerId = 0;
    std::uint32_t expectedRevision = 0;
    std::vector<InventoryEntry> entries; // <= 64

    void Write(wire::Writer& w) const;
    static ItemArrange Read(wire::Reader& r);
};

/// SItemArrange (187).
struct ItemArrangeResult {
    std::uint64_t correlationId = 0;
    std::uint8_t code = 0; // ErrorCode
    std::uint32_t revision = 0;

    void Write(wire::Writer& w) const;
    static ItemArrangeResult Read(wire::Reader& r);
};

/// CItemConsume (40): use/consume a stack from one of the player's containers.
struct ItemConsume {
    std::uint64_t correlationId = 0;
    std::uint8_t kind = 0; // InventoryKind 0/1/2
    std::uint32_t ownerId = 0;
    std::uint16_t slot = 0;
    std::uint32_t itemUid = 0;
    std::uint32_t itemId = 0;
    std::uint32_t count = 0;
    std::uint32_t expectedRevision = 0;
    std::uint8_t reason = 0; // 0 use, 1 ammo, 2 durability
    /// Contract 2.16.0 / wire 14: HP restored by this use (0 = not a healing item). The
    /// server clamps it to a sane per-use maximum and settles the player's HP authoritatively.
    std::uint32_t healAmount = 0;

    void Write(wire::Writer& w) const;
    static ItemConsume Read(wire::Reader& r);
};

/// SItemConsume (188).
struct ItemConsumeResult {
    std::uint64_t correlationId = 0;
    std::uint8_t code = 0; // ErrorCode
    std::uint32_t itemUid = 0;
    std::uint32_t remaining = 0;
    std::uint32_t revision = 0;

    void Write(wire::Writer& w) const;
    static ItemConsumeResult Read(wire::Reader& r);
};

/// SCorpseSpawn (189): one player corpse container in the raid.
struct CorpseSpawn {
    std::uint32_t containerId = 0;
    std::uint16_t playerId = 0;
    std::string scene; // <= 64 bytes
    std::int32_t x = 0;
    std::int32_t y = 0;
    std::int32_t z = 0;
    std::uint16_t yaw = 0;

    void Write(wire::Writer& w) const;
    static CorpseSpawn Read(wire::Reader& r);
};

/// SCorpseDespawn (190).
struct CorpseDespawn {
    std::uint32_t containerId = 0;

    void Write(wire::Writer& w) const;
    static CorpseDespawn Read(wire::Reader& r);
};

/// SItemDetail (191): durability / loaded ammo side channel keyed by item uid.
struct ItemDetail {
    std::uint32_t itemUid = 0;
    std::uint32_t durability = 0;
    std::uint32_t maxDurability = 0;
    std::uint32_t loadedAmmoItemId = 0;
    std::uint32_t loadedAmmoCount = 0;

    void Write(wire::Writer& w) const;
    static ItemDetail Read(wire::Reader& r);
};

inline constexpr std::uint16_t kMaxItemMods = 8;
inline constexpr std::size_t kMaxModSlotKeyBytes = 64;

/// One attachment: the native `Item.Slots` key it plugs into plus the attachment item id.
struct ItemMod {
    std::string slotKey;
    std::uint32_t itemId = 0;
};

/// SItemMods (198): slot-keyed attachments owned by the server for one item uid.
struct ItemMods {
    std::uint32_t itemUid = 0;
    std::vector<ItemMod> mods;

    void Write(wire::Writer& w) const;
    static ItemMods Read(wire::Reader& r);
};

/// CItemModDetach (49): remove one attachment from a server-owned item. The server turns it
/// into a normal backpack item (new uid) so it can be dropped and seen by every client.
struct ItemModDetach {
    std::uint64_t correlationId = 0;
    std::uint32_t parentUid = 0;
    std::string slotKey;

    void Write(wire::Writer& w) const;
    static ItemModDetach Read(wire::Reader& r);
};

/// SItemModDetachResult (199): `itemUid` is the new backpack item, 0 on failure.
struct ItemModDetachResult {
    std::uint64_t correlationId = 0;
    std::uint8_t code = 0; // ErrorCode
    std::uint32_t itemUid = 0;
    std::uint32_t parentUid = 0;

    void Write(wire::Writer& w) const;
    static ItemModDetachResult Read(wire::Reader& r);
};

/// Contract 2.17.0: CItemModAttach (50). Plug one backpack item (`itemUid`) into the
/// attachment slot `slotKey` of `parentUid`; the backpack item is consumed into the attachment.
struct ItemModAttach {
    std::uint64_t correlationId = 0;
    std::uint32_t parentUid = 0;
    std::uint32_t itemUid = 0;
    std::string slotKey;

    void Write(wire::Writer& w) const;
    static ItemModAttach Read(wire::Reader& r);
};

/// SItemModAttachResult (200): `itemId` is the attached item id, 0 on failure.
struct ItemModAttachResult {
    std::uint64_t correlationId = 0;
    std::uint8_t code = 0; // ErrorCode
    std::uint32_t parentUid = 0;
    std::uint32_t itemUid = 0;
    std::uint32_t itemId = 0;

    void Write(wire::Writer& w) const;
    static ItemModAttachResult Read(wire::Reader& r);
};

/// CItemReload (41): authoritative reload request. `count` units of ammoItemId are removed
/// from the backpack; `loadedAfter` is the resulting magazine count.
struct ItemReload {
    std::uint64_t correlationId = 0;
    std::uint32_t gunUid = 0;
    std::uint32_t ammoItemId = 0;
    std::uint32_t count = 0;
    std::uint32_t loadedAfter = 0;

    void Write(wire::Writer& w) const;
    static ItemReload Read(wire::Reader& r);
};

/// SItemReloadResult (192).
struct ItemReloadResult {
    std::uint64_t correlationId = 0;
    std::uint8_t code = 0; // ErrorCode
    std::uint32_t gunUid = 0;
    std::uint32_t ammoItemId = 0;
    std::uint32_t loadedCount = 0;
    std::uint32_t backpackRevision = 0;

    void Write(wire::Writer& w) const;
    static ItemReloadResult Read(wire::Reader& r);
};

/// CItemWeaponFire (42): fire-and-forget magazine count after a shot.
struct ItemWeaponFire {
    std::uint32_t gunUid = 0;
    std::uint32_t ammoItemId = 0;
    std::uint32_t loadedCount = 0;

    void Write(wire::Writer& w) const;
    static ItemWeaponFire Read(wire::Reader& r);
};

/// CItemDurability (43): client-side durability change for one server-owned item.
/// Values are fixed-point x1000 to preserve the native float precision.
struct ItemDurability {
    std::uint32_t itemUid = 0;
    std::uint32_t durability = 0;
    std::uint32_t maxDurability = 0;

    void Write(wire::Writer& w) const;
    static ItemDurability Read(wire::Reader& r);
};

/// CBackpackCapacity (44): the native equipped-backpack capacity (bounded by the server).
struct BackpackCapacity {
    std::uint32_t capacity = 0;

    void Write(wire::Writer& w) const;
    static BackpackCapacity Read(wire::Reader& r);
};

/// SRespawn (194): revive the player at the corpse position with empty equipment/backpack.
struct Respawn {
    std::uint16_t playerId = 0;
    std::int32_t x = 0;
    std::int32_t y = 0;
    std::int32_t z = 0;
    std::uint16_t yaw = 0;
    std::uint16_t hp = 0;

    void Write(wire::Writer& w) const;
    static Respawn Read(wire::Reader& r);
};

/// CContainerOpen (35).
struct ContainerOpen {
    std::uint64_t correlationId = 0;
    std::uint32_t containerId = 0;

    void Write(wire::Writer& w) const;
    static ContainerOpen Read(wire::Reader& r);
};

/// SContainerContents (180): one complete snapshot, no search-progress message.
struct ContainerContents {
    std::uint64_t correlationId = 0;
    std::uint32_t containerId = 0;
    std::uint32_t revision = 0;
    std::uint16_t capacity = 0;
    std::vector<InventoryEntry> entries; // <= 32

    void Write(wire::Writer& w) const;
    static ContainerContents Read(wire::Reader& r);
};

/// CInventorySync (38).
struct InventorySyncRequest {
    std::uint64_t correlationId = 0;
    std::uint8_t kind = 0; // InventoryKind
    std::uint32_t ownerId = 0;

    void Write(wire::Writer& w) const;
    static InventorySyncRequest Read(wire::Reader& r);
};

/// SInventorySnapshot (186): correlationId is 0 for unsolicited pushes.
struct InventoryState {
    std::uint64_t correlationId = 0;
    InventorySnapshot snapshot;

    void Write(wire::Writer& w) const;
    static InventoryState Read(wire::Reader& r);
};

/// Ground item payload used by SWorldItemSpawn/SWorldItemDrop/SWorldItemSnapshot.
struct WorldItemInfo {
    std::uint32_t worldItemId = 0;
    std::uint32_t itemUid = 0;
    std::uint32_t itemId = 0;
    std::uint32_t count = 0;
    std::string scene; // <= 64 bytes
    std::int32_t x = 0;
    std::int32_t y = 0;
    std::int32_t z = 0;
    std::uint16_t yaw = 0;
    std::uint8_t source = 0; // WorldItemSource

    void Write(wire::Writer& w) const;
    static WorldItemInfo Read(wire::Reader& r);
};

/// CWorldItemPickup (36): target is always the player backpack; M2 requires count to equal
/// the world item count (no partial pickup).
struct WorldItemPickup {
    std::uint64_t correlationId = 0;
    std::uint32_t worldItemId = 0;
    std::uint32_t expectedItemUid = 0;
    std::uint32_t expectedItemId = 0;
    std::uint32_t count = 0;
    std::uint32_t expectedBackpackRevision = 0;

    void Write(wire::Writer& w) const;
    static WorldItemPickup Read(wire::Reader& r);
};

/// SWorldItemPickup (181).
struct WorldItemPickupResult {
    std::uint64_t correlationId = 0;
    std::uint8_t code = 0; // ErrorCode
    std::uint32_t itemUid = 0;
    std::uint32_t backpackRevision = 0;

    void Write(wire::Writer& w) const;
    static WorldItemPickupResult Read(wire::Reader& r);
};

/// CWorldItemDrop (37).
struct WorldItemDrop {
    std::uint64_t correlationId = 0;
    std::uint8_t sourceKind = 0; // InventoryKind 0..3
    std::uint32_t sourceId = 0;
    std::uint16_t sourceSlot = 0;
    std::uint32_t itemUid = 0;
    std::uint32_t count = 0;
    std::string scene; // <= 64 bytes
    std::int32_t x = 0;
    std::int32_t y = 0;
    std::int32_t z = 0;
    std::uint16_t yaw = 0;
    std::uint32_t expectedRevision = 0; // source inventory

    void Write(wire::Writer& w) const;
    static WorldItemDrop Read(wire::Reader& r);
};

/// SWorldItemDrop (182).
struct WorldItemDropResult {
    std::uint64_t correlationId = 0;
    std::uint8_t code = 0; // ErrorCode
    std::uint32_t worldItemId = 0;
    std::uint32_t sourceRevision = 0;

    void Write(wire::Writer& w) const;
    static WorldItemDropResult Read(wire::Reader& r);
};

/// SWorldItemSpawn (184): payload is a bare WorldItemInfo.
struct WorldItemSpawn {
    WorldItemInfo info;

    void Write(wire::Writer& w) const;
    static WorldItemSpawn Read(wire::Reader& r);
};

/// SWorldItemDespawn (185).
struct WorldItemDespawn {
    std::uint32_t worldItemId = 0;

    void Write(wire::Writer& w) const;
    static WorldItemDespawn Read(wire::Reader& r);
};

/// SWorldItemSnapshot (183): explicit chunking (<= 8 entries per chunk, 0-based chunkIndex).
struct WorldItemSnapshotChunk {
    std::uint32_t snapshotId = 0;
    std::uint16_t chunkIndex = 0;
    std::uint16_t chunkCount = 0;
    std::vector<WorldItemInfo> items; // <= 8

    void Write(wire::Writer& w) const;
    static WorldItemSnapshotChunk Read(wire::Reader& r);
};

/// CPlayerStats (C→S, opcode 23): one snapshot on spawn/equipment change.
struct PlayerStats {
    float gunDamageMultiplier = 1.0f;
    float gunCritRateGain = 0.0f;
    float gunCritDamageGain = 0.0f;
    float meleeDamageMultiplier = 1.0f;
    float meleeCritRateGain = 0.0f;
    float meleeCritDamageGain = 0.0f;
    std::array<float, 7> elementFactors{1, 1, 1, 1, 1, 1, 1};
    float bodyArmor = 0.0f;
    float headArmor = 0.0f;
    float maxHealth = 100.0f;

    void Write(wire::Writer& w) const;
    static PlayerStats Read(wire::Reader& r);
};

/// v5 CShootFact (opcode 24): one fact per trigger pull, never per bullet.
struct ShootFact {
    std::uint8_t sourceKind = 0; // 0 player, 1 ai
    std::uint32_t sourceId = 0;
    std::uint32_t weaponId = 0;
    std::uint32_t ammoId = 0;
    std::uint32_t attackId = 0;
    std::uint8_t flags = 0; // bit0 melee

    void Write(wire::Writer& w) const;
    static ShootFact Read(wire::Reader& r);
};

/// v5 CUseConsumable (opcode 25): use-completed fact; inventory stays client-authoritative.
struct UseConsumable {
    /// Contract 2.16.0: the same use was also reported as CItemConsume, so its HP is settled
    /// by that path; the server must not heal twice.
    static constexpr std::uint8_t kFlagSettledByItemConsume = 1u << 0;

    std::uint64_t correlationId = 0;
    std::uint32_t itemId = 0;
    std::uint8_t slot = 0xFF;
    std::uint8_t flags = 0;

    void Write(wire::Writer& w) const;
    static UseConsumable Read(wire::Reader& r);
};

enum class PlayerBuffOp : std::uint8_t {
    Add = 0,
    Update = 1,
    Remove = 2,
};

/// v5 SPlayerBuff (opcode 167): server-authoritative buff state for presentation.
struct PlayerBuff {
    std::uint16_t playerId = 0;
    std::uint32_t buffId = 0;
    std::uint8_t layers = 1;
    std::uint32_t remainingMs = 0;
    std::uint8_t op = 0; // PlayerBuffOp

    void Write(wire::Writer& w) const;
    static PlayerBuff Read(wire::Reader& r);
};

/// v5 SRaidRules (opcode 168): 66-byte raid rules/environment snapshot.
struct RaidRules {
    std::uint8_t rulesetId = 13; // RuleIndex.StandardChallenge
    float damageFactorToPlayer = 1.0f;
    float enemyHealthFactor = 1.0f;
    float enemyAttackTimeFactor = 1.0f;
    float enemyAttackTimeSpaceFactor = 1.0f;
    float enemyReactionTimeFactor = 1.0f;
    std::uint8_t flags = 0x06; // bit0 advancedDebuff, bit1 fog, bit2 spawnDeadBody
    float recoilMultiplier = 1.0f;
    std::uint8_t weather = 0; // 0 sunny, 1 cloudy, 2 rainy, 3/4 storm, 22 snow
    std::uint16_t timeOfDayMinutes = 720;
    std::uint8_t saveDeadbodyCount = 1;
    std::uint32_t weatherSeed = 0;
    std::array<std::uint8_t, 32> baseStatsHash{};

    void Write(wire::Writer& w) const;
    static RaidRules Read(wire::Reader& r);
};

/// Client reports where the game spawned it after a scene load.
struct PlayerSpawn {
    std::string sceneName;
    std::int32_t x = 0;
    std::int32_t y = 0;
    std::int32_t z = 0;
    std::uint32_t raidEpoch = 0;

    void Write(wire::Writer& w) const;
    static PlayerSpawn Read(wire::Reader& r);
};

/// One equipment slot: 0 primary, 1 secondary, 2 melee, 3 armor, 4 helmet,
/// 5 face mask, 6 backpack, 7 headset. itemId 0 means empty.
struct LoadoutEntry {
    std::uint8_t slot = 0;
    std::uint32_t itemId = 0;
};

struct LoadoutRequest {
    std::uint64_t correlationId = 0;
    std::vector<LoadoutEntry> entries;

    void Write(wire::Writer& w) const;
    static LoadoutRequest Read(wire::Reader& r);
};

/// Server answer / broadcast: one player's accepted loadout.
struct LoadoutState {
    std::uint64_t correlationId = 0;
    std::uint8_t code = 0;
    std::uint16_t playerId = 0;
    std::vector<LoadoutEntry> entries;

    void Write(wire::Writer& w) const;
    static LoadoutState Read(wire::Reader& r);
};

/// Server -> client: the authoritative stash mirror the client must persist
/// and acknowledge before it is allowed to spawn (B4).
struct MirrorEntry {
    std::uint32_t itemId = 0;
    std::uint32_t count = 0;
};

struct MirrorState {
    std::uint64_t version = 0;
    std::uint16_t playerId = 0;
    std::vector<MirrorEntry> entries;

    void Write(wire::Writer& w) const;
    static MirrorState Read(wire::Reader& r);
};

struct MirrorAck {
    std::uint64_t version = 0;

    void Write(wire::Writer& w) const;
    static MirrorAck Read(wire::Reader& r);
};

/// Server -> AI simulator: the aggro director's current target (0 = none).
struct AiTarget {
    std::uint32_t aiId = 0;
    std::uint16_t playerId = 0;
    std::uint16_t reserved = 0;
    std::int32_t tick = 0;

    void Write(wire::Writer& w) const;
    static AiTarget Read(wire::Reader& r);
};

/// Client tries to resume a session that is still inside the expiry window.
struct ResumeRequest {
    std::string token;

    void Write(wire::Writer& w) const;
    static ResumeRequest Read(wire::Reader& r);
};

struct ResumeResult {
    std::uint8_t code = 1; // 0 ok, 1 not found
    std::uint16_t playerId = 0;
    std::string sceneName; // empty: resume in the lobby, no teleport
    std::int32_t x = 0;
    std::int32_t y = 0;
    std::int32_t z = 0;
    std::uint32_t raidEpoch = 0;

    void Write(wire::Writer& w) const;
    static ResumeResult Read(wire::Reader& r);
};

/// Server tells everyone the shared anchor point.
struct SpawnAssign {
    std::int32_t x = 0;
    std::int32_t y = 0;
    std::int32_t z = 0;
    std::uint16_t seat = 0;

    void Write(wire::Writer& w) const;
    static SpawnAssign Read(wire::Reader& r);
};
struct Snapshot {
    std::int32_t tick = 0;
    std::vector<PlayerSnapshot> players;

    void Write(wire::Writer& w) const;
    static Snapshot Read(wire::Reader& r);
};

/// Snapshot of one scene object (door / breakable). C→S (45) and S→C (153).
/// `key` is an opaque client-computed id (kind + quantized position hash); the server
/// only stores and relays it.
enum class SceneObjectKind : std::uint8_t {
    Door = 0,
    Breakable = 1,
    Cover = 2, // HalfObsticle (sandbag cover)
};

struct SceneObjectState {
    std::uint32_t key = 0;
    std::uint8_t kind = 0;  // SceneObjectKind
    std::uint8_t state = 0; // door: 0 closed / 1 open; breakable: 1 breaked
    std::uint32_t raidEpoch = 0;

    void Write(wire::Writer& w) const;
    static SceneObjectState Read(wire::Reader& r);
};

/// Explosion fact (C→S 46, S→C 193). Carries the native DamageInfo values so the server
/// can attribute damage to the following explosion-flagged hit events without a grenade
/// content table (demo scope).
struct ExplosionEvent {
    std::uint32_t explosionId = 0;
    std::int32_t x = 0;
    std::int32_t y = 0;
    std::int32_t z = 0;
    std::uint16_t radiusCm = 0;
    std::uint16_t damage = 0;
    std::uint32_t weaponId = 0;
    std::uint8_t sourceKind = 0; // 0 player, 1 ai
    std::uint32_t sourceId = 0;
    std::uint8_t fxType = 0;
    std::uint32_t raidEpoch = 0;

    void Write(wire::Writer& w) const;
    static ExplosionEvent Read(wire::Reader& r);
};

/// M2-v12 bullet impact fact (C→S 51 / S→C 201). Presentation only: the server validates
/// ownership/epoch/monotonic shotId and relays it; it never settles damage from this.
struct ImpactFx {
    std::uint32_t raidEpoch = 0;
    std::uint8_t sourceKind = 0; // 0 player, 1 ai
    std::uint32_t sourceId = 0;
    std::uint32_t weaponId = 0; // ItemTypeID of the firing weapon, 0 = unknown
    std::uint32_t shotId = 0;   // monotonic per (sourceKind, sourceId)
    std::int32_t x = 0;         // impact point * 100
    std::int32_t y = 0;
    std::int32_t z = 0;
    std::int16_t nx = 0;        // surface normal * 32767
    std::int16_t ny = 0;
    std::int16_t nz = 0;
    std::uint8_t flags = 0;     // bit0 blocked

    void Write(wire::Writer& w) const;
    static ImpactFx Read(wire::Reader& r);
};

/// Builds a full frame (header + opcode + payload).
std::vector<std::uint8_t> EncodeFrame(Op op, const wire::Header& header, const wire::Writer& payload);

} // namespace coop::msg

