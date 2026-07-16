#import "DSFrameTraceCollector.h"

static const NSUInteger DSMaximumTraceEntries = 256;
static const NSUInteger DSFlushTraceRecordCount = 60;
static const unsigned long long DSMaximumTraceFileBytes = 64ULL * 1024ULL * 1024ULL;

@interface DSFrameTraceEntry : NSObject
@property(nonatomic) BOOL hasServer;
@property(nonatomic) DSFrameTrace server;
@property(nonatomic) uint64_t receiveFirst;
@property(nonatomic) uint64_t receiveLast;
@property(nonatomic) uint64_t assemble;
@property(nonatomic) uint64_t decodeSubmit;
@property(nonatomic) uint64_t presentEnqueue;
@end

@implementation DSFrameTraceEntry
@end

@implementation DSFrameTraceCollector {
    dispatch_queue_t _queue;
    NSMutableDictionary<NSNumber *, DSFrameTraceEntry *> *_entries;
    NSMutableArray<NSNumber *> *_arrivalOrder;
    NSMutableData *_pendingOutput;
    NSFileHandle *_output;
    NSURL *_traceURL;
    unsigned long long _writtenBytes;
    BOOL _hasClockOffset;
    int64_t _clockOffset;
    uint64_t _clockRoundTrip;
    uint64_t _clockSampleAt;
    NSUInteger _pendingRecords;
}

- (instancetype)init {
    self = [super init];
    if (self) {
        _queue = dispatch_queue_create("com.deskstream.macos.frame-trace", DISPATCH_QUEUE_SERIAL);
        _entries = [NSMutableDictionary dictionary];
        _arrivalOrder = [NSMutableArray array];
        _pendingOutput = [NSMutableData data];

        NSURL *logs = [[NSFileManager.defaultManager URLsForDirectory:NSLibraryDirectory
                                                            inDomains:NSUserDomainMask].firstObject
            URLByAppendingPathComponent:@"Logs/DeskStream" isDirectory:YES];
        [NSFileManager.defaultManager createDirectoryAtURL:logs
                               withIntermediateDirectories:YES attributes:nil error:NULL];
        _traceURL = [logs URLByAppendingPathComponent:@"frame-trace.jsonl"];
        NSDictionary *attributes = [NSFileManager.defaultManager attributesOfItemAtPath:_traceURL.path
                                                                                   error:NULL];
        if ([attributes fileSize] >= DSMaximumTraceFileBytes) {
            NSURL *previous = [logs URLByAppendingPathComponent:@"frame-trace.previous.jsonl"];
            [NSFileManager.defaultManager removeItemAtURL:previous error:NULL];
            [NSFileManager.defaultManager moveItemAtURL:_traceURL toURL:previous error:NULL];
        }
        if (![NSFileManager.defaultManager fileExistsAtPath:_traceURL.path]) {
            [NSFileManager.defaultManager createFileAtPath:_traceURL.path contents:nil attributes:nil];
        }
        _output = [NSFileHandle fileHandleForWritingToURL:_traceURL error:NULL];
        [_output seekToEndOfFile];
        _writtenBytes = [_output offsetInFile];
        if (_output) NSLog(@"[frame-trace] writing %@", _traceURL.path);
    }
    return self;
}

- (void)dealloc {
    [self flushOutput];
    [_output closeFile];
}

- (DSFrameTraceEntry *)entryForFrameID:(uint32_t)frameID {
    NSNumber *key = @(frameID);
    DSFrameTraceEntry *entry = _entries[key];
    if (entry) return entry;
    while (_entries.count >= DSMaximumTraceEntries && _arrivalOrder.count > 0) {
        NSNumber *oldest = _arrivalOrder.firstObject;
        [_arrivalOrder removeObjectAtIndex:0];
        [_entries removeObjectForKey:oldest];
    }
    entry = [[DSFrameTraceEntry alloc] init];
    _entries[key] = entry;
    [_arrivalOrder addObject:key];
    return entry;
}

- (void)recordServerTrace:(DSFrameTrace)trace {
    dispatch_async(_queue, ^{
        DSFrameTraceEntry *entry = [self entryForFrameID:trace.frameID];
        entry.server = trace;
        entry.hasServer = YES;
        [self emitFrameIDIfComplete:trace.frameID];
    });
}

- (void)recordReceiveForFrameID:(uint32_t)frameID atMicroseconds:(uint64_t)timestamp {
    dispatch_async(_queue, ^{
        DSFrameTraceEntry *entry = [self entryForFrameID:frameID];
        if (entry.receiveFirst == 0) entry.receiveFirst = timestamp;
        entry.receiveLast = timestamp;
    });
}

- (void)recordAssembleForFrameID:(uint32_t)frameID atMicroseconds:(uint64_t)timestamp {
    dispatch_async(_queue, ^{
        [self entryForFrameID:frameID].assemble = timestamp;
        [self emitFrameIDIfComplete:frameID];
    });
}

- (void)recordRendererSubmissionForFrameID:(uint32_t)frameID
                       decodeSubmitMicroseconds:(uint64_t)decodeSubmit
                      presentEnqueueMicroseconds:(uint64_t)presentEnqueue {
    dispatch_async(_queue, ^{
        DSFrameTraceEntry *entry = [self entryForFrameID:frameID];
        entry.decodeSubmit = decodeSubmit;
        entry.presentEnqueue = presentEnqueue;
        [self emitFrameIDIfComplete:frameID];
    });
}

- (void)updateClockOffsetMicroseconds:(int64_t)offset roundTripMicroseconds:(uint64_t)roundTrip {
    dispatch_async(_queue, ^{
        // Retain the lowest-RTT sample seen in the current stream. It minimizes scheduling and
        // queueing error; reset prevents a stale sample crossing reconnects.
        uint64_t now = DSMonotonicMicroseconds();
        BOOL sampleExpired = self->_clockSampleAt == 0 || now - self->_clockSampleAt >= 30000000ULL;
        if (!self->_hasClockOffset || sampleExpired || roundTrip < self->_clockRoundTrip) {
            self->_hasClockOffset = YES;
            self->_clockOffset = offset;
            self->_clockRoundTrip = roundTrip;
            self->_clockSampleAt = now;
        }
    });
}

- (void)emitFrameIDIfComplete:(uint32_t)frameID {
    NSNumber *key = @(frameID);
    DSFrameTraceEntry *entry = _entries[key];
    if (!entry.hasServer || entry.receiveFirst == 0 || entry.receiveLast == 0 ||
        entry.assemble == 0 || entry.decodeSubmit == 0 || entry.presentEnqueue == 0) return;

    DSFrameTrace server = entry.server;
    NSMutableDictionary *record = [@{
        @"frameId": @(frameID),
        @"captureStartUs": @(server.captureStartMicroseconds),
        @"captureEndUs": @(server.captureEndMicroseconds),
        @"gpuConvertSubmitUs": @(server.convertEndMicroseconds),
        @"encodeSubmitUs": @(server.encodeSubmitMicroseconds),
        @"encodeFinishUs": @(server.encodeFinishMicroseconds),
        @"packetStartUs": @(server.packetStartMicroseconds),
        @"packetEndUs": @(server.packetEndMicroseconds),
        @"receiveFirstUs": @(entry.receiveFirst),
        @"receiveLastUs": @(entry.receiveLast),
        @"assembleUs": @(entry.assemble),
        @"decodeSubmitUs": @(entry.decodeSubmit),
        @"presentEnqueueUs": @(entry.presentEnqueue),
        @"captureCallUs": @(server.captureEndMicroseconds - server.captureStartMicroseconds),
        @"gpuConvertSubmitDelayUs": @(server.convertEndMicroseconds - server.captureEndMicroseconds),
        @"encodeUs": @(server.encodeFinishMicroseconds - server.encodeSubmitMicroseconds),
        @"packetPacingUs": @(server.packetEndMicroseconds - server.packetStartMicroseconds),
        @"wireSpreadUs": @(entry.receiveLast - entry.receiveFirst),
        @"assembleAfterLastPacketUs": @(entry.assemble - entry.receiveLast),
        @"decodeSubmitAfterAssembleUs": @(entry.decodeSubmit - entry.assemble),
        @"presentEnqueueAfterDecodeSubmitUs": @(entry.presentEnqueue - entry.decodeSubmit),
        @"clientStageSemantics": @"AVSampleBufferDisplayLayer submit/enqueue; not hardware decode/present completion",
    } mutableCopy];
    if (_hasClockOffset) {
        int64_t packetStartClient = (int64_t)server.packetStartMicroseconds - _clockOffset;
        int64_t packetEndClient = (int64_t)server.packetEndMicroseconds - _clockOffset;
        record[@"clockOffsetUs"] = @(_clockOffset);
        record[@"clockRttUs"] = @(_clockRoundTrip);
        record[@"firstTransitUs"] = @((int64_t)entry.receiveFirst - packetStartClient);
        record[@"tailTransitUs"] = @((int64_t)entry.receiveLast - packetEndClient);
        record[@"captureToPresentEnqueueUs"] = @((int64_t)entry.presentEnqueue -
                                                  ((int64_t)server.captureStartMicroseconds - _clockOffset));
    }

    NSData *json = [NSJSONSerialization dataWithJSONObject:record options:0 error:NULL];
    if (json) {
        [_pendingOutput appendData:json];
        const uint8_t newline = '\n';
        [_pendingOutput appendBytes:&newline length:1];
        _pendingRecords++;
    }
    [_entries removeObjectForKey:key];
    [_arrivalOrder removeObject:key];
    if (_pendingRecords >= DSFlushTraceRecordCount) [self flushOutput];
}

- (void)flushOutput {
    if (!_output || _pendingOutput.length == 0) return;
    if (_writtenBytes + _pendingOutput.length > DSMaximumTraceFileBytes) {
        [_output closeFile];
        NSURL *previous = [[_traceURL URLByDeletingLastPathComponent]
            URLByAppendingPathComponent:@"frame-trace.previous.jsonl"];
        [NSFileManager.defaultManager removeItemAtURL:previous error:NULL];
        [NSFileManager.defaultManager moveItemAtURL:_traceURL toURL:previous error:NULL];
        [NSFileManager.defaultManager createFileAtPath:_traceURL.path contents:nil attributes:nil];
        _output = [NSFileHandle fileHandleForWritingToURL:_traceURL error:NULL];
        _writtenBytes = 0;
    }
    [_output writeData:_pendingOutput];
    _writtenBytes += _pendingOutput.length;
    [_output synchronizeFile];
    [_pendingOutput setLength:0];
    _pendingRecords = 0;
}

- (void)reset {
    dispatch_async(_queue, ^{
        [self flushOutput];
        [self->_entries removeAllObjects];
        [self->_arrivalOrder removeAllObjects];
        self->_hasClockOffset = NO;
        self->_clockOffset = 0;
        self->_clockRoundTrip = 0;
        self->_clockSampleAt = 0;
    });
}

@end
