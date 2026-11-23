#pragma once

#include <cstdint>

#include "core/Ecs.h"

namespace coop {

enum class EventType {
    InputRejected,
    PlayerDamaged,
    PlayerDied,
    AiDied,
    AiSyncRejected,
};

struct WorldEvent {
    EventType type = EventType::InputRejected;
    Entity entity = kInvalidEntity;
    std::uint16_t playerId = 0;
    std::int32_t value = 0;
};

} // namespace coop
