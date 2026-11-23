#include "net/UdpSocket.h"

#include <cstring>
#include <string>

#if defined(_WIN32)
#include <ws2tcpip.h>
#pragma comment(lib, "ws2_32.lib")
#else
#include <arpa/inet.h>
#include <sys/select.h>
#include <sys/socket.h>
#include <unistd.h>
#endif

namespace coop::net {

std::string Endpoint::ToString() const
{
    in_addr addr{};
    addr.s_addr = htonl(address);
    char text[INET_ADDRSTRLEN] = {};
    inet_ntop(AF_INET, &addr, text, sizeof(text));
    return std::string(text) + ":" + std::to_string(port);
}

UdpSocket::~UdpSocket()
{
    Close();
}

bool UdpSocket::Valid() const
{
    return _socket != static_cast<std::uintptr_t>(~0ull);
}

bool UdpSocket::Bind(std::uint16_t port, std::string& error)
{
#if defined(_WIN32)
    if (!_wsaStarted) {
        WSADATA data{};
        if (WSAStartup(MAKEWORD(2, 2), &data) != 0) {
            error = "WSAStartup failed";
            return false;
        }
        _wsaStarted = true;
    }
    SOCKET s = ::socket(AF_INET, SOCK_DGRAM, IPPROTO_UDP);
    if (s == INVALID_SOCKET) {
        error = "socket() failed: " + std::to_string(WSAGetLastError());
        return false;
    }
    _socket = static_cast<std::uintptr_t>(s);

    sockaddr_in addr{};
    addr.sin_family = AF_INET;
    addr.sin_addr.s_addr = htonl(INADDR_ANY);
    addr.sin_port = htons(port);
    if (::bind(s, reinterpret_cast<sockaddr*>(&addr), sizeof(addr)) == SOCKET_ERROR) {
        error = "bind() failed: " + std::to_string(WSAGetLastError());
        Close();
        return false;
    }
#else
    const int s = ::socket(AF_INET, SOCK_DGRAM, IPPROTO_UDP);
    if (s < 0) {
        error = "socket() failed";
        return false;
    }
    _socket = static_cast<std::uintptr_t>(s);

    sockaddr_in addr{};
    addr.sin_family = AF_INET;
    addr.sin_addr.s_addr = htonl(INADDR_ANY);
    addr.sin_port = htons(port);
    if (::bind(s, reinterpret_cast<sockaddr*>(&addr), sizeof(addr)) < 0) {
        error = "bind() failed";
        Close();
        return false;
    }
#endif

    // discover the effective port (for port == 0)
    sockaddr_in local{};
#if defined(_WIN32)
    int len = sizeof(local);
    getsockname(static_cast<SOCKET>(_socket), reinterpret_cast<sockaddr*>(&local), &len);
#else
    socklen_t len = sizeof(local);
    getsockname(static_cast<int>(_socket), reinterpret_cast<sockaddr*>(&local), &len);
#endif
    _localPort = ntohs(local.sin_port);
    return true;
}

void UdpSocket::Close()
{
    if (!Valid())
        return;
#if defined(_WIN32)
    ::closesocket(static_cast<SOCKET>(_socket));
    if (_wsaStarted) {
        WSACleanup();
        _wsaStarted = false;
    }
#else
    ::close(static_cast<int>(_socket));
#endif
    _socket = static_cast<std::uintptr_t>(~0ull);
}

bool UdpSocket::SendTo(const Endpoint& to, const std::uint8_t* data, std::size_t size)
{
    if (!Valid())
        return false;

    sockaddr_in addr{};
    addr.sin_family = AF_INET;
    addr.sin_addr.s_addr = htonl(to.address);
    addr.sin_port = htons(to.port);

#if defined(_WIN32)
    const int sent = ::sendto(static_cast<SOCKET>(_socket),
                              reinterpret_cast<const char*>(data),
                              static_cast<int>(size), 0,
                              reinterpret_cast<const sockaddr*>(&addr), sizeof(addr));
    return sent == static_cast<int>(size);
#else
    const ssize_t sent = ::sendto(static_cast<int>(_socket), data, size, 0,
                                  reinterpret_cast<const sockaddr*>(&addr), sizeof(addr));
    return sent == static_cast<ssize_t>(size);
#endif
}

int UdpSocket::RecvFrom(std::uint8_t* buffer, std::size_t size, Endpoint& from, int timeoutMs)
{
    if (!Valid())
        return -1;

    fd_set readSet;
    FD_ZERO(&readSet);
#if defined(_WIN32)
    FD_SET(static_cast<SOCKET>(_socket), &readSet);
    timeval tv{};
    tv.tv_sec = timeoutMs / 1000;
    tv.tv_usec = (timeoutMs % 1000) * 1000;
    const int ready = ::select(0, &readSet, nullptr, nullptr, &tv);
#else
    FD_SET(static_cast<int>(_socket), &readSet);
    timeval tv{};
    tv.tv_sec = timeoutMs / 1000;
    tv.tv_usec = (timeoutMs % 1000) * 1000;
    const int ready = ::select(static_cast<int>(_socket) + 1, &readSet, nullptr, nullptr, &tv);
#endif

    if (ready <= 0)
        return 0;

    sockaddr_in addr{};
#if defined(_WIN32)
    int len = sizeof(addr);
    const int received = ::recvfrom(static_cast<SOCKET>(_socket),
                                    reinterpret_cast<char*>(buffer),
                                    static_cast<int>(size), 0,
                                    reinterpret_cast<sockaddr*>(&addr), &len);
#else
    socklen_t len = sizeof(addr);
    const ssize_t received = ::recvfrom(static_cast<int>(_socket), buffer, size, 0,
                                        reinterpret_cast<sockaddr*>(&addr), &len);
#endif
    if (received < 0)
        return -1;

    from.address = ntohl(addr.sin_addr.s_addr);
    from.port = ntohs(addr.sin_port);
    return static_cast<int>(received);
}

} // namespace coop::net
