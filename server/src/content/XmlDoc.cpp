#include "content/XmlDoc.h"

#include <cerrno>
#include <cstdlib>
#include <limits>

namespace coop::xml {

namespace {

/// One numeric parse shared by every integer helper.
bool ParseInt64(const tinyxml2::XMLElement* element, const char* name, std::int64_t& out)
{
    const char* text = element->Attribute(name);
    if (text == nullptr || text[0] == '\0')
        return false;

    errno = 0;
    char* end = nullptr;
    const long long value = std::strtoll(text, &end, 10);
    if (errno != 0 || end == nullptr || *end != '\0')
        return false;

    out = static_cast<std::int64_t>(value);
    return true;
}

bool ParseDouble(const tinyxml2::XMLElement* element, const char* name, double& out)
{
    const char* text = element->Attribute(name);
    if (text == nullptr || text[0] == '\0')
        return false;

    errno = 0;
    char* end = nullptr;
    const double value = std::strtod(text, &end);
    if (errno != 0 || end == nullptr || *end != '\0')
        return false;

    out = value;
    return true;
}

std::string Missing(const std::string& where, const char* name, const char* why)
{
    return where + ": " + why + " '" + name + "'";
}

} // namespace

bool LoadFile(const std::string& path, tinyxml2::XMLDocument& doc, std::string& error)
{
    if (doc.LoadFile(path.c_str()) != tinyxml2::XML_SUCCESS) {
        const char* detail = doc.ErrorStr();
        error = path + ": " + (detail != nullptr ? detail : "XML parse error");
        return false;
    }
    return true;
}

const tinyxml2::XMLElement* TableRoot(const tinyxml2::XMLDocument& doc, const std::string& path,
                                      const char* rootName, std::string& error)
{
    const tinyxml2::XMLElement* root = doc.RootElement();
    if (root == nullptr) {
        error = path + ": missing root element";
        return nullptr;
    }
    if (root->Name() == nullptr || std::string(root->Name()) != rootName) {
        error = path + ": root element must be <" + rootName + ">";
        return nullptr;
    }
    return root;
}

bool RequiredString(const tinyxml2::XMLElement* element, const char* name, std::string& out,
                    std::string& error, const std::string& where)
{
    const char* text = element->Attribute(name);
    if (text == nullptr || text[0] == '\0') {
        error = Missing(where, name, "missing attribute");
        return false;
    }
    out = text;
    return true;
}

bool RequiredU32(const tinyxml2::XMLElement* element, const char* name, std::uint32_t& out,
                 std::string& error, const std::string& where)
{
    std::int64_t value = 0;
    if (!ParseInt64(element, name, value)) {
        error = Missing(where, name, "missing or invalid attribute");
        return false;
    }
    if (value < 0 || value > std::numeric_limits<std::uint32_t>::max()) {
        error = Missing(where, name, "out of range attribute");
        return false;
    }
    out = static_cast<std::uint32_t>(value);
    return true;
}

bool RequiredU16(const tinyxml2::XMLElement* element, const char* name, std::uint16_t& out,
                 std::string& error, const std::string& where)
{
    std::int64_t value = 0;
    if (!ParseInt64(element, name, value)) {
        error = Missing(where, name, "missing or invalid attribute");
        return false;
    }
    if (value < 0 || value > std::numeric_limits<std::uint16_t>::max()) {
        error = Missing(where, name, "out of range attribute");
        return false;
    }
    out = static_cast<std::uint16_t>(value);
    return true;
}

bool RequiredI32(const tinyxml2::XMLElement* element, const char* name, std::int32_t& out,
                 std::string& error, const std::string& where)
{
    std::int64_t value = 0;
    if (!ParseInt64(element, name, value)) {
        error = Missing(where, name, "missing or invalid attribute");
        return false;
    }
    if (value < std::numeric_limits<std::int32_t>::min() ||
        value > std::numeric_limits<std::int32_t>::max()) {
        error = Missing(where, name, "out of range attribute");
        return false;
    }
    out = static_cast<std::int32_t>(value);
    return true;
}

std::string OptionalString(const tinyxml2::XMLElement* element, const char* name,
                           const std::string& fallback)
{
    const char* text = element->Attribute(name);
    return text != nullptr ? text : fallback;
}

bool OptionalU16(const tinyxml2::XMLElement* element, const char* name, std::uint16_t fallback,
                 std::uint16_t& out, std::string& error, const std::string& where)
{
    if (element->Attribute(name) == nullptr) {
        out = fallback;
        return true;
    }
    return RequiredU16(element, name, out, error, where);
}

bool OptionalU32(const tinyxml2::XMLElement* element, const char* name, std::uint32_t fallback,
                 std::uint32_t& out, std::string& error, const std::string& where)
{
    if (element->Attribute(name) == nullptr) {
        out = fallback;
        return true;
    }
    return RequiredU32(element, name, out, error, where);
}

bool OptionalI32(const tinyxml2::XMLElement* element, const char* name, std::int32_t fallback,
                 std::int32_t& out, std::string& error, const std::string& where)
{
    if (element->Attribute(name) == nullptr) {
        out = fallback;
        return true;
    }
    return RequiredI32(element, name, out, error, where);
}

bool OptionalDouble(const tinyxml2::XMLElement* element, const char* name, double fallback,
                    double& out, std::string& error, const std::string& where)
{
    if (element->Attribute(name) == nullptr) {
        out = fallback;
        return true;
    }
    if (!ParseDouble(element, name, out)) {
        error = Missing(where, name, "invalid attribute");
        return false;
    }
    return true;
}

bool OptionalVersion(const tinyxml2::XMLElement* root, std::uint32_t& out, std::string& error,
                     const std::string& where)
{
    return OptionalU32(root, "version", 1, out, error, where);
}

} // namespace coop::xml
