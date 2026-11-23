#pragma once

#include <chrono>
#include <cstdint>
#include <mutex>
#include <string>

namespace coop {

enum class LogLevel { Debug, Info, Warn, Error };

class Log {
public:
    static void SetVerbose(bool verbose) { Verbose() = verbose; }

    static void Debug(const std::string& message) { Write(LogLevel::Debug, message); }
    static void Info(const std::string& message) { Write(LogLevel::Info, message); }
    static void Warn(const std::string& message) { Write(LogLevel::Warn, message); }
    static void Error(const std::string& message) { Write(LogLevel::Error, message); }

    static void Write(LogLevel level, const std::string& message);

    /// Lightweight per-op counters, dumped at shutdown.
    static void Count(const std::string& key, long long delta = 1);
    static void DumpCounters();

private:
    static bool& Verbose()
    {
        static bool value = false;
        return value;
    }
};

} // namespace coop
