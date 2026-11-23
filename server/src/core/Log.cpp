#include "core/Log.h"

#include <cstdio>
#include <ctime>
#include <map>

namespace coop {

namespace {

const char* LevelName(LogLevel level)
{
    switch (level) {
    case LogLevel::Debug: return "DBG ";
    case LogLevel::Info: return "INFO";
    case LogLevel::Warn: return "WARN";
    case LogLevel::Error: return "ERR ";
    }
    return "????";
}

std::mutex g_mutex;
std::map<std::string, long long> g_counters;

} // namespace

void Log::Write(LogLevel level, const std::string& message)
{
    if (level == LogLevel::Debug && !Verbose())
        return;

    using clock = std::chrono::system_clock;
    const auto now = clock::now();
    const auto ms = std::chrono::duration_cast<std::chrono::milliseconds>(now.time_since_epoch()) % 1000;
    const std::time_t t = clock::to_time_t(now);

    std::tm tm{};
#if defined(_WIN32)
    localtime_s(&tm, &t);
#else
    localtime_r(&t, &tm);
#endif

    char stamp[16];
    std::snprintf(stamp, sizeof(stamp), "%02d:%02d:%02d.%03d",
                  tm.tm_hour, tm.tm_min, tm.tm_sec, static_cast<int>(ms.count()));

    std::lock_guard<std::mutex> lock(g_mutex);
    std::printf("%s [%s] %s\n", stamp, LevelName(level), message.c_str());
    std::fflush(stdout);
}

void Log::Count(const std::string& key, long long delta)
{
    std::lock_guard<std::mutex> lock(g_mutex);
    g_counters[key] += delta;
}

void Log::DumpCounters()
{
    std::lock_guard<std::mutex> lock(g_mutex);
    if (g_counters.empty())
        return;

    std::string line;
    for (const auto& [key, value] : g_counters) {
        if (value == 0)
            continue;
        line += key + "=" + std::to_string(value) + " ";
    }

    if (!line.empty())
        std::printf("stats %s\n", line.c_str());
}

} // namespace coop
