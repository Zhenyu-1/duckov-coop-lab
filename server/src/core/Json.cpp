#include "core/Json.h"

#include <cctype>
#include <fstream>
#include <sstream>

namespace coop {

namespace {

class Parser {
public:
    Parser(const std::string& source) : _src(source) {}

    bool Parse(Json& out, std::string& error)
    {
        SkipWhitespace();
        if (!ParseValue(out)) {
            error = _error.empty() ? "invalid json" : _error;
            return false;
        }
        SkipWhitespace();
        if (_pos != _src.size()) {
            error = "trailing characters";
            return false;
        }
        return true;
    }

private:
    void SkipWhitespace()
    {
        while (_pos < _src.size() && std::isspace(static_cast<unsigned char>(_src[_pos])))
            ++_pos;
    }

    bool Fail(const std::string& message)
    {
        if (_error.empty())
            _error = message;
        return false;
    }

    bool ParseValue(Json& out)
    {
        if (_pos >= _src.size())
            return Fail("unexpected end");

        const char c = _src[_pos];
        if (c == '{') return ParseObject(out);
        if (c == '[') return ParseArray(out);
        if (c == '"') return ParseString(out);
        if (c == 't') return ParseLiteral("true", out, true);
        if (c == 'f') return ParseLiteral("false", out, false);
        if (c == 'n') return ParseNull(out);
        if (c == '-' || std::isdigit(static_cast<unsigned char>(c))) return ParseNumber(out);
        return Fail("unexpected character");
    }

    bool ParseObject(Json& out)
    {
        out = Json{};
        out.type = Json::Type::Object;
        ++_pos; // {
        SkipWhitespace();

        if (_pos < _src.size() && _src[_pos] == '}') {
            ++_pos;
            return true;
        }

        while (true) {
            SkipWhitespace();
            Json key;
            if (!ParseString(key))
                return false;

            SkipWhitespace();
            if (_pos >= _src.size() || _src[_pos] != ':')
                return Fail("expected ':'");
            ++_pos;

            SkipWhitespace();
            Json value;
            if (!ParseValue(value))
                return false;

            out.fields[key.text] = std::move(value);
            SkipWhitespace();

            if (_pos >= _src.size())
                return Fail("unterminated object");
            if (_src[_pos] == ',') {
                ++_pos;
                continue;
            }
            if (_src[_pos] == '}') {
                ++_pos;
                return true;
            }
            return Fail("expected ',' or '}'");
        }
    }

    bool ParseArray(Json& out)
    {
        out = Json{};
        out.type = Json::Type::Array;
        ++_pos; // [
        SkipWhitespace();

        if (_pos < _src.size() && _src[_pos] == ']') {
            ++_pos;
            return true;
        }

        while (true) {
            SkipWhitespace();
            Json value;
            if (!ParseValue(value))
                return false;
            out.items.push_back(std::move(value));
            SkipWhitespace();

            if (_pos >= _src.size())
                return Fail("unterminated array");
            if (_src[_pos] == ',') {
                ++_pos;
                continue;
            }
            if (_src[_pos] == ']') {
                ++_pos;
                return true;
            }
            return Fail("expected ',' or ']'");
        }
    }

    bool ParseString(Json& out)
    {
        if (_pos >= _src.size() || _src[_pos] != '"')
            return Fail("expected string");
        ++_pos;

        std::string value;
        while (_pos < _src.size()) {
            const char c = _src[_pos++];
            if (c == '"') {
                out = Json{};
                out.type = Json::Type::String;
                out.text = std::move(value);
                return true;
            }
            if (c != '\\') {
                value.push_back(c);
                continue;
            }
            if (_pos >= _src.size())
                return Fail("bad escape");
            const char esc = _src[_pos++];
            switch (esc) {
            case '"': value.push_back('"'); break;
            case '\\': value.push_back('\\'); break;
            case '/': value.push_back('/'); break;
            case 'b': value.push_back('\b'); break;
            case 'f': value.push_back('\f'); break;
            case 'n': value.push_back('\n'); break;
            case 'r': value.push_back('\r'); break;
            case 't': value.push_back('\t'); break;
            default: return Fail("unsupported escape");
            }
        }
        return Fail("unterminated string");
    }

    bool ParseNumber(Json& out)
    {
        const std::size_t start = _pos;
        if (_pos < _src.size() && _src[_pos] == '-')
            ++_pos;
        while (_pos < _src.size() && std::isdigit(static_cast<unsigned char>(_src[_pos])))
            ++_pos;
        if (_pos < _src.size() && _src[_pos] == '.') {
            ++_pos;
            while (_pos < _src.size() && std::isdigit(static_cast<unsigned char>(_src[_pos])))
                ++_pos;
        }

        out = Json{};
        out.type = Json::Type::Number;
        out.number = std::stod(_src.substr(start, _pos - start));
        return true;
    }

    bool ParseLiteral(const char* literal, Json& out, bool value)
    {
        const std::string text(literal);
        if (_src.compare(_pos, text.size(), text) != 0)
            return Fail("bad literal");
        _pos += text.size();
        out = Json{};
        out.type = Json::Type::Bool;
        out.boolean = value;
        return true;
    }

    bool ParseNull(Json& out)
    {
        if (_src.compare(_pos, 4, "null") != 0)
            return Fail("bad literal");
        _pos += 4;
        out = Json{};
        out.type = Json::Type::Null;
        return true;
    }

    const std::string& _src;
    std::size_t _pos = 0;
    std::string _error;
};

} // namespace

bool ParseJson(const std::string& source, Json& out, std::string& error)
{
    Parser parser(source);
    return parser.Parse(out, error);
}

bool LoadJsonFile(const std::string& path, Json& out, std::string& error)
{
    std::ifstream file(path, std::ios::binary);
    if (!file) {
        error = "cannot open " + path;
        return false;
    }

    std::ostringstream buffer;
    buffer << file.rdbuf();
    return ParseJson(buffer.str(), out, error);
}

} // namespace coop
