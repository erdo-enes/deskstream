#pragma once

#include <string>
#include <vector>
#include <map>
#include <cstdint>
#include <netinet/in.h>

struct DiscoveredServer {
    std::string ip;
    std::string hostname;
    uint16_t controlPort;
};

class SocketUtil {
public:
    static bool setNonBlocking(int fd);
    static bool setBufferSize(int fd, int optname, int size);
};

class DiscoveryClient {
private:
    int _fd = -1;
    uint64_t _lastProbeUs = 0;
    std::vector<uint32_t> _broadcastAddresses;
    std::map<std::string, DiscoveredServer> _knownServers;
    std::string _lastError;
public:
    DiscoveryClient();
    ~DiscoveryClient();
    bool start();
    std::vector<DiscoveredServer> probe();
    void clearResults();
    const std::string& lastError() const { return _lastError; }
};

class TCPClient {
private:
    int _fd = -1;
    std::string _ip;
    uint16_t _port;
    std::vector<uint8_t> _readBuffer;
    bool tryExtractMessage(std::string& json);
public:
    TCPClient(const std::string& ip, uint16_t port);
    ~TCPClient();
    bool connectToServer(int timeoutSec = 5);
    void disconnect();
    bool isConnected() const { return _fd != -1; }

    bool sendMessage(const std::string& json);
    bool readMessage(std::string& json, int timeoutMs = 100);
};

class UDPReceiver {
private:
    int _fd = -1;
    uint16_t _port = 0;
public:
    UDPReceiver();
    ~UDPReceiver();
    bool bindToAny();
    uint16_t getPort() const { return _port; }
    int getFd() const { return _fd; }
    
    // Reads a packet, returns bytes read (negative on error/timeout)
    int receive(uint8_t* buffer, int maxLen, struct sockaddr_in& fromAddr, int timeoutMs = 0);
    // Sends a hole-punch or gamepad packet
    bool sendTo(const uint8_t* data, int len, const struct sockaddr_in& destAddr);
};
