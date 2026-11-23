#pragma once

#include <cstddef>
#include <cstdint>
#include <map>
#include <unordered_map>
#include <vector>

#include "protocol/Opcodes.h"
#include "protocol/Wire.h"

namespace coop::net {

struct Delivered {
    Op op = Op::CInput;
    std::vector<std::uint8_t> payload;
};

/// One logical channel with its own sequence space and reliability policy.
/// Large payloads are split into Fragment frames and reassembled on the other side.
class ReliableChannel {
public:
    static constexpr std::size_t kMaxChunk = 1100;
    static constexpr std::uint64_t kAssemblyTimeoutMs = 5000;

    ReliableChannel(std::uint8_t id, bool reliable, bool ordered);

    std::uint8_t Id() const { return _id; }
    bool IsReliable() const { return _reliable; }

    /// Build one or more frames (splits when payload > kMaxChunk).
    std::vector<std::vector<std::uint8_t>> BuildFrames(Op op, const std::vector<std::uint8_t>& payload,
                                                       std::uint32_t sessionId, std::uint64_t nowMs);

    /// Single-frame helper (no fragmentation).
    std::vector<std::uint8_t> BuildFrame(Op op, const std::vector<std::uint8_t>& payload,
                                         std::uint32_t sessionId, std::uint64_t nowMs,
                                         std::uint32_t forceSeq = 0);

    void OnFrame(Op op, const wire::Header& header, const std::uint8_t* payload, std::size_t size,
                 std::uint64_t nowMs, std::vector<Delivered>& out);

    void CollectRetransmits(std::uint32_t sessionId, std::uint64_t nowMs,
                            std::vector<std::vector<std::uint8_t>>& out);

    std::uint32_t RtoMs() const { return _rto; }
    std::size_t PendingCount() const { return _pending.size(); }

private:
    struct Pending {
        std::uint32_t seq = 0;
        wire::FrameType type = wire::FrameType::Data;
        Op op = Op::CInput;
        std::vector<std::uint8_t> payload;
        std::uint64_t sentAtMs = 0;
        int retries = 0;
    };

    struct Assembly {
        Op op = Op::CInput;
        std::uint16_t count = 0;
        std::uint16_t received = 0;
        std::uint64_t firstSeenMs = 0;
        std::map<std::uint16_t, std::vector<std::uint8_t>> chunks;
    };

    std::vector<std::uint8_t> BuildFragmentFrame(Op op, std::uint16_t messageId, std::uint16_t index,
                                                 std::uint16_t count, const std::uint8_t* chunk,
                                                 std::size_t chunkSize, std::uint32_t sessionId,
                                                 std::uint64_t nowMs);

    void ProcessAck(std::uint32_t ack, std::uint32_t ackBits, std::uint64_t nowMs);
    void UpdateRtt(std::uint64_t sampleMs);
    void DeliverOrdered(Op op, std::uint32_t seq, const std::uint8_t* payload, std::size_t size,
                        std::vector<Delivered>& out);
    void HandleFragment(Op op, const wire::Header& header, const std::uint8_t* payload, std::size_t size,
                        std::uint64_t nowMs, std::vector<Delivered>& out);
    void PruneAssemblies(std::uint64_t nowMs);

    std::uint8_t _id;
    bool _reliable;
    bool _ordered;

    std::uint32_t _nextSeq = 0;
    std::uint16_t _nextMessageId = 1;
    std::vector<Pending> _pending;

    std::uint32_t _recvNext = 1;
    std::uint32_t _lastUnreliableSeq = 0;
    std::uint32_t _lastReliableSeq = 0;
    std::map<std::uint32_t, Delivered> _recvBuffer;
    std::unordered_map<std::uint16_t, Assembly> _assemblies;

    std::uint32_t _ack = 0;
    std::uint32_t _ackBits = 0;

    double _srtt = 0.0;
    double _rttvar = 0.0;
    std::uint32_t _rto = 300;
    bool _hasRtt = false;
};

} // namespace coop::net
