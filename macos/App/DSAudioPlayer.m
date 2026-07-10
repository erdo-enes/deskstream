#import "DSAudioPlayer.h"

#import <AVFAudio/AVFAudio.h>

static uint16_t DSReadU16(const uint8_t *p) {
    return (uint16_t)((uint16_t)p[0] << 8 | p[1]);
}

static uint32_t DSReadU32(const uint8_t *p) {
    return (uint32_t)p[0] << 24 | (uint32_t)p[1] << 16 | (uint32_t)p[2] << 8 | p[3];
}

static const void *DSAudioQueueKey = &DSAudioQueueKey;

@interface DSAudioPlayer ()
@property (nonatomic) dispatch_queue_t queue;
@property (nonatomic, strong) AVAudioEngine *engine;
@property (nonatomic, strong) AVAudioPlayerNode *player;
@property (nonatomic, strong) AVAudioFormat *format;
@property (nonatomic, strong) NSMutableArray<AVAudioPCMBuffer *> *freeBuffers;
@property (nonatomic, copy) NSData *silencePacket;
@property (nonatomic) NSUInteger packetSamples;
@property (nonatomic) NSUInteger payloadBytes;
@property (nonatomic) NSUInteger queuedBuffers;
@property (nonatomic) BOOL running;
@property (nonatomic) BOOL hasSequence;
@property (nonatomic) uint32_t expectedSequence;
@property (atomic, readwrite) NSUInteger outputDrops;
@property (atomic, readwrite) uint64_t packetsLost;
@property (nonatomic) NSUInteger generation;
@property (nonatomic) NSUInteger pendingDatagrams;
@property (nonatomic) BOOL acceptingDatagrams;
@end

@implementation DSAudioPlayer

@synthesize muted = _muted;

- (instancetype)init {
    self = [super init];
    if (self) {
        _queue = dispatch_queue_create("com.deskstream.macos.audio", DISPATCH_QUEUE_SERIAL);
        dispatch_queue_set_specific(_queue, DSAudioQueueKey, (void *)DSAudioQueueKey, NULL);
        _freeBuffers = [NSMutableArray array];
    }
    return self;
}

- (void)dealloc {
    [self stop];
}

- (BOOL)startWithSampleRate:(double)sampleRate
                   channels:(NSUInteger)channels
              packetSamples:(NSUInteger)packetSamples
                      error:(NSError **)error {
    if (sampleRate <= 0 || channels != 2 || packetSamples == 0) {
        if (error) *error = [NSError errorWithDomain:@"DeskStreamAudio" code:1
                                            userInfo:@{NSLocalizedDescriptionKey: @"Unsupported audio format"}];
        return NO;
    }

    __block BOOL started = NO;
    __block NSError *startError = nil;
    @synchronized (self) { self.acceptingDatagrams = NO; }
    [self performSynchronouslyOnQueue:^{
        [self stopOnQueue];
        AVAudioFormat *format = [[AVAudioFormat alloc]
            initWithCommonFormat:AVAudioPCMFormatInt16
                      sampleRate:sampleRate
                        channels:(AVAudioChannelCount)channels
                     interleaved:YES];
        if (!format) {
            startError = [NSError errorWithDomain:@"DeskStreamAudio" code:2
                userInfo:@{NSLocalizedDescriptionKey: @"Could not create the PCM output format"}];
            return;
        }

        AVAudioEngine *engine = [[AVAudioEngine alloc] init];
        AVAudioPlayerNode *player = [[AVAudioPlayerNode alloc] init];
        [engine attachNode:player];
        [engine connect:player to:engine.mainMixerNode format:format];
        [engine prepare];
        if (![engine startAndReturnError:&startError]) {
            [engine detachNode:player];
            return;
        }
        player.volume = self.muted ? 0.0f : 1.0f;
        [player play];

        self.engine = engine;
        self.player = player;
        self.format = format;
        self.packetSamples = packetSamples;
        self.payloadBytes = packetSamples * channels * sizeof(int16_t);
        self.silencePacket = [NSMutableData dataWithLength:self.payloadBytes];
        self.queuedBuffers = 0;
        self.hasSequence = NO;
        self.expectedSequence = 0;
        self.outputDrops = 0;
        self.packetsLost = 0;
        self.generation++;
        self.running = YES;
        [self.freeBuffers removeAllObjects];
        for (NSUInteger i = 0; i < 8; i++) {
            AVAudioPCMBuffer *buffer = [[AVAudioPCMBuffer alloc]
                initWithPCMFormat:format frameCapacity:(AVAudioFrameCount)packetSamples];
            if (buffer) [self.freeBuffers addObject:buffer];
        }
        if (self.freeBuffers.count == 0) {
            startError = [NSError errorWithDomain:@"DeskStreamAudio" code:3
                userInfo:@{NSLocalizedDescriptionKey: @"Could not allocate audio buffers"}];
            [self stopOnQueue];
            return;
        }
        @synchronized (self) { self.acceptingDatagrams = YES; }
        started = YES;
    }];
    if (!started && error) *error = startError;
    return started;
}

- (BOOL)isMuted {
    @synchronized (self) { return _muted; }
}

- (void)setMuted:(BOOL)muted {
    @synchronized (self) { _muted = muted; }
    dispatch_async(self.queue, ^{ self.player.volume = muted ? 0.0f : 1.0f; });
}

- (void)consumeAudioDatagram:(NSData *)datagram {
    if (datagram.length < 16) return;
    @synchronized (self) {
        if (!self.acceptingDatagrams) return;
        if (self.pendingDatagrams >= 8) {
            self.outputDrops++;
            return;
        }
        self.pendingDatagrams++;
    }
    // The UDP receiver is allowed to reuse a mutable receive buffer immediately after this
    // call, so take an immutable snapshot before crossing queues.
    NSData *ownedDatagram = [datagram copy];
    dispatch_async(self.queue, ^{
        [self consumeOnQueue:ownedDatagram];
        @synchronized (self) {
            if (self.pendingDatagrams > 0) self.pendingDatagrams--;
        }
    });
}

- (void)consumeOnQueue:(NSData *)datagram {
    if (!self.running) return;
    const uint8_t *bytes = datagram.bytes;
    uint16_t payloadLength = DSReadU16(bytes + 2);
    uint32_t sequence = DSReadU32(bytes + 4);
    uint16_t sampleCount = DSReadU16(bytes + 12);
    if (bytes[0] != 1 || bytes[1] != 1 || bytes[14] != 0 || bytes[15] != 0 ||
        sampleCount != self.packetSamples || payloadLength != self.payloadBytes ||
        datagram.length != 16 + payloadLength) return;

    NSUInteger missing = 0;
    if (self.hasSequence) {
        int32_t delta = (int32_t)(sequence - self.expectedSequence);
        if (delta < 0) return; // stale/reordered
        missing = (NSUInteger)delta;
        self.packetsLost += missing;
        if (missing > 4) {
            // A large discontinuity should not leave old audio queued. Recreate the tiny
            // player queue and anchor immediately to the newest packet.
            [self resetScheduledAudio];
            missing = 0;
        }
    }
    self.hasSequence = YES;
    self.expectedSequence = sequence + 1;

    for (NSUInteger i = 0; i < missing; i++) {
        [self schedulePCM:self.silencePacket.bytes length:self.payloadBytes];
    }
    [self schedulePCM:bytes + 16 length:payloadLength];
}

- (void)schedulePCM:(const void *)pcm length:(NSUInteger)length {
    if (!self.running || self.queuedBuffers >= 6 || self.freeBuffers.count == 0) {
        [self incrementOutputDrops];
        return;
    }
    AVAudioPCMBuffer *buffer = self.freeBuffers.lastObject;
    [self.freeBuffers removeLastObject];
    buffer.frameLength = (AVAudioFrameCount)self.packetSamples;
    AudioBufferList *list = buffer.mutableAudioBufferList;
    if (!list || list->mNumberBuffers < 1 || !list->mBuffers[0].mData ||
        list->mBuffers[0].mDataByteSize < length) {
        [self.freeBuffers addObject:buffer];
        [self incrementOutputDrops];
        return;
    }
    memcpy(list->mBuffers[0].mData, pcm, length);
    list->mBuffers[0].mDataByteSize = (UInt32)length;
    self.queuedBuffers++;
    NSUInteger generation = self.generation;
    __weak typeof(self) weakSelf = self;
    [self.player scheduleBuffer:buffer
         completionCallbackType:AVAudioPlayerNodeCompletionDataRendered
              completionHandler:^(AVAudioPlayerNodeCompletionCallbackType callbackType) {
        (void)callbackType;
        typeof(self) selfRef = weakSelf;
        if (!selfRef) return;
        dispatch_async(selfRef.queue, ^{
            if (generation != selfRef.generation) return;
            if (selfRef.queuedBuffers > 0) selfRef.queuedBuffers--;
            [selfRef.freeBuffers addObject:buffer];
        });
    }];
}

- (void)resetScheduledAudio {
    self.generation++;
    [self.player stop];
    self.queuedBuffers = 0;
    [self.freeBuffers removeAllObjects];
    for (NSUInteger i = 0; i < 8; i++) {
        AVAudioPCMBuffer *buffer = [[AVAudioPCMBuffer alloc]
            initWithPCMFormat:self.format frameCapacity:(AVAudioFrameCount)self.packetSamples];
        if (buffer) [self.freeBuffers addObject:buffer];
    }
    [self.player play];
}

- (void)stop {
    @synchronized (self) { self.acceptingDatagrams = NO; }
    [self performSynchronouslyOnQueue:^{ [self stopOnQueue]; }];
}

- (void)stopOnQueue {
    self.running = NO;
    self.generation++;
    [self.player stop];
    [self.engine stop];
    if (self.player && self.engine) [self.engine detachNode:self.player];
    self.player = nil;
    self.engine = nil;
    self.format = nil;
    self.silencePacket = nil;
    self.queuedBuffers = 0;
    self.hasSequence = NO;
    [self.freeBuffers removeAllObjects];
}

- (void)incrementOutputDrops {
    @synchronized (self) { self.outputDrops++; }
}

- (void)performSynchronouslyOnQueue:(dispatch_block_t)block {
    if (dispatch_get_specific(DSAudioQueueKey) == DSAudioQueueKey) block();
    else dispatch_sync(self.queue, block);
}

@end
