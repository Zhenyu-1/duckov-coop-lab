#include "content/ContentHash.h"

#include <algorithm>
#include <cstdint>
#include <cstring>
#include <vector>

namespace coop {

namespace {

void AppendCanonical(const tinyxml2::XMLElement* element, std::string& out)
{
    // Element names and attribute values are kept verbatim; only ordering and whitespace
    // are normalised. Content tables use attributes only, so text nodes are ignored.
    out += '<';
    out += element->Name();

    std::vector<std::pair<std::string, std::string>> attributes;
    for (const tinyxml2::XMLAttribute* attr = element->FirstAttribute(); attr != nullptr;
         attr = attr->Next()) {
        attributes.emplace_back(attr->Name(), attr->Value() != nullptr ? attr->Value() : "");
    }
    std::sort(attributes.begin(), attributes.end());

    for (const auto& [name, value] : attributes) {
        out += '|';
        out += name;
        out += '=';
        out += value;
    }
    out += '>';

    std::vector<std::string> children;
    for (const tinyxml2::XMLElement* child = element->FirstChildElement(); child != nullptr;
         child = child->NextSiblingElement()) {
        std::string encoded;
        AppendCanonical(child, encoded);
        children.push_back(std::move(encoded));
    }
    std::sort(children.begin(), children.end());
    for (const auto& child : children)
        out += child;

    out += "</";
    out += element->Name();
    out += '>';
}

std::string ToHex(const unsigned char* bytes, std::size_t size)
{
    static const char* digits = "0123456789abcdef";
    std::string out;
    out.reserve(size * 2);
    for (std::size_t i = 0; i < size; ++i) {
        out.push_back(digits[bytes[i] >> 4]);
        out.push_back(digits[bytes[i] & 0x0F]);
    }
    return out;
}

// Portable SHA-256 (FIPS 180-4). The old Windows-only bcrypt path made the Linux build
// return an empty hash, which breaks the login content-hash check.
constexpr std::uint32_t kSha256Round[64] = {
    0x428a2f98u, 0x71374491u, 0xb5c0fbcfu, 0xe9b5dba5u, 0x3956c25bu, 0x59f111f1u, 0x923f82a4u,
    0xab1c5ed5u, 0xd807aa98u, 0x12835b01u, 0x243185beu, 0x550c7dc3u, 0x72be5d74u, 0x80deb1feu,
    0x9bdc06a7u, 0xc19bf174u, 0xe49b69c1u, 0xefbe4786u, 0x0fc19dc6u, 0x240ca1ccu, 0x2de92c6fu,
    0x4a7484aau, 0x5cb0a9dcu, 0x76f988dau, 0x983e5152u, 0xa831c66du, 0xb00327c8u, 0xbf597fc7u,
    0xc6e00bf3u, 0xd5a79147u, 0x06ca6351u, 0x14292967u, 0x27b70a85u, 0x2e1b2138u, 0x4d2c6dfcu,
    0x53380d13u, 0x650a7354u, 0x766a0abbu, 0x81c2c92eu, 0x92722c85u, 0xa2bfe8a1u, 0xa81a664bu,
    0xc24b8b70u, 0xc76c51a3u, 0xd192e819u, 0xd6990624u, 0xf40e3585u, 0x106aa070u, 0x19a4c116u,
    0x1e376c08u, 0x2748774cu, 0x34b0bcb5u, 0x391c0cb3u, 0x4ed8aa4au, 0x5b9cca4fu, 0x682e6ff3u,
    0x748f82eeu, 0x78a5636fu, 0x84c87814u, 0x8cc70208u, 0x90befffau, 0xa4506cebu, 0xbef9a3f7u,
    0xc67178f2u,
};

std::uint32_t RotateRight(std::uint32_t value, int bits)
{
    return (value >> bits) | (value << (32 - bits));
}

void Sha256Transform(std::uint32_t state[8], const unsigned char block[64])
{
    std::uint32_t w[64];
    for (int i = 0; i < 16; ++i) {
        w[i] = (static_cast<std::uint32_t>(block[i * 4]) << 24) |
               (static_cast<std::uint32_t>(block[i * 4 + 1]) << 16) |
               (static_cast<std::uint32_t>(block[i * 4 + 2]) << 8) |
               static_cast<std::uint32_t>(block[i * 4 + 3]);
    }
    for (int i = 16; i < 64; ++i) {
        const std::uint32_t s0 =
            RotateRight(w[i - 15], 7) ^ RotateRight(w[i - 15], 18) ^ (w[i - 15] >> 3);
        const std::uint32_t s1 =
            RotateRight(w[i - 2], 17) ^ RotateRight(w[i - 2], 19) ^ (w[i - 2] >> 10);
        w[i] = w[i - 16] + s0 + w[i - 7] + s1;
    }

    std::uint32_t a = state[0];
    std::uint32_t b = state[1];
    std::uint32_t c = state[2];
    std::uint32_t d = state[3];
    std::uint32_t e = state[4];
    std::uint32_t f = state[5];
    std::uint32_t g = state[6];
    std::uint32_t h = state[7];
    for (int i = 0; i < 64; ++i) {
        const std::uint32_t s1 = RotateRight(e, 6) ^ RotateRight(e, 11) ^ RotateRight(e, 25);
        const std::uint32_t ch = (e & f) ^ (~e & g);
        const std::uint32_t temp1 = h + s1 + ch + kSha256Round[i] + w[i];
        const std::uint32_t s0 = RotateRight(a, 2) ^ RotateRight(a, 13) ^ RotateRight(a, 22);
        const std::uint32_t maj = (a & b) ^ (a & c) ^ (b & c);
        const std::uint32_t temp2 = s0 + maj;
        h = g;
        g = f;
        f = e;
        e = d + temp1;
        d = c;
        c = b;
        b = a;
        a = temp1 + temp2;
    }
    state[0] += a;
    state[1] += b;
    state[2] += c;
    state[3] += d;
    state[4] += e;
    state[5] += f;
    state[6] += g;
    state[7] += h;
}

void Sha256Digest(const void* data, std::size_t size, unsigned char digest[32])
{
    std::uint32_t state[8] = {0x6a09e667u, 0xbb67ae85u, 0x3c6ef372u, 0xa54ff53au,
                              0x510e527fu, 0x9b05688cu, 0x1f83d9abu, 0x5be0cd19u};
    const auto* bytes = static_cast<const unsigned char*>(data);
    std::size_t offset = 0;
    while (size - offset >= 64) {
        Sha256Transform(state, bytes + offset);
        offset += 64;
    }

    unsigned char block[128] = {};
    const std::size_t rest = size - offset;
    if (rest != 0)
        std::memcpy(block, bytes + offset, rest);
    block[rest] = 0x80;

    const std::size_t padded = rest < 56 ? 64 : 128;
    const std::uint64_t bits = static_cast<std::uint64_t>(size) * 8u;
    for (int i = 0; i < 8; ++i)
        block[padded - 1 - i] = static_cast<unsigned char>(bits >> (8 * i));

    Sha256Transform(state, block);
    if (padded == 128)
        Sha256Transform(state, block + 64);

    for (int i = 0; i < 8; ++i) {
        digest[i * 4] = static_cast<unsigned char>(state[i] >> 24);
        digest[i * 4 + 1] = static_cast<unsigned char>(state[i] >> 16);
        digest[i * 4 + 2] = static_cast<unsigned char>(state[i] >> 8);
        digest[i * 4 + 3] = static_cast<unsigned char>(state[i]);
    }
}

} // namespace

std::string Sha256Hex(const void* data, std::size_t size)
{
    unsigned char digest[32] = {};
    Sha256Digest(data, size, digest);
    return ToHex(digest, sizeof(digest));
}

std::string CanonicalTableHash(const tinyxml2::XMLDocument& doc)
{
    const tinyxml2::XMLElement* root = doc.RootElement();
    if (root == nullptr)
        return {};

    std::string canonical;
    AppendCanonical(root, canonical);
    return Sha256Hex(canonical.data(), canonical.size());
}

std::string MissingTableHash(const std::string& table)
{
    const std::string marker = "missing:" + table;
    return Sha256Hex(marker.data(), marker.size());
}

std::string CombineTableHashes(const std::map<std::string, std::string>& tableHashes)
{
    std::string canonical;
    for (const auto& [table, hash] : tableHashes) {
        canonical += table;
        canonical += '=';
        canonical += hash;
        canonical += '\n';
    }
    return Sha256Hex(canonical.data(), canonical.size());
}

} // namespace coop
