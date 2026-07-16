#import <Foundation/Foundation.h>

NS_ASSUME_NONNULL_BEGIN

FOUNDATION_EXPORT NSString * const DSProtocolErrorDomain;

typedef NS_ERROR_ENUM(DSProtocolErrorDomain, DSProtocolError) {
    DSProtocolErrorInvalidArgument = 1,
    DSProtocolErrorMalformedPacket = 2,
};

enum {
    DSProtocolVersion = 1,
    DSMediaHeaderSize = 20,
    DSMediaMaximumPayload = 1200,
    DSMousePacketSize = 28,
    DSGamepadPacketSize = 24,
    DSCursorPacketSize = 16,
    DSFrameTracePacketSize = 68,
};

typedef NS_OPTIONS(uint8_t, DSMediaFlags) {
    DSMediaFlagKeyframe = 1 << 0,
    DSMediaFlagFEC = 1 << 1,
};

typedef NS_ENUM(uint8_t, DSMouseMode) {
    DSMouseModeRelative = 0,
    DSMouseModeAbsolute = 1,
};

typedef struct {
    uint8_t version;
    uint8_t flags;
    uint16_t payloadLength;
    uint32_t frameID;
    uint16_t packetIndex;
    uint16_t packetCount;
    uint16_t fecCount;
    uint32_t presentationTimeMilliseconds;
    uint16_t pipelineDelayMilliseconds;
} DSMediaHeader;

typedef struct {
    uint32_t frameID;
    uint64_t captureStartMicroseconds;
    uint64_t captureEndMicroseconds;
    uint64_t convertEndMicroseconds;
    uint64_t encodeSubmitMicroseconds;
    uint64_t encodeFinishMicroseconds;
    uint64_t packetStartMicroseconds;
    uint64_t packetEndMicroseconds;
} DSFrameTrace;

typedef struct {
    uint8_t controllerID;
    uint16_t buttons;
    uint8_t leftTrigger;
    uint8_t rightTrigger;
    int16_t leftX;
    int16_t leftY;
    int16_t rightX;
    int16_t rightY;
    uint32_t sequence;
} DSGamepadState;

FOUNDATION_EXPORT uint16_t DSReadUInt16BigEndian(const uint8_t *bytes);
FOUNDATION_EXPORT uint32_t DSReadUInt32BigEndian(const uint8_t *bytes);
FOUNDATION_EXPORT uint64_t DSReadUInt64BigEndian(const uint8_t *bytes);
FOUNDATION_EXPORT uint64_t DSMonotonicMicroseconds(void);
FOUNDATION_EXPORT int16_t DSReadInt16BigEndian(const uint8_t *bytes);
FOUNDATION_EXPORT int32_t DSReadInt32BigEndian(const uint8_t *bytes);
FOUNDATION_EXPORT void DSWriteUInt16BigEndian(uint8_t *bytes, uint16_t value);
FOUNDATION_EXPORT void DSWriteUInt32BigEndian(uint8_t *bytes, uint32_t value);
FOUNDATION_EXPORT void DSWriteUInt64BigEndian(uint8_t *bytes, uint64_t value);
FOUNDATION_EXPORT void DSWriteInt16BigEndian(uint8_t *bytes, int16_t value);
FOUNDATION_EXPORT void DSWriteInt32BigEndian(uint8_t *bytes, int32_t value);

/// Returns YES only for a structurally valid v1 video/FEC datagram. Extra trailing bytes
/// are rejected so the caller never accidentally includes unrelated memory in an access unit.
FOUNDATION_EXPORT BOOL DSParseMediaDatagram(const void *bytes,
                                             size_t length,
                                             DSMediaHeader * _Nullable headerOut,
                                             const uint8_t * _Nullable * _Nullable payloadOut);

FOUNDATION_EXPORT BOOL DSParseFrameTraceDatagram(const void *bytes,
                                                  size_t length,
                                                  DSFrameTrace * _Nullable traceOut);

/// Serializes the fixed 28-byte DSMI packet. Absolute x/y values must be 0...65535.
FOUNDATION_EXPORT NSData * _Nullable DSMakeMousePacket(uint32_t sequence,
                                                        DSMouseMode mode,
                                                        int32_t x,
                                                        int32_t y,
                                                        int32_t horizontalWheel,
                                                        int32_t verticalWheel,
                                                        NSError **error);

/// Serializes the fixed 24-byte DSGP complete-state snapshot.
FOUNDATION_EXPORT NSData * _Nullable DSMakeGamepadPacket(DSGamepadState state,
                                                          NSError **error);

/// Standard wrap-safe ordering comparison for monotonic UInt32 protocol sequences.
FOUNDATION_EXPORT BOOL DSUInt32IsNewer(uint32_t candidate, uint32_t reference);

NS_ASSUME_NONNULL_END
