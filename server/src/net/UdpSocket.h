#pragma once

#include <cstddef>
#include <cstdint>
#include <string>

#if defined(_WIN32)
#include <winsock2.h>
#include <ws2tcpip.h>
#else
#include <netinet/in.h>
#endif

namespace coop::net {

struct Endpoint {
    std::uint32_t address = 0; // host order IPv4
    std::uint16_t port = 0;

    std::uint64_t Key() const
    {
        return (static_cast<std::uint64_t>(address) << 16) | port;
    }

    std::string ToString() const;
};

class UdpSocket {
public:
    UdpSocket() = default;
    ~UdpSocket();

    UdpSocket(const UdpSocket&) = delete;
    UdpSocket& operator=(const UdpSocket&) = delete;

    bool Bind(std::uint16_t port, std::string& error);
    void Close();

    bool SendTo(const Endpoint& to, const std::uint8_t* data, std::size_t size);
    int RecvFrom(std::uint8_t* buffer, std::size_t size, Endpoint& from, int timeoutMs);

    std::uint16_t LocalPort() const { return _localPort; }
    bool Valid() const;

private:
    std::uintptr_t _socket = static_cast<std::uintptr_t>(~0ull);
    std::uint16_t _localPort = 0;
    bool _wsaStarted = false;
};

} // namespace coop::net
