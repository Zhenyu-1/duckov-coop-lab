#pragma once

#include <cstdint>

namespace coop {

/// Client -> server: 1..99, server -> client: 101..199, server push: 150..199.
/// Naming rule: C prefix = client sent, S prefix = server sent, reply shares the suffix.
enum class Op : std::uint8_t {
    CLogin = 1,
    CResume = 2,
    CJoin = 3,
    CInput = 4,
    CAnim = 5,
    CFire = 6,
    CHit = 7,
    CInteract = 8,
    CLoot = 9,
    CExtract = 10,
    CAiState = 11,
    CContainerView = 12,
    CStartRaid = 13,
    CPlayerSpawn = 14,
    CHitReport = 15,
    CLeaveRaid = 17,
    CLoadout = 18,
    CMirrorAck = 19,
    CHitEvent = 20,
    CAiAnim = 21,
    CAiLoadout = 22,
    CPlayerStats = 23,
    CShootFact = 24,
    CUseConsumable = 25,
    CAiAim = 26,
    CAiStateSample = 27,
    CAiLoadoutV7 = 28,
    CAiShot = 29,
    CPlayerStateSample = 30,
    CPlayerLoadoutV8 = 31,
    CPlayerShot = 32,
    CPlayerFaceV8 = 33,
    // v9 item contract (SPEC-2026-11-13-item-protocol-v9).
    CItemTransfer = 34,
    CContainerOpen = 35,
    CWorldItemPickup = 36,
    CWorldItemDrop = 37,
    CInventorySync = 38,
    // P1 item management (contract 2.2.0): server-side sort/merge/reorder.
    CItemArrange = 39,
    CItemConsume = 40,
    CItemReload = 41,
    CItemWeaponFire = 42,
    CItemDurability = 43,
    CBackpackCapacity = 44,
    // M2-v10 scene objects + explosion facts.
    CSceneObjectState = 45,
    CExplosion = 46,
    // M2-v12 raid lobby: per-player ready state and the host's force reset.
    CPlayerReady = 47,
    CForceResetRaid = 48,
    /// Contract 2.15.0: pull an attachment off a server-owned item and turn it into a normal
    /// backpack item (so it can be dropped/picked up like any other loot).
    CItemModDetach = 49,
    /// Contract 2.17.0: plug one backpack item into an attachment slot of a server-owned item.
    CItemModAttach = 50,
    /// Contract 2.18.0 (M2-v12): presentation-only bullet impact fact (world hit).
    CImpactFx = 51,

    SLogin = 101,
    SResume = 102,
    SJoin = 103,
    SFire = 106,
    SHit = 107,
    SInteract = 108,
    SLoot = 109,
    SExtract = 110,

    SSnapshot = 150,
    SAiState = 151,
    SPlayerEvent = 152,
    SObjectState = 153,
    SAiEvent = 154,
    SKick = 155,
    SShutdown = 156,
    SRaidPhase = 157,
    SContainerView = 158,
    SRaidStart = 159,
    SSpawnAssign = 160,
    SCombatEvent = 161,
    SAiSpawn = 162,
    SAiTarget = 163,
    SAiDespawn = 164,
    SAiAnim = 165,
    SAiHealth = 166,
    SPlayerBuff = 167,
    SRaidRules = 168,
    SAiLoadout = 169,
    SAiAim = 170,
    SAiStateSample = 171,
    SAiLoadoutV7 = 172,
    SAiShot = 173,
    SPlayerStateSample = 174,
    SPlayerLoadoutV8 = 175,
    SPlayerShot = 176,
    SPlayerFaceV8 = 177,
    // v9 item contract (SPEC-2026-11-13-item-protocol-v9).
    SInitialInventory = 178,
    SItemTransfer = 179,
    SContainerContents = 180,
    SWorldItemPickup = 181,
    SWorldItemDrop = 182,
    SWorldItemSnapshot = 183,
    SWorldItemSpawn = 184,
    SWorldItemDespawn = 185,
    SInventorySnapshot = 186,
    // P1 item management (contract 2.2.0).
    SItemArrange = 187,
    SItemConsume = 188,
    // P3/D6 corpse containers (contract 2.3.0).
    SCorpseSpawn = 189,
    SCorpseDespawn = 190,
    // P1 phase 3 item detail side channel (contract 2.4.0).
    SItemDetail = 191,
    SItemReloadResult = 192,
    // M2-v10 explosion broadcast (presentation for non-throwers).
    SExplosion = 193,
    SRespawn = 194,
    // M2-v12 raid lobby: ready-state broadcast, force-reset notice and control ack.
    SPlayerReady = 195,
    SForceResetRaid = 196,
    SRaidControlResult = 197,
    // Loadout attachment side channel (contract 2.14.0 / wire 12).
    SItemMods = 198,
    /// Contract 2.15.0: result of CItemModDetach (uid of the new backpack item on success).
    SItemModDetachResult = 199,
    /// Contract 2.17.0: result of CItemModAttach (the attached item id, 0 on failure).
    SItemModAttachResult = 200,
    /// Contract 2.18.0 (M2-v12): server-relayed bullet impact fact (no gameplay effect).
    SImpactFx = 201,
    SLoadout = 118,
    SMirror = 119,
};

enum class ErrorCode : std::uint8_t {
    Ok = 0,
    InvalidArgument = 1,
    Unauthorized = 2,
    VersionMismatch = 3,
    RoomFull = 4,
    NotInRaid = 5,
    Conflict = 6,
    IdempotentReplay = 7,
    ServerBusy = 8,
    /// A1: the account logged in from another connection; this session is fenced out.
    SessionReplaced = 9,
    /// Content tables (monsters/spawn/scenes/items) do not match the server.
    ContentMismatch = 10,
};

} // namespace coop
