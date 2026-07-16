#import "DSFrameAssembler.h"

#import "DSProtocol.h"

// Assembly state is not a render/jitter queue. Four frames tolerates ordinary Wi-Fi datagram
// reordering without adding presentation latency (completed frames still leave immediately).
static const NSUInteger DSMaximumInflightFrames = 4;
static const uint16_t DSMaximumPacketCount = 4096;
static const uint16_t DSFECInterleave = 4;

@interface DSInflightFrame : NSObject

@property(nonatomic, readonly) uint32_t frameID;
@property(nonatomic, readonly) uint16_t packetCount;
@property(nonatomic, readonly) uint16_t fecCount;
@property(nonatomic, readonly) BOOL keyframe;
@property(nonatomic, readonly) uint32_t presentationTimeMilliseconds;
@property(nonatomic, readonly) uint16_t pipelineDelayMilliseconds;
@property(nonatomic, readonly) NSMutableData *assembly;
@property(nonatomic, readonly) BOOL complete;
@property(nonatomic, readonly) NSInteger lastPacketLength;

- (instancetype)initWithHeader:(DSMediaHeader)header;
- (void)acceptDataHeader:(DSMediaHeader)header payload:(const uint8_t *)payload;
- (void)acceptFECHeader:(DSMediaHeader)header payload:(const uint8_t *)payload;
- (BOOL)matchesHeader:(DSMediaHeader)header;

@end

@implementation DSInflightFrame {
    NSMutableData *_dataPresent;
    NSUInteger _presentCount;
    NSMutableArray<id> *_fecPayloads;
    NSMutableData *_fecLengths;
    NSInteger _lastPacketLength;
}

- (instancetype)initWithHeader:(DSMediaHeader)header {
    self = [super init];
    if (self) {
        _frameID = header.frameID;
        _packetCount = header.packetCount;
        _fecCount = header.fecCount;
        _keyframe = (header.flags & DSMediaFlagKeyframe) != 0;
        _presentationTimeMilliseconds = header.presentationTimeMilliseconds;
        _pipelineDelayMilliseconds = header.pipelineDelayMilliseconds;
        _assembly = [NSMutableData dataWithLength:(NSUInteger)header.packetCount * DSMediaMaximumPayload];
        _dataPresent = [NSMutableData dataWithLength:header.packetCount];
        _fecPayloads = [NSMutableArray arrayWithCapacity:header.fecCount];
        for (NSUInteger index = 0; index < header.fecCount; index++) {
            [_fecPayloads addObject:NSNull.null];
        }
        _fecLengths = [NSMutableData dataWithLength:(NSUInteger)header.fecCount * sizeof(uint16_t)];
        uint16_t *lengths = _fecLengths.mutableBytes;
        for (NSUInteger index = 0; index < header.fecCount; index++) {
            lengths[index] = UINT16_MAX;
        }
        _lastPacketLength = -1;
    }
    return self;
}

- (NSInteger)lastPacketLength {
    return _lastPacketLength;
}

- (BOOL)complete {
    return _presentCount == _packetCount && _lastPacketLength >= 0;
}

- (BOOL)matchesHeader:(DSMediaHeader)header {
    return header.frameID == _frameID &&
           header.packetCount == _packetCount &&
           header.fecCount == _fecCount &&
           ((header.flags & DSMediaFlagKeyframe) != 0) == _keyframe &&
           header.presentationTimeMilliseconds == _presentationTimeMilliseconds &&
           header.pipelineDelayMilliseconds == _pipelineDelayMilliseconds;
}

- (void)acceptDataHeader:(DSMediaHeader)header payload:(const uint8_t *)payload {
    NSUInteger index = header.packetIndex;
    uint8_t *present = _dataPresent.mutableBytes;
    if (present[index] != 0) return;

    memcpy((uint8_t *)_assembly.mutableBytes + index * DSMediaMaximumPayload,
           payload,
           header.payloadLength);
    present[index] = 1;
    _presentCount++;
    if (index == _packetCount - 1) {
        _lastPacketLength = header.payloadLength;
    }
    [self tryRecoverGroup:(uint16_t)(index % DSFECInterleave)];
}

- (void)acceptFECHeader:(DSMediaHeader)header payload:(const uint8_t *)payload {
    NSUInteger group = header.packetIndex;
    uint16_t *lengths = _fecLengths.mutableBytes;
    if (lengths[group] != UINT16_MAX) return;

    _fecPayloads[group] = [NSData dataWithBytes:payload length:header.payloadLength];
    lengths[group] = header.payloadLength;
    [self tryRecoverGroup:(uint16_t)group];
}

- (void)tryRecoverGroup:(uint16_t)group {
    if (group >= _fecCount) return;
    uint16_t *fecLengths = _fecLengths.mutableBytes;
    uint16_t parityLength = fecLengths[group];
    if (parityLength == UINT16_MAX) return;

    uint8_t *present = _dataPresent.mutableBytes;
    NSInteger missingIndex = -1;
    NSUInteger missingCount = 0;
    for (NSUInteger index = group; index < _packetCount; index += DSFECInterleave) {
        if (present[index] == 0) {
            missingIndex = (NSInteger)index;
            missingCount++;
        }
    }

    if (missingCount == 0) {
        _fecPayloads[group] = NSNull.null;
        fecLengths[group] = UINT16_MAX;
        return;
    }
    if (missingCount != 1) return;

    NSData *parity = _fecPayloads[group];
    if (![parity isKindOfClass:NSData.class]) return;
    const uint8_t *parityBytes = parity.bytes;
    uint8_t *assemblyBytes = _assembly.mutableBytes;
    uint8_t *destination = assemblyBytes + (NSUInteger)missingIndex * DSMediaMaximumPayload;

    for (NSUInteger byteIndex = 0; byteIndex < parityLength; byteIndex++) {
        uint8_t value = parityBytes[byteIndex];
        for (NSUInteger index = group; index < _packetCount; index += DSFECInterleave) {
            if ((NSInteger)index == missingIndex || present[index] == 0) continue;
            NSInteger packetLength = index == _packetCount - 1
                ? _lastPacketLength
                : DSMediaMaximumPayload;
            if (packetLength > 0 && byteIndex < (NSUInteger)packetLength) {
                value ^= assemblyBytes[index * DSMediaMaximumPayload + byteIndex];
            }
        }
        destination[byteIndex] = value;
    }

    present[missingIndex] = 1;
    _presentCount++;
    if ((NSUInteger)missingIndex == _packetCount - 1) {
        // The protocol defines parityLength as the recovered final packet length; any bytes
        // beyond the real Annex-B unit are harmless trailing_zero_8bits.
        _lastPacketLength = parityLength;
    }
    _fecPayloads[group] = NSNull.null;
    fecLengths[group] = UINT16_MAX;
}

@end

@implementation DSFrameAssembler {
    DSFrameOutputHandler _outputHandler;
    DSFrameDropHandler _dropHandler;
    NSMutableDictionary<NSNumber *, DSInflightFrame *> *_frames;
    NSMutableArray<NSNumber *> *_arrivalOrder;
    BOOL _discardingUntilKeyframe;
    BOOL _hasDropWatermark;
    uint32_t _dropWatermark;
    BOOL _hasLastOutputFrame;
    uint32_t _lastOutputFrameID;
}

- (instancetype)initWithOutputHandler:(DSFrameOutputHandler)outputHandler
                           dropHandler:(DSFrameDropHandler)dropHandler {
    NSParameterAssert(outputHandler != nil);
    NSParameterAssert(dropHandler != nil);
    self = [super init];
    if (self) {
        _outputHandler = [outputHandler copy];
        _dropHandler = [dropHandler copy];
        _frames = [NSMutableDictionary dictionary];
        _arrivalOrder = [NSMutableArray array];
    }
    return self;
}

- (NSUInteger)incompleteFrameCount {
    @synchronized (self) { return _frames.count; }
}

- (BOOL)discardingUntilKeyframe {
    @synchronized (self) { return _discardingUntilKeyframe; }
}

- (void)consumeDatagram:(NSData *)datagram {
    [self consumeBytes:datagram.bytes length:datagram.length];
}

- (void)consumeBytes:(const void *)bytes length:(size_t)length {
    @synchronized (self) {
    DSMediaHeader header;
    const uint8_t *payload = NULL;
    if (!DSParseMediaDatagram(bytes, length, &header, &payload) ||
        header.packetCount == 0 ||
        header.packetCount > DSMaximumPacketCount ||
        header.fecCount != MIN(DSFECInterleave, header.packetCount)) {
        return;
    }

    BOOL fec = (header.flags & DSMediaFlagFEC) != 0;
    if (fec) {
        if (header.packetIndex >= header.fecCount) return;
        NSUInteger group = header.packetIndex;
        NSUInteger memberCount = (header.packetCount - group + DSFECInterleave - 1) /
                                 DSFECInterleave;
        // A multi-member interleaved group always contains a full non-final packet.
        if (memberCount > 1 &&
            header.payloadLength != DSMediaMaximumPayload) return;
    } else {
        if (header.packetIndex >= header.packetCount) return;
        if (header.packetIndex != header.packetCount - 1 &&
            header.payloadLength != DSMediaMaximumPayload) return;
    }

    if (_hasDropWatermark && !DSUInt32IsNewer(header.frameID, _dropWatermark)) return;

    BOOL keyframe = (header.flags & DSMediaFlagKeyframe) != 0;
    if (_discardingUntilKeyframe && !keyframe) return;

    BOOL notifyDrop = NO;
    NSNumber *key = @(header.frameID);
    DSInflightFrame *frame = _frames[key];
    if (frame == nil) {
        if (_frames.count >= DSMaximumInflightFrames) {
            NSNumber *oldestKey = key;
            for (NSNumber *existingKey in _arrivalOrder) {
                if (DSUInt32IsNewer(oldestKey.unsignedIntValue, existingKey.unsignedIntValue)) {
                    oldestKey = existingKey;
                }
            }
            // If the arriving datagram belongs to the oldest frame, refuse it instead of
            // evicting newer assembly state. Any capacity drop still invalidates references.
            if ([oldestKey isEqual:key]) {
                [self advanceWatermark:header.frameID];
                _discardingUntilKeyframe = YES;
                _dropHandler();
                return;
            }
            if (oldestKey != nil) {
                [_arrivalOrder removeObject:oldestKey];
                [_frames removeObjectForKey:oldestKey];
                [self advanceWatermark:oldestKey.unsignedIntValue];
                _discardingUntilKeyframe = YES;
                notifyDrop = YES;
            }
            if (_discardingUntilKeyframe && !keyframe) {
                if (notifyDrop) _dropHandler();
                return;
            }
        }
        frame = [[DSInflightFrame alloc] initWithHeader:header];
        _frames[key] = frame;
        [_arrivalOrder addObject:key];
    } else if (![frame matchesHeader:header]) {
        return;
    }

    if (fec) {
        [frame acceptFECHeader:header payload:payload];
    } else {
        [frame acceptDataHeader:header payload:payload];
    }
    if (!frame.complete) {
        if (notifyDrop) _dropHandler();
        return;
    }

    [_frames removeObjectForKey:key];
    [_arrivalOrder removeObject:key];

    // Completing a newer frame makes any older incomplete reference frame unusable.
    NSArray<NSNumber *> *snapshot = _arrivalOrder.copy;
    for (NSNumber *otherKey in snapshot) {
        if (DSUInt32IsNewer(header.frameID, otherKey.unsignedIntValue)) {
            [_frames removeObjectForKey:otherKey];
            [_arrivalOrder removeObject:otherKey];
            [self advanceWatermark:otherKey.unsignedIntValue];
            _discardingUntilKeyframe = YES;
            notifyDrop = YES;
        }
    }
    [self advanceWatermark:header.frameID];

    NSData *output = nil;
    if (!_discardingUntilKeyframe &&
        _hasLastOutputFrame &&
        !frame.keyframe &&
        header.frameID != _lastOutputFrameID + 1) {
        // It is possible to lose every packet of a frame, leaving no incomplete assembly
        // object to evict. Detect that reference gap from the monotonic frame ID itself.
        _discardingUntilKeyframe = YES;
        notifyDrop = YES;
    }
    if (_discardingUntilKeyframe) {
        if (frame.keyframe) {
            _discardingUntilKeyframe = NO;
        }
    }
    if (!_discardingUntilKeyframe) {
        NSUInteger outputLength = ((NSUInteger)frame.packetCount - 1) * DSMediaMaximumPayload +
                                  (NSUInteger)frame.lastPacketLength;
        output = [NSData dataWithBytes:frame.assembly.bytes length:outputLength];
        _lastOutputFrameID = frame.frameID;
        _hasLastOutputFrame = YES;
    }

    if (notifyDrop) _dropHandler();
    if (output != nil) {
        _outputHandler(output,
                       frame.keyframe,
                       frame.frameID,
                       frame.presentationTimeMilliseconds,
                       frame.pipelineDelayMilliseconds);
    }
    }
}

- (void)advanceWatermark:(uint32_t)frameID {
    if (!_hasDropWatermark || DSUInt32IsNewer(frameID, _dropWatermark)) {
        _dropWatermark = frameID;
        _hasDropWatermark = YES;
    }
}

- (void)requestDiscardUntilKeyframe {
    @synchronized (self) { _discardingUntilKeyframe = YES; }
}

- (void)reset {
    @synchronized (self) {
    [_frames removeAllObjects];
    [_arrivalOrder removeAllObjects];
    _discardingUntilKeyframe = NO;
    _hasDropWatermark = NO;
    _dropWatermark = 0;
    _hasLastOutputFrame = NO;
    _lastOutputFrameID = 0;
    }
}

@end
