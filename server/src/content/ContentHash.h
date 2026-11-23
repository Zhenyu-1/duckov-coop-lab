#pragma once

// Content hashing (ADR-014): every table is reduced to a canonical byte string (attributes
// and child elements sorted, whitespace folded) and hashed with SHA-256, so an equivalent
// XML file always produces the same hash. The combined hash is the SHA-256 of the sorted
// "<table>=<hash>" lines; both ends of the wire can compare it.

#include <cstddef>
#include <map>
#include <string>

#include "tinyxml2.h"

namespace coop {

/// Lower-case hex SHA-256 of the given bytes (portable FIPS 180-4 implementation).
std::string Sha256Hex(const void* data, std::size_t size);

/// SHA-256 of one XML document's canonical form.
std::string CanonicalTableHash(const tinyxml2::XMLDocument& doc);

/// Hash used when a table file is absent, so "missing table" still changes the combined hash.
std::string MissingTableHash(const std::string& table);

/// SHA-256 over the sorted "<table>=<hash>" lines.
std::string CombineTableHashes(const std::map<std::string, std::string>& tableHashes);

} // namespace coop
