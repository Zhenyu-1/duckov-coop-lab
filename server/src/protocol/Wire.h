#pragma once

#include <cstddef>
#include <cstdint>
#include <string>
#include <vector>

namespace coop::wire {

inline constexpr std::uint8_t kProtocolVersion = 16;
inline constexpr std::size_t kHeaderSize = 20;
inline constexpr std::size_t kMaxPayload = 1200;

/// Contract-level client version (ADR-007). The frame layout stays `kProtocolVersion`;
/// this axis must be bumped with every contract change so N2 can reject stale clients.
/// Clients send `<product>/<semver>` with product `mod` or `bot`.
inline constexpr const char* kClientVersion = "2.18.0";

/// Result of comparing a client version string against `kClientVersion`.
enum class ClientVersionStatus : std::uint8_t {
    Ok,
    Older,
    Newer,
    Malformed,
};

/// Parses `<product>/<semver>`: product must be `mod`/`bot`, semver exactly `x.y.z`.
/// Returns false for empty strings, a missing/extra `/`, an unknown product or a
/// non-numeric / multi-part semver (e.g. `1.3`, `1.3.0.1`, `x.y.z`).
bool ParseClientVersion(const std::string& value, std::string& product, std::string& semver);

/// Strict comparison against the contract version; the direction tells the operator
/// which side is behind (`older`/`newer`) and `malformed` means "cannot even parse".
ClientVersionStatus ClassifyClientVersion(const std::string& value, const std::string& expected);

/// `ok` / `older` / `newer` / `malformed`, for the one-line rejection log.
const char* ClientVersionStatusText(ClientVersionStatus status);

enum class FrameType : std::uint8_t {
    Data = 0,
    AckOnly = 1,
    Handshake = 2,
    Fragment = 3,
    Ping = 4,
    Pong = 5,
    Close = 6,
};

struct Header {
    std::uint8_t version = kProtocolVersion;
    FrameType type = FrameType::Data;
    std::uint8_t channel = 0;
    std::uint8_t flags = 0;
    std::uint32_t sessionId = 0;
    std::uint32_t seq = 0;
    std::uint32_t ack = 0;
    std::uint32_t ackBits = 0;
};

void WriteHeader(std::vector<std::uint8_t>& out, const Header& header);
bool ReadHeader(const std::uint8_t* data, std::size_t size, Header& header);

/// Little-endian writer.
class Writer {
public:
    void U8(std::uint8_t v);
    void U16(std::uint16_t v);
    void U32(std::uint32_t v);
    void U64(std::uint64_t v);
    void I16(std::int16_t v);
    void I32(std::int32_t v);
    void I64(std::int64_t v);
    void F32(float v);
    void Str(const std::string& v);
    void Raw(const std::uint8_t* data, std::size_t size);

    const std::vector<std::uint8_t>& Bytes() const { return _buf; }
    std::vector<std::uint8_t>& Bytes() { return _buf; }
    void Clear() { _buf.clear(); }

private:
    std::vector<std::uint8_t> _buf;
};

/// Bounds-checked reader. On overflow Ok() becomes false and all reads return 0.
class Reader {
public:
    Reader(const std::uint8_t* data, std::size_t size) : _data(data), _size(size) {}

    std::uint8_t U8();
    std::uint16_t U16();
    std::uint32_t U32();
    std::uint64_t U64();
    std::int16_t I16();
    std::int32_t I32();
    std::int64_t I64();
    float F32();
    std::string Str();
    void Raw(std::uint8_t* out, std::size_t size);

    bool Ok() const { return _ok; }
    std::size_t Remaining() const { return _ok ? _size - _offset : 0; }

private:
    bool Need(std::size_t n);

    const std::uint8_t* _data;
    std::size_t _size;
    std::size_t _offset = 0;
    bool _ok = true;
};

} // namespace coop::wire
