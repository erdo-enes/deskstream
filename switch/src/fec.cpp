#include "fec.hpp"
#include <algorithm>
#include <cstring>
#include <iostream>

namespace {

constexpr uint16_t kFecInterleave = 4;
constexpr uint16_t kMaxPayload = 1200;
constexpr uint16_t kMaxReasonablePacketCount = 4096;

uint16_t readU16(const uint8_t* p) {
    return static_cast<uint16_t>((static_cast<uint16_t>(p[0]) << 8) | p[1]);
}

uint32_t readU32(const uint8_t* p) {
    return (static_cast<uint32_t>(p[0]) << 24) |
           (static_cast<uint32_t>(p[1]) << 16) |
           (static_cast<uint32_t>(p[2]) << 8) |
           static_cast<uint32_t>(p[3]);
}

int64_t readI64(const uint8_t* p) {
    uint64_t value = 0;
    for (int i = 0; i < 8; ++i) value = (value << 8) | p[i];
    return static_cast<int64_t>(value);
}

} // namespace

bool FecProcessor::parsePacket(const uint8_t* buffer, int len, MediaPacket& packet) {
    if (len < 20) return false;
    
    packet.version = buffer[0];
    packet.flags = buffer[1];
    packet.payloadLen = readU16(buffer + 2);
    packet.frameId = readU32(buffer + 4);
    packet.packetIndex = readU16(buffer + 8);
    packet.packetCount = readU16(buffer + 10);
    packet.fecCount = readU16(buffer + 12);
    packet.ptsMs = readU32(buffer + 14);
    packet.pipelineDelayMs = readU16(buffer + 18);

    if (packet.version != 1 || (packet.flags & ~0x03u) != 0 ||
        packet.payloadLen == 0 || packet.payloadLen > kMaxPayload ||
        packet.packetCount == 0 || packet.packetCount > kMaxReasonablePacketCount ||
        packet.fecCount != std::min<uint16_t>(kFecInterleave, packet.packetCount) ||
        len != 20 + packet.payloadLen) {
        return false;
    }

    const bool isFec = (packet.flags & 0x02u) != 0;
    if ((isFec && packet.packetIndex >= packet.fecCount) ||
        (!isFec && packet.packetIndex >= packet.packetCount)) {
        return false;
    }
    if (!isFec && packet.packetIndex + 1 < packet.packetCount &&
        packet.payloadLen != kMaxPayload) {
        return false;
    }
    if (isFec) {
        const uint16_t group = packet.packetIndex;
        if (group >= packet.packetCount) return false;
        const uint16_t memberCount = static_cast<uint16_t>(
            (packet.packetCount - group + kFecInterleave - 1) / kFecInterleave);
        if (memberCount > 1 && packet.payloadLen != kMaxPayload) return false;
    }
    
    packet.payload.assign(buffer + 20, buffer + 20 + packet.payloadLen);
    return true;
}

bool FecProcessor::parseFrameTrace(const uint8_t* buffer, int len, ServerFrameTrace& trace) {
    if (len != 68 || std::memcmp(buffer, "DSTR", 4) != 0 || buffer[4] != 1 ||
        buffer[5] != 0 || buffer[6] != 0 || buffer[7] != 0) {
        return false;
    }
    trace.frameId = readU32(buffer + 8);
    trace.captureStartUs = readI64(buffer + 12);
    trace.captureEndUs = readI64(buffer + 20);
    trace.convertEndUs = readI64(buffer + 28);
    trace.encodeSubmitUs = readI64(buffer + 36);
    trace.encodeFinishUs = readI64(buffer + 44);
    trace.packetStartUs = readI64(buffer + 52);
    trace.packetEndUs = readI64(buffer + 60);
    return true;
}

bool FrameAssembly::runFecRecovery() {
    if (isComplete()) return true;
    if (packetCount == 0) return false;

    for (uint16_t g = 0; g < fecCount; ++g) {
        std::vector<uint16_t> missingIndices;
        for (uint16_t i = g; i < packetCount; i = static_cast<uint16_t>(i + kFecInterleave)) {
            if (dataPackets.find(i) == dataPackets.end()) {
                missingIndices.push_back(i);
            }
        }

        // XOR Recovery can reconstruct exactly 1 missing packet if we have the parity packet
        if (missingIndices.size() == 1 && parityPackets.find(g) != parityPackets.end()) {
            uint16_t missingIdx = missingIndices[0];
            const std::vector<uint8_t>& parity = parityPackets[g];
            size_t parityLen = parity.size();

            std::vector<uint8_t> recovered(parityLen, 0);
            std::memcpy(recovered.data(), parity.data(), parityLen);

            for (uint16_t i = g; i < packetCount; i = static_cast<uint16_t>(i + kFecInterleave)) {
                if (i == missingIdx) continue;
                auto dataIt = dataPackets.find(i);
                if (dataIt == dataPackets.end()) return false;
                const std::vector<uint8_t>& data = dataIt->second;
                for (size_t byte = 0; byte < parityLen; ++byte) {
                    uint8_t val = (byte < data.size()) ? data[byte] : 0;
                    recovered[byte] ^= val;
                }
            }

            // Normal packets are 1200 bytes, the last packet is parityLen
            size_t trueLen = (missingIdx == packetCount - 1) ? parityLen : kMaxPayload;
            if (recovered.size() > trueLen) {
                recovered.resize(trueLen);
            }

            dataPackets[missingIdx] = recovered;
            ++recoveredPacketCount;
        }
    }

    return isComplete();
}
