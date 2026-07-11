#pragma once

#include <vector>
#include <cstdint>
#include <map>

// Struct representing a received media packet
struct MediaPacket {
    uint8_t version;
    uint8_t flags;
    uint16_t payloadLen;
    uint32_t frameId;
    uint16_t packetIndex;
    uint16_t packetCount;
    uint16_t fecCount;
    uint32_t ptsMs;
    uint16_t pipelineDelayMs;
    std::vector<uint8_t> payload;
};

// Assembly state for a single frame
struct FrameAssembly {
    uint32_t frameId = 0;
    uint16_t packetCount = 0;
    uint16_t fecCount = 0;
    uint32_t ptsMs = 0;
    bool isKeyframe = false;
    
    // Map from packetIndex to packet payload
    std::map<uint16_t, std::vector<uint8_t>> dataPackets;
    // Map from group index to parity packet
    std::map<uint16_t, std::vector<uint8_t>> parityPackets;
    
    bool isComplete() const { return dataPackets.size() == packetCount; }
    
    // Attemps to recover any missing packet using FEC parity.
    // Returns true if recovery succeeded or the frame was already complete.
    bool runFecRecovery();
};

class FecProcessor {
public:
    static bool parsePacket(const uint8_t* buffer, int len, MediaPacket& packet);
};
