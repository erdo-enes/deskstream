#pragma once

#include <vector>
#include <cstdint>
#include <map>

struct ServerFrameTrace {
    uint32_t frameId = 0;
    int64_t captureStartUs = 0;
    int64_t captureEndUs = 0;
    int64_t convertEndUs = 0;
    int64_t encodeSubmitUs = 0;
    int64_t encodeFinishUs = 0;
    int64_t packetStartUs = 0;
    int64_t packetEndUs = 0;
};

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
    uint64_t firstReceiveUs = 0;
    uint64_t lastReceiveUs = 0;
    uint64_t completedAtUs = 0;
    uint16_t recoveredPacketCount = 0;
    
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
    static bool parseFrameTrace(const uint8_t* buffer, int len, ServerFrameTrace& trace);
};
