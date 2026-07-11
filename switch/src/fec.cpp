#include "fec.hpp"
#include <algorithm>
#include <cstring>
#include <iostream>

bool FecProcessor::parsePacket(const uint8_t* buffer, int len, MediaPacket& packet) {
    if (len < 20) return false;
    
    packet.version = buffer[0];
    packet.flags = buffer[1];
    packet.payloadLen = (buffer[2] << 8) | buffer[3];
    packet.frameId = ((uint32_t)buffer[4] << 24) | ((uint32_t)buffer[5] << 16) | 
                     ((uint32_t)buffer[6] << 8) | (uint32_t)buffer[7];
    packet.packetIndex = (buffer[8] << 8) | buffer[9];
    packet.packetCount = (buffer[10] << 8) | buffer[11];
    packet.fecCount = (buffer[12] << 8) | buffer[13];
    packet.ptsMs = ((uint32_t)buffer[14] << 24) | ((uint32_t)buffer[15] << 16) | 
                   ((uint32_t)buffer[16] << 8) | (uint32_t)buffer[17];
    packet.pipelineDelayMs = (buffer[18] << 8) | buffer[19];

    if (len < 20 + packet.payloadLen) return false;
    
    packet.payload.assign(buffer + 20, buffer + 20 + packet.payloadLen);
    return true;
}

bool FrameAssembly::runFecRecovery() {
    if (isComplete()) return true;
    if (packetCount == 0) return false;

    uint16_t numGroups = (packetCount + 7) / 8;
    bool recoveredAny = false;

    for (uint16_t g = 0; g < numGroups; ++g) {
        uint16_t groupStart = g * 8;
        uint16_t groupEnd = std::min((uint16_t)(groupStart + 8), packetCount);

        std::vector<uint16_t> missingIndices;
        for (uint16_t i = groupStart; i < groupEnd; ++i) {
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

            for (uint16_t i = groupStart; i < groupEnd; ++i) {
                if (i == missingIdx) continue;
                const std::vector<uint8_t>& data = dataPackets[i];
                for (size_t byte = 0; byte < parityLen; ++byte) {
                    uint8_t val = (byte < data.size()) ? data[byte] : 0;
                    recovered[byte] ^= val;
                }
            }

            // Normal packets are 1200 bytes, the last packet is parityLen
            size_t trueLen = (missingIdx == packetCount - 1) ? parityLen : 1200;
            if (recovered.size() > trueLen) {
                recovered.resize(trueLen);
            }

            dataPackets[missingIdx] = recovered;
            recoveredAny = true;
        }
    }

    return isComplete();
}
