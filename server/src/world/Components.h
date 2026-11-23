#pragma once

#include <array>
#include <cstdint>
#include <string>

#include "core/Ecs.h"

namespace coop {

struct Position {
    std::int32_t x = 0;
    std::int32_t y = 0;
    std::int32_t z = 0;
    std::uint16_t yaw = 0;
};

struct Health {
    std::int32_t hp = 100;
    std::int32_t maxHp = 100;
    bool alive = true;
};

struct PlayerComponent {
    std::uint16_t playerId = 0;
    std::uint64_t identity = 0; // stable account key for persistence
    std::uint32_t lastSeq = 0;
    bool hasInput = false;
    bool leftRaid = false;      // dead / gave up: ignore further spawn reports
    std::string scene; // last scene this player reported
    // A native in-raid teleporter (or a long hitch) can move the client by more than one
    // accepted step. After a few consecutive step rejections, accept the new position
    // once so the session can resync instead of permanently rejecting every input.
    std::uint8_t inputRejectStreak = 0;

    // B2/v5: equipment snapshot, slot -> itemId (0 = empty); 8/9 are totems.
    std::array<std::uint32_t, 10> loadout{};
    bool hasLoadout = false;

    // SSpawnAssign is a server-commanded teleport. Keep its target so the first
    // in-flight input from the old position cannot rewind the authoritative
    // Position (the stale input is dropped and the next near-target input wins).
    bool teleportPending = false;
    std::int32_t teleportX = 0;
    std::int32_t teleportY = 0;
    std::int32_t teleportZ = 0;
};

struct SessionRef {
    std::uint64_t endpointKey = 0;
};

struct PlayerProgress {
    bool extracted = false;
};

/// AI is simulated by a client ("simulator"); the server owns ownership + authoritative health.
struct AiTag {
    std::uint32_t aiId = 0;
    std::uint32_t monsterId = 0;   // server registry id (SAiSpawn v3)
    std::uint16_t ownerPlayerId = 0; // 0 = unassigned
    std::uint8_t state = 0;          // 0 dormant, 1 active, 2 dead
};

} // namespace coop
