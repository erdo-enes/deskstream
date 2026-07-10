#import "DSProtocol.h"

#import <libkern/OSByteOrder.h>

NSString * const DSProtocolErrorDomain = @"com.deskstream.macos.protocol";

uint16_t DSReadUInt16BigEndian(const uint8_t *bytes) {
    uint16_t value;
    memcpy(&value, bytes, sizeof(value));
    return OSSwapBigToHostInt16(value);
}

uint32_t DSReadUInt32BigEndian(const uint8_t *bytes) {
    uint32_t value;
    memcpy(&value, bytes, sizeof(value));
    return OSSwapBigToHostInt32(value);
}

int16_t DSReadInt16BigEndian(const uint8_t *bytes) {
    return (int16_t)DSReadUInt16BigEndian(bytes);
}

int32_t DSReadInt32BigEndian(const uint8_t *bytes) {
    return (int32_t)DSReadUInt32BigEndian(bytes);
}

void DSWriteUInt16BigEndian(uint8_t *bytes, uint16_t value) {
    value = OSSwapHostToBigInt16(value);
    memcpy(bytes, &value, sizeof(value));
}

void DSWriteUInt32BigEndian(uint8_t *bytes, uint32_t value) {
    value = OSSwapHostToBigInt32(value);
    memcpy(bytes, &value, sizeof(value));
}

void DSWriteInt16BigEndian(uint8_t *bytes, int16_t value) {
    DSWriteUInt16BigEndian(bytes, (uint16_t)value);
}

void DSWriteInt32BigEndian(uint8_t *bytes, int32_t value) {
    DSWriteUInt32BigEndian(bytes, (uint32_t)value);
}

BOOL DSUInt32IsNewer(uint32_t candidate, uint32_t reference) {
    return (int32_t)(candidate - reference) > 0;
}

BOOL DSParseMediaDatagram(const void *rawBytes,
                          size_t length,
                          DSMediaHeader *headerOut,
                          const uint8_t **payloadOut) {
    if (rawBytes == NULL || length < DSMediaHeaderSize) {
        return NO;
    }

    const uint8_t *bytes = rawBytes;
    DSMediaHeader header = {
        .version = bytes[0],
        .flags = bytes[1],
        .payloadLength = DSReadUInt16BigEndian(bytes + 2),
        .frameID = DSReadUInt32BigEndian(bytes + 4),
        .packetIndex = DSReadUInt16BigEndian(bytes + 8),
        .packetCount = DSReadUInt16BigEndian(bytes + 10),
        .fecCount = DSReadUInt16BigEndian(bytes + 12),
        .presentationTimeMilliseconds = DSReadUInt32BigEndian(bytes + 14),
        .pipelineDelayMilliseconds = DSReadUInt16BigEndian(bytes + 18),
    };

    if (header.version != DSProtocolVersion ||
        (header.flags & ~(DSMediaFlagKeyframe | DSMediaFlagFEC)) != 0 ||
        header.payloadLength == 0 ||
        header.payloadLength > DSMediaMaximumPayload ||
        length != DSMediaHeaderSize + header.payloadLength) {
        return NO;
    }

    if (headerOut != NULL) {
        *headerOut = header;
    }
    if (payloadOut != NULL) {
        *payloadOut = bytes + DSMediaHeaderSize;
    }
    return YES;
}

static NSError *DSArgumentError(NSString *description) {
    return [NSError errorWithDomain:DSProtocolErrorDomain
                               code:DSProtocolErrorInvalidArgument
                           userInfo:@{NSLocalizedDescriptionKey: description}];
}

NSData *DSMakeMousePacket(uint32_t sequence,
                          DSMouseMode mode,
                          int32_t x,
                          int32_t y,
                          int32_t horizontalWheel,
                          int32_t verticalWheel,
                          NSError **error) {
    if (mode != DSMouseModeRelative && mode != DSMouseModeAbsolute) {
        if (error != NULL) *error = DSArgumentError(@"Unsupported mouse mode");
        return nil;
    }
    if (mode == DSMouseModeAbsolute && (x < 0 || x > 65535 || y < 0 || y > 65535)) {
        if (error != NULL) *error = DSArgumentError(@"Absolute mouse coordinates must be 0...65535");
        return nil;
    }

    uint8_t bytes[DSMousePacketSize] = {'D', 'S', 'M', 'I'};
    bytes[4] = DSProtocolVersion;
    bytes[5] = mode;
    DSWriteUInt32BigEndian(bytes + 8, sequence);
    DSWriteInt32BigEndian(bytes + 12, x);
    DSWriteInt32BigEndian(bytes + 16, y);
    DSWriteInt32BigEndian(bytes + 20, horizontalWheel);
    DSWriteInt32BigEndian(bytes + 24, verticalWheel);
    return [NSData dataWithBytes:bytes length:sizeof(bytes)];
}

NSData *DSMakeGamepadPacket(DSGamepadState state, NSError **error) {
    if (state.controllerID > 3) {
        if (error != NULL) *error = DSArgumentError(@"Controller ID must be 0...3");
        return nil;
    }

    uint8_t bytes[DSGamepadPacketSize] = {'D', 'S', 'G', 'P'};
    bytes[4] = DSProtocolVersion;
    bytes[5] = state.controllerID;
    DSWriteUInt16BigEndian(bytes + 6, state.buttons);
    bytes[8] = state.leftTrigger;
    bytes[9] = state.rightTrigger;
    DSWriteInt16BigEndian(bytes + 10, state.leftX);
    DSWriteInt16BigEndian(bytes + 12, state.leftY);
    DSWriteInt16BigEndian(bytes + 14, state.rightX);
    DSWriteInt16BigEndian(bytes + 16, state.rightY);
    DSWriteUInt32BigEndian(bytes + 18, state.sequence);
    // bytes 22...23 are reserved and remain zero.
    return [NSData dataWithBytes:bytes length:sizeof(bytes)];
}
