#pragma once

// Thin helpers over tinyxml2 used by the content loader: file loading, element lookup
// and attribute parsing with one consistent fail-fast error format.

#include <cstdint>
#include <string>

#include "tinyxml2.h"

namespace coop::xml {

/// Loads one XML file; on failure fills `error` with "<path>: <parse error>".
bool LoadFile(const std::string& path, tinyxml2::XMLDocument& doc, std::string& error);

/// Returns the root element when it is named `rootName`, otherwise reports an error.
const tinyxml2::XMLElement* TableRoot(const tinyxml2::XMLDocument& doc, const std::string& path,
                                      const char* rootName, std::string& error);

/// Required string attribute; on failure fills `error` with "<where>: missing 'name'".
bool RequiredString(const tinyxml2::XMLElement* element, const char* name, std::string& out,
                    std::string& error, const std::string& where);

/// Required integer attributes; on failure `error` says which attribute is missing/invalid.
bool RequiredU32(const tinyxml2::XMLElement* element, const char* name, std::uint32_t& out,
                 std::string& error, const std::string& where);
bool RequiredU16(const tinyxml2::XMLElement* element, const char* name, std::uint16_t& out,
                 std::string& error, const std::string& where);
bool RequiredI32(const tinyxml2::XMLElement* element, const char* name, std::int32_t& out,
                 std::string& error, const std::string& where);

/// Optional attributes with defaults; invalid values are still a fail-fast error.
std::string OptionalString(const tinyxml2::XMLElement* element, const char* name,
                           const std::string& fallback);
bool OptionalU16(const tinyxml2::XMLElement* element, const char* name, std::uint16_t fallback,
                 std::uint16_t& out, std::string& error, const std::string& where);
bool OptionalU32(const tinyxml2::XMLElement* element, const char* name, std::uint32_t fallback,
                 std::uint32_t& out, std::string& error, const std::string& where);
bool OptionalI32(const tinyxml2::XMLElement* element, const char* name, std::int32_t fallback,
                 std::int32_t& out, std::string& error, const std::string& where);
bool OptionalDouble(const tinyxml2::XMLElement* element, const char* name, double fallback,
                    double& out, std::string& error, const std::string& where);

/// Table version attribute (defaults to 1 when absent).
bool OptionalVersion(const tinyxml2::XMLElement* root, std::uint32_t& out, std::string& error,
                     const std::string& where);

} // namespace coop::xml
