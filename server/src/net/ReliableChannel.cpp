#include "net/ReliableChannel.h"

#include <algorithm>

namespace coop::net {

namespace {

constexpr std::uint32_t kMinRto = 100;
constexpr std::uint32_t kMaxRto = 2000;
constexpr int kMaxRetries = 10;

// fragment payload (after the opcode byte): [messageId u16][index u16][count u16][reserved u16][chunk]
constexpr std::size_t kFragmentHeader = 8;

} // namespace

ReliableChannel::ReliableChannel(std::uint8_t id, bool reliable, bool ordered)
    : _id(id), _reliable(reliable), _ordered(ordered)
{
}

std::vector<std::uint8_t> ReliableChannel::BuildFrame(Op op, const std::vector<std::uint8_t>& payload,
                                                      std::uint32_t sessionId, std::uint64_t nowMs,
                                                      std::uint32_t forceSeq)
{
    (void)forceSeq;
    auto frames = BuildFrames(op, payload, sessionId, nowMs);
    if (frames.empty())
        return {};
    return std::move(frames.front());
}

std::vector<std::uint8_t> ReliableChannel::BuildFragmentFrame(Op op, std::uint16_t messageId,
                                                              std::uint16_t index, std::uint16_t count,
                                                              const std::uint8_t* chunk, std::size_t chunkSize,
                                                              std::uint32_t sessionId, std::uint64_t nowMs)
{
    const std::uint32_t seq = ++_nextSeq;

    wire::Header header;
    header.type = wire::FrameType::Fragment;
    header.channel = _id;
    header.sessionId = sessionId;
    header.seq = seq;
    header.ack = _ack;
    header.ackBits = _ackBits;

    std::vector<std::uint8_t> frame;
    wire::WriteHeader(frame, header);
    frame.push_back(static_cast<std::uint8_t>(op));
    frame.push_back(static_cast<std::uint8_t>(messageId & 0xFF));
    frame.push_back(static_cast<std::uint8_t>(messageId >> 8));
    frame.push_back(static_cast<std::uint8_t>(index & 0xFF));
    frame.push_back(static_cast<std::uint8_t>(index >> 8));
    frame.push_back(static_cast<std::uint8_t>(count & 0xFF));
    frame.push_back(static_cast<std::uint8_t>(count >> 8));
    frame.push_back(0);
    frame.push_back(0);
    frame.insert(frame.end(), chunk, chunk + chunkSize);

    if (_reliable)
        _pending.push_back(Pending{seq, wire::FrameType::Fragment, op,
                                   std::vector<std::uint8_t>(frame.begin() + wire::kHeaderSize, frame.end()),
                                   nowMs, 0});

    return frame;
}

std::vector<std::vector<std::uint8_t>> ReliableChannel::BuildFrames(Op op,
                                                                     const std::vector<std::uint8_t>& payload,
                                                                     std::uint32_t sessionId, std::uint64_t nowMs)
{
    std::vector<std::vector<std::uint8_t>> frames;

    if (payload.size() + 1 <= kMaxChunk) {
        const std::uint32_t seq = ++_nextSeq;

        wire::Header header;
        header.type = wire::FrameType::Data;
        header.channel = _id;
        header.sessionId = sessionId;
        header.seq = seq;
        header.ack = _ack;
        header.ackBits = _ackBits;

        std::vector<std::uint8_t> frame;
        wire::WriteHeader(frame, header);
        frame.push_back(static_cast<std::uint8_t>(op));
        frame.insert(frame.end(), payload.begin(), payload.end());

        if (_reliable)
            _pending.push_back(Pending{seq, wire::FrameType::Data, op, payload, nowMs, 0});

        frames.push_back(std::move(frame));
        return frames;
    }

    // Fragmentation is only supported on unordered channels: on an ordered channel the
    // per-fragment sequence numbers would conflict with in-order delivery of messages.
    if (_ordered)
        return frames;

    const std::size_t count = (payload.size() + kMaxChunk - 1) / kMaxChunk;
    if (count > 0xFFFF)
        return frames;

    const std::uint16_t messageId = _nextMessageId++;

    for (std::size_t i = 0; i < count; ++i) {
        const std::size_t offset = i * kMaxChunk;
        const std::size_t size = std::min(kMaxChunk, payload.size() - offset);
        frames.push_back(BuildFragmentFrame(op, messageId, static_cast<std::uint16_t>(i),
                                            static_cast<std::uint16_t>(count),
                                            payload.data() + offset, size, sessionId, nowMs));
    }

    return frames;
}

void ReliableChannel::UpdateRtt(std::uint64_t sampleMs)
{
    const double sample = static_cast<double>(sampleMs);
    if (!_hasRtt) {
        _srtt = sample;
        _rttvar = sample / 2.0;
        _hasRtt = true;
    } else {
        _rttvar = 0.75 * _rttvar + 0.25 * std::abs(_srtt - sample);
        _srtt = 0.875 * _srtt + 0.125 * sample;
    }

    const double rto = _srtt + 4.0 * _rttvar;
    _rto = static_cast<std::uint32_t>(std::clamp(rto, static_cast<double>(kMinRto), static_cast<double>(kMaxRto)));
}

void ReliableChannel::ProcessAck(std::uint32_t ack, std::uint32_t ackBits, std::uint64_t nowMs)
{
    if (ack == 0)
        return;

    for (auto it = _pending.begin(); it != _pending.end();) {
        bool acked = false;
        if (it->seq == ack) {
            acked = true;
        } else if (it->seq < ack && ack - it->seq <= 32) {
            const std::uint32_t bit = ack - it->seq - 1;
            acked = (ackBits & (1u << bit)) != 0;
        }

        if (acked) {
            if (nowMs >= it->sentAtMs)
                UpdateRtt(nowMs - it->sentAtMs);
            it = _pending.erase(it);
        } else {
            ++it;
        }
    }
}

void ReliableChannel::HandleFragment(Op op, const wire::Header& header, const std::uint8_t* payload,
                                     std::size_t size, std::uint64_t nowMs, std::vector<Delivered>& out)
{
    if (size < kFragmentHeader)
        return;

    const std::uint16_t messageId = static_cast<std::uint16_t>(payload[0] | (payload[1] << 8));
    const std::uint16_t index = static_cast<std::uint16_t>(payload[2] | (payload[3] << 8));
    const std::uint16_t count = static_cast<std::uint16_t>(payload[4] | (payload[5] << 8));

    if (count == 0 || index >= count)
        return;

    auto& assembly = _assemblies[messageId];
    if (assembly.count == 0) {
        assembly.count = count;
        assembly.op = op;
        assembly.firstSeenMs = nowMs;
    }

    if (assembly.chunks.find(index) == assembly.chunks.end()) {
        assembly.chunks[index] = std::vector<std::uint8_t>(payload + kFragmentHeader, payload + size);
        assembly.received++;
    }

    if (assembly.received == assembly.count) {
        Delivered message;
        message.op = assembly.op;
        for (std::uint16_t i = 0; i < assembly.count; ++i) {
            const auto it = assembly.chunks.find(i);
            if (it == assembly.chunks.end()) {
                _assemblies.erase(messageId);
                return;
            }
            message.payload.insert(message.payload.end(), it->second.begin(), it->second.end());
        }

        _assemblies.erase(messageId);

        if (!_reliable) {
            if (header.seq > _lastUnreliableSeq) {
                _lastUnreliableSeq = header.seq;
                out.push_back(std::move(message));
            }
            return;
        }

        if (_ordered)
            DeliverOrdered(message.op, header.seq, message.payload.data(), message.payload.size(), out);
        else
            out.push_back(std::move(message));
    }
}

void ReliableChannel::PruneAssemblies(std::uint64_t nowMs)
{
    for (auto it = _assemblies.begin(); it != _assemblies.end();) {
        if (nowMs - it->second.firstSeenMs > kAssemblyTimeoutMs)
            it = _assemblies.erase(it);
        else
            ++it;
    }
}

void ReliableChannel::DeliverOrdered(Op op, std::uint32_t seq, const std::uint8_t* payload,
                                     std::size_t size, std::vector<Delivered>& out)
{
    if (seq < _recvNext)
        return;

    if (seq == _recvNext) {
        out.push_back(Delivered{op, std::vector<std::uint8_t>(payload, payload + size)});
        ++_recvNext;

        for (auto it = _recvBuffer.begin(); it != _recvBuffer.end();) {
            if (it->first != _recvNext)
                break;
            out.push_back(std::move(it->second));
            it = _recvBuffer.erase(it);
            ++_recvNext;
        }
        return;
    }

    if (_recvBuffer.size() < 256 && _recvBuffer.find(seq) == _recvBuffer.end())
        _recvBuffer.emplace(seq, Delivered{op, std::vector<std::uint8_t>(payload, payload + size)});
}

void ReliableChannel::OnFrame(Op op, const wire::Header& header, const std::uint8_t* payload,
                              std::size_t size, std::uint64_t nowMs, std::vector<Delivered>& out)
{
    ProcessAck(header.ack, header.ackBits, nowMs);
    PruneAssemblies(nowMs);

    if (header.seq > 0) {
        if (header.seq > _ack) {
            const std::uint32_t shift = header.seq - _ack;
            _ackBits = shift >= 32 ? 0u : (_ackBits << shift);
            if (shift >= 1 && shift <= 32)
                _ackBits |= 1u << (shift - 1);
            _ack = header.seq;
        } else if (_ack - header.seq <= 32) {
            _ackBits |= 1u << (_ack - header.seq - 1);
        }
    }

    if (header.seq == 0)
        return;

    if (header.type == wire::FrameType::Fragment) {
        HandleFragment(op, header, payload, size, nowMs, out);
        return;
    }

    if (!_reliable) {
        if (header.seq > _lastUnreliableSeq) {
            _lastUnreliableSeq = header.seq;
            out.push_back(Delivered{op, std::vector<std::uint8_t>(payload, payload + size)});
        }
        return;
    }

    if (_ordered) {
        DeliverOrdered(op, header.seq, payload, size, out);
        return;
    }

    if (header.seq <= _lastReliableSeq)
        return;

    _lastReliableSeq = header.seq;
    out.push_back(Delivered{op, std::vector<std::uint8_t>(payload, payload + size)});
}

void ReliableChannel::CollectRetransmits(std::uint32_t sessionId, std::uint64_t nowMs,
                                         std::vector<std::vector<std::uint8_t>>& out)
{
    if (!_reliable)
        return;

    for (auto it = _pending.begin(); it != _pending.end();) {
        if (nowMs - it->sentAtMs < _rto) {
            ++it;
            continue;
        }

        if (++it->retries > kMaxRetries) {
            it = _pending.erase(it);
            continue;
        }

        it->sentAtMs = nowMs;

        wire::Header header;
        header.type = it->type;
        header.channel = _id;
        header.sessionId = sessionId;
        header.seq = it->seq;
        header.ack = _ack;
        header.ackBits = _ackBits;

        std::vector<std::uint8_t> frame;
        wire::WriteHeader(frame, header);
        if (it->type == wire::FrameType::Data)
            frame.push_back(static_cast<std::uint8_t>(it->op));
        frame.insert(frame.end(), it->payload.begin(), it->payload.end());

        out.push_back(std::move(frame));
        ++it;
    }
}

} // namespace coop::net
