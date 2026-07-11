#include "network.hpp"
#include <sys/socket.h>
#include <sys/types.h>
#include <arpa/inet.h>
#include <unistd.h>
#include <fcntl.h>
#include <cstring>
#include <iostream>
#include <poll.h>

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
    _fd = socket(AF_INET, SOCK_DGRAM, IPPROTO_UDP);
    if (_fd == -1) return false;

    int broadcastEnable = 1;
    if (setsockopt(_fd, SOL_SOCKET, SO_BROADCAST, &broadcastEnable, sizeof(broadcastEnable)) < 0) {
        return false;
    }

    SocketUtil::setNonBlocking(_fd);
    return true;
}

std::vector<DiscoveredServer> DiscoveryClient::probe() {
    std::vector<DiscoveredServer> list;
    if (_fd == -1) return list;

    // Send DSPROBE1
    struct sockaddr_in bcAddr;
    std::memset(&bcAddr, 0, sizeof(bcAddr));
    bcAddr.sin_family = AF_INET;
    bcAddr.sin_port = htons(47800);
    bcAddr.sin_addr.s_addr = htonl(INADDR_BROADCAST);

    const char* probeStr = "DSPROBE1";
    sendto(_fd, probeStr, 8, 0, (struct sockaddr*)&bcAddr, sizeof(bcAddr));

    // Wait and read replies (up to 150ms)
    struct pollfd pfd;
    pfd.fd = _fd;
    pfd.events = POLLIN;

    uint8_t buf[1024];
    while (poll(&pfd, 1, 150) > 0) {
        struct sockaddr_in replyAddr;
        socklen_t addrLen = sizeof(replyAddr);
        int received = recvfrom(_fd, buf, sizeof(buf) - 1, 0, (struct sockaddr*)&replyAddr, &addrLen);
        if (received > 0) {
            buf[received] = '\0';
            std::string payload(reinterpret_cast<char*>(buf));
            
            // Minimal parser for {"type":"DSREPLY","ver":1,"name":"hostname","controlPort":47801}
            size_t namePos = payload.find("\"name\":\"");
            size_t portPos = payload.find("\"controlPort\":");
            if (namePos != std::string::npos && portPos != std::string::npos) {
                size_t nameEnd = payload.find("\"", namePos + 8);
                std::string name = payload.substr(namePos + 8, nameEnd - (namePos + 8));
                int port = std::stoi(payload.substr(portPos + 14));
                
                DiscoveredServer s;
                s.ip = inet_ntoa(replyAddr.sin_addr);
                s.hostname = name;
                s.controlPort = (uint16_t)port;
                list.push_back(s);
            }
        }
    }
    return list;
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
    inet_pton(AF_INET, _ip.c_str(), &addr.sin_addr);

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
    return true;
}

void TCPClient::disconnect() {
    if (_fd != -1) {
        close(_fd);
        _fd = -1;
    }
}

bool TCPClient::sendMessage(const std::string& json) {
    if (_fd == -1) return false;

    uint32_t len = htonl(json.length());
    // Send 4-byte length prefix
    if (send(_fd, &len, 4, 0) != 4) return false;
    // Send JSON payload
    if (send(_fd, json.c_str(), json.length(), 0) != (ssize_t)json.length()) return false;

    return true;
}

bool TCPClient::readMessage(std::string& json, int timeoutMs) {
    if (_fd == -1) return false;

    struct pollfd pfd;
    pfd.fd = _fd;
    pfd.events = POLLIN;

    int pollRes = poll(&pfd, 1, timeoutMs);
    if (pollRes <= 0) return false; // timeout or error

    // Read 4-byte length
    uint32_t lenBE = 0;
    ssize_t readBytes = recv(_fd, &lenBE, 4, MSG_PEEK); // peek to check if 4 bytes available
    if (readBytes < 4) {
        if (readBytes == 0) disconnect(); // socket closed
        return false;
    }
    recv(_fd, &lenBE, 4, 0); // consume length

    uint32_t len = ntohl(lenBE);
    if (len > 65536) { // wire limit check
        disconnect();
        return false;
    }

    std::vector<char> buf(len);
    uint32_t totalRead = 0;
    while (totalRead < len) {
        pollRes = poll(&pfd, 1, 1000);
        if (pollRes <= 0) return false;

        ssize_t chunk = recv(_fd, buf.data() + totalRead, len - totalRead, 0);
        if (chunk <= 0) {
            disconnect();
            return false;
        }
        totalRead += chunk;
    }

    json.assign(buf.data(), len);
    return true;
}

// ---- UDP Receiver --------------------------------------------------------

UDPReceiver::UDPReceiver() {}

UDPReceiver::~UDPReceiver() {
    if (_fd != -1) close(_fd);
}

bool UDPReceiver::bindToAny() {
    _fd = socket(AF_INET, SOCK_DGRAM, IPPROTO_UDP);
    if (_fd == -1) return false;

    // Request large buffer sizes to prevent packet drops in high throughput streams
    SocketUtil::setBufferSize(_fd, SO_RCVBUF, 512 * 1024);
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
