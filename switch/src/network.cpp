#include "network.hpp"
#include <sys/socket.h>
#include <sys/types.h>
#include <arpa/inet.h>
#include <netinet/tcp.h>
#include <unistd.h>
#include <fcntl.h>
#include <cstring>
#include <iostream>
#include <poll.h>
#include <algorithm>
#include <charconv>
#include <chrono>
#include <cerrno>

#ifdef __SWITCH__
#include <switch.h>
#endif

namespace {

uint64_t monotonicUs() {
    return static_cast<uint64_t>(std::chrono::duration_cast<std::chrono::microseconds>(
        std::chrono::steady_clock::now().time_since_epoch()).count());
}

bool extractJsonString(const std::string& json, const char* key, std::string& value) {
    const std::string marker = std::string("\"") + key + "\"";
    size_t pos = json.find(marker);
    if (pos == std::string::npos) return false;
    pos = json.find(':', pos + marker.size());
    if (pos == std::string::npos) return false;
    pos = json.find_first_not_of(" \t\r\n", pos + 1);
    if (pos == std::string::npos || json[pos] != '"') return false;
    size_t end = json.find('"', pos + 1);
    if (end == std::string::npos) return false;
    value.assign(json, pos + 1, end - pos - 1);
    return true;
}

bool extractJsonUInt(const std::string& json, const char* key, uint32_t& value) {
    const std::string marker = std::string("\"") + key + "\"";
    size_t pos = json.find(marker);
    if (pos == std::string::npos) return false;
    pos = json.find(':', pos + marker.size());
    if (pos == std::string::npos) return false;
    pos = json.find_first_not_of(" \t\r\n", pos + 1);
    if (pos == std::string::npos) return false;
    const char* begin = json.data() + pos;
    const char* end = json.data() + json.size();
    uint32_t parsed = 0;
    auto result = std::from_chars(begin, end, parsed);
    if (result.ec != std::errc()) return false;
    value = parsed;
    return true;
}

bool sendAll(int fd, const uint8_t* data, size_t length) {
    size_t sentTotal = 0;
    while (sentTotal < length) {
        ssize_t sent = send(fd, data + sentTotal, length - sentTotal, 0);
        if (sent > 0) {
            sentTotal += static_cast<size_t>(sent);
            continue;
        }
        if (sent < 0 && errno == EINTR) continue;
        if (sent < 0 && (errno == EAGAIN || errno == EWOULDBLOCK)) {
            struct pollfd writable;
            writable.fd = fd;
            writable.events = POLLOUT;
            int pollResult;
            do {
                pollResult = poll(&writable, 1, 1000);
            } while (pollResult < 0 && errno == EINTR);
            if (pollResult > 0 && (writable.revents & POLLOUT) != 0) continue;
        }
        return false;
    }
    return true;
}

} // namespace

bool SocketUtil::setNonBlocking(int fd) {
    int flags = fcntl(fd, F_GETFL, 0);
    if (flags == -1) return false;
    return fcntl(fd, F_SETFL, flags | O_NONBLOCK) == 0;
}

bool SocketUtil::setBufferSize(int fd, int optname, int size) {
    return setsockopt(fd, SOL_SOCKET, optname, &size, sizeof(size)) == 0;
}

// ---- Discovery -----------------------------------------------------------

DiscoveryClient::DiscoveryClient() {}

DiscoveryClient::~DiscoveryClient() {
    if (_fd != -1) {
        close(_fd);
    }
}

bool DiscoveryClient::start() {
    if (_fd != -1) {
        close(_fd);
        _fd = -1;
    }
    _lastError.clear();
    _lastProbeUs = 0;
    _broadcastAddresses.clear();
    _knownServers.clear();

    _fd = socket(AF_INET, SOCK_DGRAM, IPPROTO_UDP);
    if (_fd == -1) {
        _lastError = std::string("socket: ") + std::strerror(errno);
        return false;
    }

    int broadcastEnable = 1;
    if (setsockopt(_fd, SOL_SOCKET, SO_BROADCAST, &broadcastEnable, sizeof(broadcastEnable)) < 0) {
        _lastError = std::string("setsockopt(SO_BROADCAST): ") + std::strerror(errno);
        close(_fd);
        _fd = -1;
        return false;
    }
    int reuse = 1;
    (void)setsockopt(_fd, SOL_SOCKET, SO_REUSEADDR, &reuse, sizeof(reuse));

    struct sockaddr_in localAddr;
    std::memset(&localAddr, 0, sizeof(localAddr));
    localAddr.sin_family = AF_INET;
    localAddr.sin_port = htons(0);
    localAddr.sin_addr.s_addr = htonl(INADDR_ANY);
    if (bind(_fd, reinterpret_cast<struct sockaddr*>(&localAddr), sizeof(localAddr)) < 0) {
        _lastError = std::string("bind: ") + std::strerror(errno);
        close(_fd);
        _fd = -1;
        return false;
    }

    if (!SocketUtil::setNonBlocking(_fd)) {
        _lastError = std::string("fcntl(O_NONBLOCK): ") + std::strerror(errno);
        close(_fd);
        _fd = -1;
        return false;
    }

    _broadcastAddresses.push_back(htonl(INADDR_BROADCAST));
#ifdef __SWITCH__
    // Limited broadcast is not forwarded by every access point. Ask nifm for the active
    // interface's mask and also target its directed subnet broadcast, matching Android/macOS.
    Result nifmResult = nifmInitialize(NifmServiceType_User);
    if (R_SUCCEEDED(nifmResult)) {
        u32 address = 0, mask = 0, gateway = 0, primaryDns = 0, secondaryDns = 0;
        if (R_SUCCEEDED(nifmGetCurrentIpConfigInfo(
                &address, &mask, &gateway, &primaryDns, &secondaryDns)) && mask != 0) {
            uint32_t subnetBroadcast = (address & mask) | ~mask;
            if (std::find(_broadcastAddresses.begin(), _broadcastAddresses.end(), subnetBroadcast) ==
                _broadcastAddresses.end()) {
                _broadcastAddresses.push_back(subnetBroadcast);
            }
        }
        nifmExit();
    }
#endif
    return true;
}

std::vector<DiscoveredServer> DiscoveryClient::probe() {
    std::vector<DiscoveredServer> list;
    if (_fd == -1) return list;

    // Retry once per second, but keep this UI-facing method non-blocking. Replies remain queued
    // on the socket and are drained on this and subsequent menu ticks.
    uint64_t nowUs = monotonicUs();
    if (_lastProbeUs == 0 || nowUs - _lastProbeUs >= 1000000) {
        const char* probeStr = "DSPROBE1";
        for (uint32_t address : _broadcastAddresses) {
            struct sockaddr_in bcAddr;
            std::memset(&bcAddr, 0, sizeof(bcAddr));
            bcAddr.sin_family = AF_INET;
            bcAddr.sin_port = htons(47800);
            bcAddr.sin_addr.s_addr = address;
            (void)sendto(_fd, probeStr, 8, 0,
                         reinterpret_cast<struct sockaddr*>(&bcAddr), sizeof(bcAddr));
        }
        _lastProbeUs = nowUs;
    }

    // Give the server a small immediate-response window, then drain every currently queued reply.
    // Unlike the previous 150 ms blocking scan, delayed replies are still accepted next tick.
    struct pollfd pfd;
    pfd.fd = _fd;
    pfd.events = POLLIN;

    uint8_t buf[1024];
    int waitMs = 20;
    while (poll(&pfd, 1, waitMs) > 0) {
        waitMs = 0;
        struct sockaddr_in replyAddr;
        socklen_t addrLen = sizeof(replyAddr);
        int received = recvfrom(_fd, buf, sizeof(buf) - 1, 0, (struct sockaddr*)&replyAddr, &addrLen);
        if (received > 0) {
            buf[received] = '\0';
            std::string payload(reinterpret_cast<char*>(buf), static_cast<size_t>(received));
            std::string type;
            std::string name;
            uint32_t version = 0;
            uint32_t port = 0;
            if (!extractJsonString(payload, "type", type) || type != "DSREPLY" ||
                !extractJsonUInt(payload, "ver", version) || version != 1 ||
                !extractJsonUInt(payload, "controlPort", port) || port == 0 || port > 65535) {
                continue;
            }

            char ipBuffer[INET_ADDRSTRLEN] = {0};
            if (inet_ntop(AF_INET, &replyAddr.sin_addr, ipBuffer, sizeof(ipBuffer)) == nullptr) continue;
            (void)extractJsonString(payload, "name", name);
            if (name.empty()) name = ipBuffer;

            DiscoveredServer server;
            server.ip = ipBuffer;
            server.hostname = name;
            server.controlPort = static_cast<uint16_t>(port);
            _knownServers[server.ip + ":" + std::to_string(server.controlPort)] = server;
        }
    }

    list.reserve(_knownServers.size());
    for (const auto& pair : _knownServers) list.push_back(pair.second);
    return list;
}

void DiscoveryClient::clearResults() {
    _knownServers.clear();
    _lastProbeUs = 0;
}

// ---- TCP Control Client --------------------------------------------------

TCPClient::TCPClient(const std::string& ip, uint16_t port) : _ip(ip), _port(port) {}

TCPClient::~TCPClient() {
    disconnect();
}

bool TCPClient::connectToServer(int timeoutSec) {
    _fd = socket(AF_INET, SOCK_STREAM, IPPROTO_TCP);
    if (_fd == -1) return false;

    // Use non-blocking to support connection timeout
    SocketUtil::setNonBlocking(_fd);

    struct sockaddr_in addr;
    std::memset(&addr, 0, sizeof(addr));
    addr.sin_family = AF_INET;
    addr.sin_port = htons(_port);
    if (inet_pton(AF_INET, _ip.c_str(), &addr.sin_addr) != 1) {
        close(_fd);
        _fd = -1;
        return false;
    }

    int res = connect(_fd, (struct sockaddr*)&addr, sizeof(addr));
    if (res < 0 && errno != EINPROGRESS) {
        close(_fd);
        _fd = -1;
        return false;
    }

    struct pollfd pfd;
    pfd.fd = _fd;
    pfd.events = POLLOUT;
    
    int pollRes = poll(&pfd, 1, timeoutSec * 1000);
    if (pollRes <= 0) {
        close(_fd);
        _fd = -1;
        return false;
    }

    int optVal = 0;
    socklen_t optLen = sizeof(optVal);
    if (getsockopt(_fd, SOL_SOCKET, SO_ERROR, &optVal, &optLen) < 0 || optVal != 0) {
        close(_fd);
        _fd = -1;
        return false;
    }

    int nodelay = 1;
    setsockopt(_fd, IPPROTO_TCP, TCP_NODELAY, &nodelay, sizeof(nodelay));
    _readBuffer.clear();
    return true;
}

void TCPClient::disconnect() {
    if (_fd != -1) {
        close(_fd);
        _fd = -1;
    }
    _readBuffer.clear();
}

bool TCPClient::sendMessage(const std::string& json) {
    if (_fd == -1) return false;

    if (json.empty() || json.size() > 65536) return false;
    uint32_t len = htonl(static_cast<uint32_t>(json.size()));
    bool sent = sendAll(_fd, reinterpret_cast<const uint8_t*>(&len), sizeof(len)) &&
                sendAll(_fd, reinterpret_cast<const uint8_t*>(json.data()), json.size());
    if (!sent) disconnect();
    return sent;
}

bool TCPClient::tryExtractMessage(std::string& json) {
    if (_readBuffer.size() < 4) return false;
    uint32_t lenBE = 0;
    std::memcpy(&lenBE, _readBuffer.data(), sizeof(lenBE));
    uint32_t len = ntohl(lenBE);
    if (len == 0 || len > 65536) {
        disconnect();
        return false;
    }
    if (_readBuffer.size() < sizeof(lenBE) + len) return false;
    json.assign(reinterpret_cast<const char*>(_readBuffer.data() + sizeof(lenBE)), len);
    _readBuffer.erase(_readBuffer.begin(), _readBuffer.begin() + sizeof(lenBE) + len);
    return true;
}

bool TCPClient::readMessage(std::string& json, int timeoutMs) {
    if (_fd == -1) return false;
    const bool nonBlocking = timeoutMs <= 0;
    const auto deadline = std::chrono::steady_clock::now() +
        std::chrono::milliseconds(std::max(0, timeoutMs));
    bool receivedOnce = false;

    while (_fd != -1) {
        if (tryExtractMessage(json)) return true;
        if (nonBlocking && receivedOnce) return false;

        int waitMs = 0;
        if (!nonBlocking) {
            auto now = std::chrono::steady_clock::now();
            if (now >= deadline) return false;
            auto remaining = std::chrono::duration_cast<std::chrono::milliseconds>(deadline - now).count();
            waitMs = static_cast<int>(std::max<int64_t>(1, remaining));
        }

        struct pollfd pfd;
        pfd.fd = _fd;
        pfd.events = POLLIN;
        int pollResult;
        do {
            pollResult = poll(&pfd, 1, waitMs);
        } while (pollResult < 0 && errno == EINTR);
        if (pollResult <= 0 || (pfd.revents & POLLIN) == 0) {
            if (pollResult > 0 && (pfd.revents & (POLLERR | POLLHUP | POLLNVAL)) != 0)
                disconnect();
            return false;
        }

        uint8_t chunk[16384];
        ssize_t count = recv(_fd, chunk, sizeof(chunk), 0);
        if (count > 0) {
            _readBuffer.insert(_readBuffer.end(), chunk, chunk + count);
            receivedOnce = true;
            continue;
        }
        if (count < 0 && (errno == EINTR || errno == EAGAIN || errno == EWOULDBLOCK)) continue;
        disconnect();
        return false;
    }
    return false;
}

// ---- UDP Receiver --------------------------------------------------------

UDPReceiver::UDPReceiver() {}

UDPReceiver::~UDPReceiver() {
    if (_fd != -1) close(_fd);
}

bool UDPReceiver::bindToAny() {
    _fd = socket(AF_INET, SOCK_DGRAM, IPPROTO_UDP);
    if (_fd == -1) return false;

    // Pacing makes 256 KiB sufficient for 720p60 while bounding hidden stale-media backlog.
    SocketUtil::setBufferSize(_fd, SO_RCVBUF, 256 * 1024);
    SocketUtil::setNonBlocking(_fd);

    struct sockaddr_in addr;
    std::memset(&addr, 0, sizeof(addr));
    addr.sin_family = AF_INET;
    addr.sin_port = htons(0); // bind to ephemeral port
    addr.sin_addr.s_addr = htonl(INADDR_ANY);

    if (bind(_fd, (struct sockaddr*)&addr, sizeof(addr)) < 0) {
        close(_fd);
        _fd = -1;
        return false;
    }

    socklen_t len = sizeof(addr);
    if (getsockname(_fd, (struct sockaddr*)&addr, &len) < 0) {
        close(_fd);
        _fd = -1;
        return false;
    }

    _port = ntohs(addr.sin_port);
    return true;
}

int UDPReceiver::receive(uint8_t* buffer, int maxLen, struct sockaddr_in& fromAddr, int timeoutMs) {
    if (_fd == -1) return -1;

    if (timeoutMs > 0) {
        struct pollfd pfd;
        pfd.fd = _fd;
        pfd.events = POLLIN;
        int res = poll(&pfd, 1, timeoutMs);
        if (res <= 0) return -1; // timeout
    }

    socklen_t addrLen = sizeof(fromAddr);
    ssize_t bytes = recvfrom(_fd, buffer, maxLen, 0, (struct sockaddr*)&fromAddr, &addrLen);
    return (int)bytes;
}

bool UDPReceiver::sendTo(const uint8_t* data, int len, const struct sockaddr_in& destAddr) {
    if (_fd == -1) return false;
    ssize_t sent = sendto(_fd, data, len, 0, (struct sockaddr*)&destAddr, sizeof(destAddr));
    return sent == len;
}
