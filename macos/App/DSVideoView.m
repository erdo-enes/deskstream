#import "DSVideoView.h"

#import "DSProtocol.h"

#import <AVFoundation/AVFoundation.h>
#import <CoreImage/CoreImage.h>
#import <CoreMedia/CoreMedia.h>
#import <CoreVideo/CoreVideo.h>
#import <QuartzCore/QuartzCore.h>
#import <VideoToolbox/VideoToolbox.h>

// One NAL unit located in place inside the access-unit buffer: no per-NAL copy is made.
typedef struct {
    NSUInteger offset;
    NSUInteger length;
    uint8_t type;
} DSNALRange;

static NSUInteger DSFindStartCode(const uint8_t *bytes, NSUInteger length,
                                  NSUInteger from, NSUInteger *codeLength) {
    for (NSUInteger i = from; i + 3 <= length; i++) {
        if (i + 4 <= length && bytes[i] == 0 && bytes[i + 1] == 0 &&
            bytes[i + 2] == 0 && bytes[i + 3] == 1) {
            *codeLength = 4;
            return i;
        }
        if (bytes[i] == 0 && bytes[i + 1] == 0 && bytes[i + 2] == 1) {
            *codeLength = 3;
            return i;
        }
    }
    return NSNotFound;
}

// Locate the Annex-B NAL units in `bytes` and append a {offset,length,type} record for each to
// `out`. No payload bytes are copied: callers index back into `bytes`. Trailing_zero_8bits are
// stripped from each NAL, which also removes the harmless zero padding FEC leaves on the final
// reconstructed datagram (decoders consume NALs by start code, so this is purely hygiene).
static void DSAppendNALRanges(const uint8_t *bytes, NSUInteger length, NSMutableData *out) {
    NSUInteger codeLength = 0;
    NSUInteger start = DSFindStartCode(bytes, length, 0, &codeLength);
    while (start != NSNotFound) {
        NSUInteger payloadStart = start + codeLength;
        NSUInteger nextCodeLength = 0;
        NSUInteger next = DSFindStartCode(bytes, length, payloadStart, &nextCodeLength);
        NSUInteger payloadEnd = next == NSNotFound ? length : next;
        while (payloadEnd > payloadStart && bytes[payloadEnd - 1] == 0) payloadEnd--;
        if (payloadEnd > payloadStart) {
            DSNALRange range = {
                .offset = payloadStart,
                .length = payloadEnd - payloadStart,
                .type = (uint8_t)(bytes[payloadStart] & 0x1F),
            };
            [out appendBytes:&range length:sizeof(range)];
        }
        if (next == NSNotFound) break;
        start = next;
        codeLength = nextCodeLength;
    }
}

@interface DSVideoView () {
    CMVideoFormatDescriptionRef _formatDescription;
    VTDecompressionSessionRef _decompressionSession;
    CVPixelBufferRef _lastPixelBuffer;
    BOOL _screenshotEnabled;
    BOOL _rendererFlushInProgress;
    BOOL _waitingForKeyframe;
    BOOL _requestIDRAfterFlush;
    NSUInteger _rendererFlushToken;
}
@property (nonatomic, strong) AVSampleBufferDisplayLayer *displayLayer;
@property (nonatomic) dispatch_queue_t conversionQueue;
@property (nonatomic, copy) NSData *sps;
@property (nonatomic, copy) NSData *pps;
@property (nonatomic) NSUInteger queuedFrames;
@property (nonatomic) NSUInteger rendererGeneration;
@property (nonatomic, readwrite) BOOL hasPicture;
@property (nonatomic, strong) CIContext *ciContext;
@end

@implementation DSVideoView

- (instancetype)initWithFrame:(NSRect)frameRect {
    self = [super initWithFrame:frameRect];
    if (self) {
        self.wantsLayer = YES;
        self.layerContentsRedrawPolicy = NSViewLayerContentsRedrawOnSetNeedsDisplay;
        _displayLayer = [AVSampleBufferDisplayLayer layer];
        _displayLayer.videoGravity = AVLayerVideoGravityResizeAspect;
        _displayLayer.backgroundColor = NSColor.blackColor.CGColor;
        _displayLayer.frame = self.bounds;
        _displayLayer.autoresizingMask = kCALayerWidthSizable | kCALayerHeightSizable;
        self.layer = _displayLayer;
        dispatch_queue_attr_t conversionAttributes = dispatch_queue_attr_make_with_qos_class(
            DISPATCH_QUEUE_SERIAL, QOS_CLASS_USER_INTERACTIVE, 0);
        _conversionQueue = dispatch_queue_create("com.deskstream.macos.h264", conversionAttributes);

        if (@available(macOS 14.0, *)) {
            [[NSNotificationCenter defaultCenter]
                addObserver:self
                   selector:@selector(rendererFailed:)
                       name:AVSampleBufferVideoRendererDidFailToDecodeNotification
                     object:_displayLayer.sampleBufferRenderer];
        } else {
            [[NSNotificationCenter defaultCenter]
                addObserver:self
                   selector:@selector(rendererFailed:)
                       name:AVSampleBufferDisplayLayerFailedToDecodeNotification
                     object:_displayLayer];
        }
    }
    return self;
}

- (BOOL)acceptsFirstResponder { return YES; }

- (void)dealloc {
    [[NSNotificationCenter defaultCenter] removeObserver:self];
    if (_formatDescription) CFRelease(_formatDescription);
    if (_decompressionSession) {
        VTDecompressionSessionInvalidate(_decompressionSession);
        CFRelease(_decompressionSession);
    }
    if (_lastPixelBuffer) CVPixelBufferRelease(_lastPixelBuffer);
}

- (void)rendererFailed:(NSNotification *)notification {
    (void)notification;
    [self beginRendererFailureRecovery];
}

// Full teardown that also removes the displayed image. Used only on stream start/stop, where a
// black frame is the correct state until the first keyframe of the new stream arrives.
- (void)resetRenderer {
    VTDecompressionSessionRef screenshotSession = NULL;
    CVPixelBufferRef screenshotPixelBuffer = NULL;
    NSUInteger flushToken = 0;
    @synchronized (self) {
        self.rendererGeneration++;
        self.hasPicture = NO;
        _waitingForKeyframe = YES;
        _rendererFlushInProgress = YES;
        _requestIDRAfterFlush = NO;
        flushToken = ++_rendererFlushToken;
        self.sps = nil;
        self.pps = nil;
        if (_formatDescription) {
            CFRelease(_formatDescription);
            _formatDescription = NULL;
        }
        _screenshotEnabled = NO;
        screenshotSession = _decompressionSession;
        _decompressionSession = NULL;
        screenshotPixelBuffer = _lastPixelBuffer;
        _lastPixelBuffer = NULL;
    }
    if (screenshotSession) {
        VTDecompressionSessionInvalidate(screenshotSession);
        CFRelease(screenshotSession);
    }
    if (screenshotPixelBuffer) CVPixelBufferRelease(screenshotPixelBuffer);
    [self performRendererFlushRemovingImage:YES token:flushToken];
}

// Renderer flushes reset H.264 decoder state. On macOS 14 they are asynchronous, so no recovery
// IDR may be requested (or accepted) until the completion handler runs. All renderer calls are
// initiated from conversionQueue, and the state gate rejects any access unit that races the flush.
- (void)performRendererFlushRemovingImage:(BOOL)removeImage token:(NSUInteger)token {
    dispatch_async(self.conversionQueue, ^{
        if (@available(macOS 14.0, *)) {
            [self.displayLayer.sampleBufferRenderer
                flushWithRemovalOfDisplayedImage:removeImage
                completionHandler:^{ [self rendererFlushCompletedForToken:token]; }];
        } else {
            dispatch_async(dispatch_get_main_queue(), ^{
#pragma clang diagnostic push
#pragma clang diagnostic ignored "-Wdeprecated-declarations"
                if (removeImage) [self.displayLayer flushAndRemoveImage];
                else [self.displayLayer flush];
#pragma clang diagnostic pop
                [self rendererFlushCompletedForToken:token];
            });
        }
    });
}

- (void)rendererFlushCompletedForToken:(NSUInteger)token {
    BOOL requestIDR = NO;
    @synchronized (self) {
        if (token != _rendererFlushToken) return;
        _rendererFlushInProgress = NO;
        requestIDR = _requestIDRAfterFlush;
        _requestIDRAfterFlush = NO;
    }
    if (requestIDR && self.requestIDRHandler) self.requestIDRHandler();
}

- (void)requestKeyframeWhenRendererReady {
    BOOL requestNow = NO;
    @synchronized (self) {
        if (_rendererFlushInProgress) _requestIDRAfterFlush = YES;
        else requestNow = YES;
    }
    if (requestNow && self.requestIDRHandler) self.requestIDRHandler();
}

- (void)notifyAssemblerOfReferenceDrop {
    if (self.discardUntilKeyframeHandler) self.discardUntilKeyframeHandler();
}

// A locally dropped compressed frame breaks the H.264 reference chain, but a healthy renderer
// does not need flushing: the replacement IDR resets that chain by itself. Avoiding a flush here
// is what prevents ordinary backpressure from becoming an asynchronous flush storm.
- (void)beginReferenceDropRecovery {
    BOOL requestNow = NO;
    @synchronized (self) {
        if (!_waitingForKeyframe) {
            _waitingForKeyframe = YES;
            self.rendererGeneration++;
        }
        if (_rendererFlushInProgress) _requestIDRAfterFlush = YES;
        else requestNow = YES;
    }
    [self notifyAssemblerOfReferenceDrop];
    if (requestNow && self.requestIDRHandler) self.requestIDRHandler();
}

// A failed renderer really does require a flush. Start at most one, preserve the displayed image,
// and defer the IDR until AVFoundation confirms that its decoder state has been reset.
- (void)beginRendererFailureRecovery {
    BOOL startFlush = NO;
    NSUInteger flushToken = 0;
    @synchronized (self) {
        _waitingForKeyframe = YES;
        _requestIDRAfterFlush = YES;
        if (!_rendererFlushInProgress) {
            _rendererFlushInProgress = YES;
            self.rendererGeneration++;
            flushToken = ++_rendererFlushToken;
            startFlush = YES;
        }
    }
    [self notifyAssemblerOfReferenceDrop];
    if (startFlush) [self performRendererFlushRemovingImage:NO token:flushToken];
}

- (void)enqueueAnnexBAccessUnit:(NSData *)accessUnit keyframe:(BOOL)keyframe frameID:(uint32_t)frameID {
    if (accessUnit.length == 0) return;
    NSUInteger generation = 0;
    BOOL overflow = NO;
    BOOL rejectedDuringFlush = NO;
    BOOL waitingForKeyframe = NO;
    @synchronized (self) {
        // Conversion plus AVFoundation may hold at most two compressed frames. Dropping a
        // reference frame requires a fresh IDR; never let work accumulate behind the screen.
        if (_rendererFlushInProgress) {
            _requestIDRAfterFlush = YES;
            rejectedDuringFlush = YES;
        } else if (_waitingForKeyframe && !keyframe) {
            waitingForKeyframe = YES;
        } else if (self.queuedFrames >= 2) {
            overflow = YES;
        } else {
            generation = self.rendererGeneration;
            self.queuedFrames++;
        }
    }
    if (waitingForKeyframe) return;
    if (rejectedDuringFlush) {
        [self notifyAssemblerOfReferenceDrop];
        return;
    }
    if (overflow) {
        [self beginReferenceDropRecovery];
        return;
    }

    dispatch_async(self.conversionQueue, ^{
        __block BOOL stale = NO;
        @synchronized (self) { stale = generation != self.rendererGeneration; }
        if (stale) {
            [self finishQueuedFrame];
            return;
        }
        CMSampleBufferRef sample = [self sampleBufferForAccessUnit:accessUnit
                                                          keyframe:keyframe
                                                        generation:generation];
        if (!sample) {
            [self finishQueuedFrame];
            @synchronized (self) {
                if (generation != self.rendererGeneration) return;
            }
            [self beginReferenceDropRecovery];
            return;
        }

        if (@available(macOS 14.0, *)) {
            // sampleBufferRenderer explicitly supports background enqueue. Keeping conversion,
            // readiness checks, enqueue and flush initiation on one serial queue removes the main
            // thread from the 60 fps hot path.
            [self renderSampleBuffer:sample keyframe:keyframe frameID:frameID generation:generation];
        } else {
            dispatch_async(dispatch_get_main_queue(), ^{
                [self renderSampleBuffer:sample keyframe:keyframe frameID:frameID generation:generation];
            });
        }
    });
}

- (void)renderSampleBuffer:(CMSampleBufferRef)sample
                  keyframe:(BOOL)keyframe
                   frameID:(uint32_t)frameID
                generation:(NSUInteger)generation {
    BOOL stale = NO;
    @synchronized (self) {
        stale = generation != self.rendererGeneration || _rendererFlushInProgress;
    }
    if (stale) {
        CFRelease(sample);
        [self finishQueuedFrame];
        return;
    }

    BOOL ready = NO;
    BOOL requiresFlush = NO;
    uint64_t decodeSubmitMicroseconds = DSMonotonicMicroseconds();
    if (@available(macOS 14.0, *)) {
        AVSampleBufferVideoRenderer *renderer = self.displayLayer.sampleBufferRenderer;
        requiresFlush = renderer.requiresFlushToResumeDecoding;
        ready = !requiresFlush && renderer.isReadyForMoreMediaData;
        if (ready) [renderer enqueueSampleBuffer:sample];
    } else {
#pragma clang diagnostic push
#pragma clang diagnostic ignored "-Wdeprecated-declarations"
        requiresFlush = self.displayLayer.requiresFlushToResumeDecoding;
        ready = !requiresFlush && self.displayLayer.isReadyForMoreMediaData;
        if (ready) [self.displayLayer enqueueSampleBuffer:sample];
#pragma clang diagnostic pop
    }
    if (ready) {
        uint64_t presentEnqueueMicroseconds = DSMonotonicMicroseconds();
        @synchronized (self) {
            self.hasPicture = YES;
            if (keyframe) _waitingForKeyframe = NO;
        }
        if (self.frameTraceHandler) {
            self.frameTraceHandler(frameID, decodeSubmitMicroseconds, presentEnqueueMicroseconds);
        }
    }
    CFRelease(sample);
    [self finishQueuedFrame];
    if (requiresFlush) [self beginRendererFailureRecovery];
    else if (!ready) [self beginReferenceDropRecovery];
}

- (CMSampleBufferRef)sampleBufferForAccessUnit:(NSData *)accessUnit
                                      keyframe:(BOOL)keyframe
                                    generation:(NSUInteger)generation CF_RETURNS_RETAINED {
    const uint8_t *auBytes = accessUnit.bytes;
    NSUInteger auLength = accessUnit.length;
    NSMutableData *rangeData = [NSMutableData dataWithCapacity:sizeof(DSNALRange) * 8];
    DSAppendNALRanges(auBytes, auLength, rangeData);
    const DSNALRange *ranges = rangeData.bytes;
    NSUInteger count = rangeData.length / sizeof(DSNALRange);
    if (count == 0) return NULL;

    // Single pass: extract SPS/PPS (small, only when present) and size the AVCC output. Only the
    // two parameter-set NALs are ever copied into NSData; slice data stays in the access unit.
    NSData *newSPS = nil;
    NSData *newPPS = nil;
    NSUInteger avccLength = 0;
    for (NSUInteger i = 0; i < count; i++) {
        uint8_t type = ranges[i].type;
        if (type == 7) {
            newSPS = [NSData dataWithBytes:auBytes + ranges[i].offset length:ranges[i].length];
        } else if (type == 8) {
            newPPS = [NSData dataWithBytes:auBytes + ranges[i].offset length:ranges[i].length];
        } else {
            avccLength += 4 + ranges[i].length;
        }
    }

    @synchronized (self) {
        if (generation != self.rendererGeneration) return NULL;
        if (newSPS && newPPS &&
            (![newSPS isEqualToData:self.sps] || ![newPPS isEqualToData:self.pps])) {
            const uint8_t *sets[] = { newSPS.bytes, newPPS.bytes };
            size_t sizes[] = { newSPS.length, newPPS.length };
            CMVideoFormatDescriptionRef next = NULL;
            OSStatus status = CMVideoFormatDescriptionCreateFromH264ParameterSets(
                kCFAllocatorDefault, 2, sets, sizes, 4, &next);
            if (status != noErr || !next) return NULL;
            if (_formatDescription) CFRelease(_formatDescription);
            _formatDescription = next;
            self.sps = newSPS;
            self.pps = newPPS;
            // Parameter sets changed: rebuild the parallel screenshot session to match, but only
            // if screenshots have been enabled — otherwise it must not run at all.
            if (_decompressionSession) {
                VTDecompressionSessionInvalidate(_decompressionSession);
                CFRelease(_decompressionSession);
                _decompressionSession = NULL;
            }
            if (_screenshotEnabled) [self rebuildScreenshotSessionLocked];
        }
        if (!_formatDescription || (!keyframe && !self.hasPicture)) return NULL;
    }

    if (avccLength == 0) return NULL;
    uint8_t *avcc = malloc(avccLength);
    if (!avcc) return NULL;
    NSUInteger pos = 0;
    for (NSUInteger i = 0; i < count; i++) {
        uint8_t type = ranges[i].type;
        if (type == 7 || type == 8) continue;
        uint32_t lengthBE = CFSwapInt32HostToBig((uint32_t)ranges[i].length);
        memcpy(avcc + pos, &lengthBE, sizeof(lengthBE));
        pos += sizeof(lengthBE);
        memcpy(avcc + pos, auBytes + ranges[i].offset, ranges[i].length);
        pos += ranges[i].length;
    }

    // Hand the single AVCC buffer straight to CoreMedia with no second copy: the block buffer
    // takes ownership and frees it (kCFAllocatorMalloc) when the sample is released.
    CMBlockBufferRef block = NULL;
    OSStatus status = CMBlockBufferCreateWithMemoryBlock(
        kCFAllocatorDefault, avcc, avccLength, kCFAllocatorMalloc, NULL,
        0, avccLength, 0, &block);
    if (status != kCMBlockBufferNoErr || !block) {
        free(avcc);
        return NULL;
    }

    CMSampleTimingInfo timing = {
        .duration = kCMTimeInvalid,
        .presentationTimeStamp = kCMTimeInvalid,
        .decodeTimeStamp = kCMTimeInvalid,
    };
    size_t sampleSize = avccLength;
    CMSampleBufferRef sample = NULL;
    @synchronized (self) {
        if (generation != self.rendererGeneration) {
            CFRelease(block);
            return NULL;
        }
        status = CMSampleBufferCreateReady(
            kCFAllocatorDefault, block, _formatDescription, 1, 1, &timing,
            1, &sampleSize, &sample);
    }
    CFRelease(block);
    if (status != noErr || !sample) return NULL;

    CFArrayRef attachments = CMSampleBufferGetSampleAttachmentsArray(sample, true);
    if (attachments && CFArrayGetCount(attachments) > 0) {
        CFMutableDictionaryRef values = (CFMutableDictionaryRef)CFArrayGetValueAtIndex(attachments, 0);
        CFDictionarySetValue(values, kCMSampleAttachmentKey_DisplayImmediately, kCFBooleanTrue);
        CFDictionarySetValue(values, kCMSampleAttachmentKey_NotSync,
                             keyframe ? kCFBooleanFalse : kCFBooleanTrue);
    }
    // Feed the same access unit to the parallel screenshot session when it is enabled. This is a
    // no-op (early return) while screenshots are off, so the second decode never runs by default.
    [self decodePixelBufferFromSample:sample generation:generation];
    return sample;
}

// Must be called with the @synchronized(self) lock held and _formatDescription non-NULL.
- (void)rebuildScreenshotSessionLocked {
    NSDictionary *destinationAttributes = @{
        (NSString *)kCVPixelBufferPixelFormatTypeKey: @(kCVPixelFormatType_32BGRA),
        (NSString *)kCVPixelBufferIOSurfacePropertiesKey: @{},
    };
    VTDecompressionSessionRef session = NULL;
    OSStatus status = VTDecompressionSessionCreate(
        kCFAllocatorDefault, _formatDescription, NULL,
        (__bridge CFDictionaryRef)destinationAttributes, NULL, &session);
    if (status == noErr && session) _decompressionSession = session;
}

- (BOOL)prepareScreenshotCapture {
    if (@available(macOS 14.4, *)) {
        // The renderer can expose exactly the pixel buffer already on screen; no second decode.
        return NO;
    }

    CVPixelBufferRef oldPixelBuffer = NULL;
    @synchronized (self) {
        _screenshotEnabled = YES;
        oldPixelBuffer = _lastPixelBuffer;
        _lastPixelBuffer = NULL;
        if (_formatDescription && !_decompressionSession) [self rebuildScreenshotSessionLocked];
    }
    if (oldPixelBuffer) CVPixelBufferRelease(oldPixelBuffer);
    return YES;
}

- (void)cancelScreenshotCapture {
    VTDecompressionSessionRef session = NULL;
    CVPixelBufferRef pixelBuffer = NULL;
    @synchronized (self) {
        _screenshotEnabled = NO;
        session = _decompressionSession;
        _decompressionSession = NULL;
        pixelBuffer = _lastPixelBuffer;
        _lastPixelBuffer = NULL;
    }
    if (session) {
        VTDecompressionSessionInvalidate(session);
        CFRelease(session);
    }
    if (pixelBuffer) CVPixelBufferRelease(pixelBuffer);
}

- (void)decodePixelBufferFromSample:(CMSampleBufferRef)sample generation:(NSUInteger)generation {
    VTDecompressionSessionRef session = NULL;
    @synchronized (self) {
        if (!_screenshotEnabled || generation != self.rendererGeneration) return;
        session = _decompressionSession;
        if (session) CFRetain(session);
    }
    if (!session) return;
    __weak typeof(self) weakSelf = self;
    (void)VTDecompressionSessionDecodeFrameWithOutputHandler(session, sample, 0, NULL,
        ^(OSStatus decodeStatus, VTDecodeInfoFlags infoFlags, CVImageBufferRef _Nullable imageBuffer,
          CMTime presentationTimeStamp, CMTime presentationDuration) {
            (void)infoFlags; (void)presentationTimeStamp; (void)presentationDuration;
            if (decodeStatus != noErr || !imageBuffer) return;
            typeof(self) strongSelf = weakSelf;
            if (!strongSelf) return;
            CVPixelBufferRef pixelBuffer = (CVPixelBufferRef)imageBuffer;
            CVPixelBufferRetain(pixelBuffer);
            CVPixelBufferRef old = NULL;
            VTDecompressionSessionRef completedSession = NULL;
            BOOL accepted = NO;
            @synchronized (strongSelf) {
                if (generation == strongSelf.rendererGeneration &&
                    strongSelf->_screenshotEnabled &&
                    strongSelf->_decompressionSession == session) {
                    old = strongSelf->_lastPixelBuffer;
                    strongSelf->_lastPixelBuffer = pixelBuffer;
                    strongSelf->_screenshotEnabled = NO;
                    completedSession = strongSelf->_decompressionSession;
                    strongSelf->_decompressionSession = NULL;
                    accepted = YES;
                }
            }
            if (!accepted) CVPixelBufferRelease(pixelBuffer);
            if (old) CVPixelBufferRelease(old);
            if (completedSession) {
                // Never invalidate/release a VT session from inside its own output callback.
                // Queue teardown behind the DecodeFrame call; only this one pixel is retained.
                dispatch_async(strongSelf.conversionQueue, ^{
                    VTDecompressionSessionInvalidate(completedSession);
                    CFRelease(completedSession);
                });
            }
        });
    CFRelease(session);
}

- (CIContext *)ciContext {
    if (!_ciContext) _ciContext = [CIContext contextWithOptions:nil];
    return _ciContext;
}

- (nullable CGImageRef)copyCurrentFrameImage {
    CVPixelBufferRef pixelBuffer = NULL;
    if (@available(macOS 14.4, *)) {
        pixelBuffer = [self.displayLayer.sampleBufferRenderer copyDisplayedPixelBuffer];
    }
    @synchronized (self) {
        if (!pixelBuffer && _lastPixelBuffer) {
            pixelBuffer = _lastPixelBuffer;
            CVPixelBufferRetain(pixelBuffer);
        }
    }
    if (!pixelBuffer) return NULL;
    CIImage *ciImage = [CIImage imageWithCVPixelBuffer:pixelBuffer];
    CGImageRef image = [self.ciContext createCGImage:ciImage fromRect:ciImage.extent];
    CVPixelBufferRelease(pixelBuffer);
    return image;
}

- (void)finishQueuedFrame {
    @synchronized (self) {
        if (self.queuedFrames > 0) self.queuedFrames--;
    }
}

@end
