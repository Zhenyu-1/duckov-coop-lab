#include "protocol/Wire.h"

#include <cstring>

namespace coop::wire {

namespace {

void PutU16(std::vector<std::uint8_t>& out, std::uint16_t v)
{
    out.push_back(static_cast<std::uint8_t>(v & 0xFF));
    out.push_back(static_cast<std::uint8_t>((v >> 8) & 0xFF));
}

void PutU32(std::vector<std::uint8_t>& out, std::uint32_t v)
{
    for (int i = 0; i < 4; ++i)
        out.push_back(static_cast<std::uint8_t>((v >> (8 * i)) & 0xFF));
}

std::uint16_t GetU16(const std::uint8_t* p)
{
    return static_cast<std::uint16_t>(p[0] | (p[1] << 8));
}

std::uint32_t GetU32(const std::uint8_t* p)
{
    return static_cast<std::uint32_t>(p[0])
         | (static_cast<std::uint32_t>(p[1]) << 8)
         | (static_cast<std::uint32_t>(p[2]) << 16)
         | (static_cast<std::uint32_t>(p[3]) << 24);
}

// ---------------- client version (N2 / ADR-007) ----------------

constexpr std::size_t kMaxClientVersionLength = 32;
constexpr std::size_t kMaxVersionSegmentDigits = 5;

bool IsDigits(const std::string& text)
{
    if (text.empty() || text.size() > kMaxVersionSegmentDigits)
        return false;
    for (const char c : text) {
        if (c < '0' || c > '9')
            return false;
    }
    return true;
}

struct Semver {
    int major = 0;
    int minor = 0;
    int patch = 0;
};

/// Exact `x.y.z`: three numeric segments, nothing else.
bool ParseSemver(const std::string& semver, Semver& out)
{
    if (semver.empty() || semver.size() > kMaxClientVersionLength)
        return false;

    int parts[3] = {0, 0, 0};
    int parsed = 0;
    std::size_t start = 0;

    for (std::size_t i = 0; i <= semver.size(); ++i) {
        if (i < semver.size() && semver[i] != '.')
            continue;
        if (parsed >= 3)
            return false; // `1.3.0.1` is not a semver
        const std::string segment = semver.substr(start, i - start);
        if (!IsDigits(segment))
            return false;
        int value = 0;
        for (const char c : segment)
            value = value * 10 + (c - '0');
        parts[parsed++] = value;
        start = i + 1;
    }

    if (parsed != 3)
        return false;

    out = Semver{parts[0], parts[1], parts[2]};
    return true;
}

int CompareSemver(const Semver& lhs, const Semver& rhs)
{
    if (lhs.major != rhs.major)
        return lhs.major < rhs.major ? -1 : 1;
    if (lhs.minor != rhs.minor)
        return lhs.minor < rhs.minor ? -1 : 1;
    if (lhs.patch != rhs.patch)
        return lhs.patch < rhs.patch ? -1 : 1;
    return 0;
}

} // namespace

bool ParseClientVersion(const std::string& value, std::string& product, std::string& semver)
{
    product.clear();
    semver.clear();

    if (value.empty() || value.size() > kMaxClientVersionLength)
        return false;

    const auto slash = value.find('/');
    if (slash == std::string::npos)
        return false;
    if (value.find('/', slash + 1) != std::string::npos)
        return false;

    const std::string candidate = value.substr(0, slash);
    if (candidate != "mod" && candidate != "bot")
        return false;

    const std::string candidateSemver = value.substr(slash + 1);
    Semver parsed;
    if (!ParseSemver(candidateSemver, parsed))
        return false;

    product = candidate;
    semver = candidateSemver;
    return true;
}

ClientVersionStatus ClassifyClientVersion(const std::string& value, const std::string& expected)
{
    std::string product;
    std::string semver;
    if (!ParseClientVersion(value, product, semver))
        return ClientVersionStatus::Malformed;

    Semver got;
    Semver want;
    if (!ParseSemver(semver, got) || !ParseSemver(expected, want))
        return ClientVersionStatus::Malformed;

    const int order = CompareSemver(got, want);
    if (order == 0)
        return ClientVersionStatus::Ok;
    return order < 0 ? ClientVersionStatus::Older : ClientVersionStatus::Newer;
}

const char* ClientVersionStatusText(ClientVersionStatus status)
{
    switch (status) {
    case ClientVersionStatus::Ok:
        return "ok";
    case ClientVersionStatus::Older:
        return "older";
    case ClientVersionStatus::Newer:
        return "newer";
    case ClientVersionStatus::Malformed:
        return "malformed";
    }
    return "malformed";
}

void WriteHeader(std::vector<std::uint8_t>& out, const Header& h)
{
    out.push_back(h.version);
    out.push_back(static_cast<std::uint8_t>(h.type));
    out.push_back(h.channel);
    out.push_back(h.flags);
    PutU32(out, h.sessionId);
    PutU32(out, h.seq);
    PutU32(out, h.ack);
    PutU32(out, h.ackBits);
}

bool ReadHeader(const std::uint8_t* data, std::size_t size, Header& out)
{
    if (data == nullptr || size < kHeaderSize)
        return false;

    out.version = data[0];
    out.type = static_cast<FrameType>(data[1]);
    out.channel = data[2];
    out.flags = data[3];
    out.sessionId = GetU32(data + 4);
    out.seq = GetU32(data + 8);
    out.ack = GetU32(data + 12);
    out.ackBits = GetU32(data + 16);
    return out.version == kProtocolVersion;
}

// ---------------- Writer ----------------

void Writer::U8(std::uint8_t v) { _buf.push_back(v); }

void Writer::U16(std::uint16_t v) { PutU16(_buf, v); }

void Writer::U32(std::uint32_t v) { PutU32(_buf, v); }

void Writer::U64(std::uint64_t v)
{
    PutU32(_buf, static_cast<std::uint32_t>(v & 0xFFFFFFFFu));
    PutU32(_buf, static_cast<std::uint32_t>((v >> 32) & 0xFFFFFFFFu));
}

void Writer::I16(std::int16_t v) { PutU16(_buf, static_cast<std::uint16_t>(v)); }

void Writer::I32(std::int32_t v) { PutU32(_buf, static_cast<std::uint32_t>(v)); }

void Writer::I64(std::int64_t v) { U64(static_cast<std::uint64_t>(v)); }

void Writer::F32(float v)
{
    std::uint32_t bits = 0;
    std::memcpy(&bits, &v, sizeof(bits));
    PutU32(_buf, bits);
}

void Writer::Str(const std::string& v)
{
    if (v.size() > 0xFFFF)
        return;
    PutU16(_buf, static_cast<std::uint16_t>(v.size()));
    _buf.insert(_buf.end(), v.begin(), v.end());
}

void Writer::Raw(const std::uint8_t* data, std::size_t size)
{
    if (data != nullptr && size > 0)
        _buf.insert(_buf.end(), data, data + size);
}

// ---------------- Reader ----------------

bool Reader::Need(std::size_t n)
{
    if (!_ok || _offset + n > _size) {
        _ok = false;
        return false;
    }
    return true;
}

std::uint8_t Reader::U8()
{
    if (!Need(1))
        return 0;
    return _data[_offset++];
}

std::uint16_t Reader::U16()
{
    if (!Need(2))
        return 0;
    const std::uint16_t v = GetU16(_data + _offset);
    _offset += 2;
    return v;
}

std::uint32_t Reader::U32()
{
    if (!Need(4))
        return 0;
    const std::uint32_t v = GetU32(_data + _offset);
    _offset += 4;
    return v;
}

std::uint64_t Reader::U64()
{
    const std::uint64_t lo = U32();
    const std::uint64_t hi = U32();
    return lo | (hi << 32);
}

std::int16_t Reader::I16() { return static_cast<std::int16_t>(U16()); }

std::int32_t Reader::I32() { return static_cast<std::int32_t>(U32()); }

std::int64_t Reader::I64() { return static_cast<std::int64_t>(U64()); }

float Reader::F32()
{
    const std::uint32_t bits = U32();
    float v = 0.0f;
    std::memcpy(&v, &bits, sizeof(v));
    return v;
}

std::string Reader::Str()
{
    const std::uint16_t len = U16();
    if (!Need(len))
        return {};
    std::string s(reinterpret_cast<const char*>(_data + _offset), len);
    _offset += len;
    return s;
}

void Reader::Raw(std::uint8_t* out, std::size_t size)
{
    if (!Need(size)) {
        if (out != nullptr)
            std::fill(out, out + size, 0);
        return;
    }
    if (out != nullptr)
        std::copy(_data + _offset, _data + _offset + size, out);
    _offset += size;
}

} // namespace coop::wire
