#pragma once

#include <map>
#include <string>
#include <vector>

namespace coop {

/// Minimal JSON subset parser (objects, arrays, strings, numbers, bool, null).
/// Enough for config files; no external dependency.
struct Json {
    enum class Type { Null, Bool, Number, String, Array, Object };

    Type type = Type::Null;
    bool boolean = false;
    double number = 0.0;
    std::string text;
    std::vector<Json> items;
    std::map<std::string, Json> fields;

    bool IsObject() const { return type == Type::Object; }
    bool IsArray() const { return type == Type::Array; }
    bool IsString() const { return type == Type::String; }
    bool IsNumber() const { return type == Type::Number; }

    const Json* Find(const std::string& key) const
    {
        if (type != Type::Object)
            return nullptr;
        const auto it = fields.find(key);
        return it == fields.end() ? nullptr : &it->second;
    }

    std::string AsString(const std::string& fallback = {}) const
    {
        return type == Type::String ? text : fallback;
    }

    double AsNumber(double fallback = 0.0) const
    {
        return type == Type::Number ? number : fallback;
    }
};

bool ParseJson(const std::string& source, Json& out, std::string& error);
bool LoadJsonFile(const std::string& path, Json& out, std::string& error);

} // namespace coop
