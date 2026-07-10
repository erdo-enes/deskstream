#import "DSVideoView.h"

#import <AVFoundation/AVFoundation.h>
#import <CoreMedia/CoreMedia.h>
#import <QuartzCore/QuartzCore.h>

static NSArray<NSData *> *DSNALUnits(NSData *accessUnit) {
    const uint8_t *bytes = accessUnit.bytes;
    NSUInteger length = accessUnit.length;
    NSMutableArray<NSData *> *units = [NSMutableArray array];

    NSUInteger (^findStart)(NSUInteger, NSUInteger *) = ^NSUInteger(NSUInteger from, NSUInteger *codeLength) {
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
    };

    NSUInteger codeLength = 0;
    NSUInteger start = findStart(0, &codeLength);
    while (start != NSNotFound) {
        NSUInteger payloadStart = start + codeLength;
        NSUInteger nextCodeLength = 0;
        NSUInteger next = findStart(payloadStart, &nextCodeLength);
        NSUInteger payloadEnd = next == NSNotFound ? length : next;

        // Annex-B trailing_zero_8bits are not part of the NAL. This also removes harmless
        // zero padding introduced when FEC reconstructs the final datagram.
        while (payloadEnd > payloadStart && bytes[payloadEnd - 1] == 0) payloadEnd--;
        if (payloadEnd > payloadStart) {
            [units addObject:[NSData dataWithBytes:bytes + payloadStart
                                            length:payloadEnd - payloadStart]];
        }
        if (next == NSNotFound) break;
        start = next;
        codeLength = nextCodeLength;
    }
    return units;
}

@interface DSVideoView () {
    CMVideoFormatDescriptionRef _formatDescription;
}
@property (nonatomic, strong) AVSampleBufferDisplayLayer *displayLayer;
@property (nonatomic) dispatch_queue_t conversionQueue;
@property (nonatomic, copy) NSData *sps;
@property (nonatomic, copy) NSData *pps;
@property (nonatomic) NSUInteger queuedFrames;
@property (nonatomic) NSUInteger rendererGeneration;
@property (nonatomic, readwrite) BOOL hasPicture;
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
        _conversionQueue = dispatch_queue_create("com.deskstream.macos.h264", DISPATCH_QUEUE_SERIAL);

        [[NSNotificationCenter defaultCenter]
            addObserver:self
               selector:@selector(rendererFailed:)
                   name:AVSampleBufferDisplayLayerFailedToDecodeNotification
                 object:_displayLayer];
    }
    return self;
}

- (BOOL)acceptsFirstResponder { return YES; }

- (void)dealloc {
    [[NSNotificationCenter defaultCenter] removeObserver:self];
    if (_formatDescription) CFRelease(_formatDescription);
}

- (void)rendererFailed:(NSNotification *)notification {
    (void)notification;
    [self resetRenderer];
    if (self.requestIDRHandler) self.requestIDRHandler();
}

- (void)resetRenderer {
    @synchronized (self) {
        self.rendererGeneration++;
        self.hasPicture = NO;
        self.sps = nil;
        self.pps = nil;
        if (_formatDescription) {
            CFRelease(_formatDescription);
            _formatDescription = NULL;
        }
    }
    dispatch_async(dispatch_get_main_queue(), ^{
        if (@available(macOS 14.0, *)) {
            [self.displayLayer.sampleBufferRenderer
                flushWithRemovalOfDisplayedImage:YES completionHandler:nil];
        } else {
#pragma clang diagnostic push
#pragma clang diagnostic ignored "-Wdeprecated-declarations"
            [self.displayLayer flushAndRemoveImage];
#pragma clang diagnostic pop
        }
    });
}

- (void)enqueueAnnexBAccessUnit:(NSData *)accessUnit keyframe:(BOOL)keyframe {
    if (accessUnit.length == 0) return;
    __block NSUInteger generation = 0;
    __block BOOL overflow = NO;
    @synchronized (self) {
        // Conversion plus AVFoundation may hold at most two compressed frames. Dropping a
        // reference frame requires a fresh IDR; never let work accumulate behind the screen.
        if (self.queuedFrames >= 2) {
            overflow = YES;
        } else {
            generation = self.rendererGeneration;
            self.queuedFrames++;
        }
    }
    if (overflow) {
        [self resetRenderer];
        if (self.requestIDRHandler) self.requestIDRHandler();
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
            if (self.requestIDRHandler) self.requestIDRHandler();
            return;
        }

        dispatch_async(dispatch_get_main_queue(), ^{
            __block BOOL stale = NO;
            @synchronized (self) { stale = generation != self.rendererGeneration; }
            if (stale) {
                CFRelease(sample);
                [self finishQueuedFrame];
                return;
            }
            BOOL ready = NO;
            if (@available(macOS 14.0, *)) {
                AVSampleBufferVideoRenderer *renderer = self.displayLayer.sampleBufferRenderer;
                if (renderer.requiresFlushToResumeDecoding) {
                    [renderer flushWithRemovalOfDisplayedImage:NO completionHandler:nil];
                }
                ready = renderer.isReadyForMoreMediaData;
                if (ready) [renderer enqueueSampleBuffer:sample];
            } else {
#pragma clang diagnostic push
#pragma clang diagnostic ignored "-Wdeprecated-declarations"
                if (self.displayLayer.requiresFlushToResumeDecoding) [self.displayLayer flush];
                ready = self.displayLayer.isReadyForMoreMediaData;
                if (ready) [self.displayLayer enqueueSampleBuffer:sample];
#pragma clang diagnostic pop
            }
            if (ready) {
                @synchronized (self) { self.hasPicture = YES; }
            }
            CFRelease(sample);
            if (ready) {
                [self finishQueuedFrame];
            } else {
                [self finishQueuedFrame];
                [self resetRenderer];
                if (self.requestIDRHandler) self.requestIDRHandler();
            }
        });
    });
}

- (CMSampleBufferRef)sampleBufferForAccessUnit:(NSData *)accessUnit
                                      keyframe:(BOOL)keyframe
                                    generation:(NSUInteger)generation CF_RETURNS_RETAINED {
    NSArray<NSData *> *units = DSNALUnits(accessUnit);
    if (units.count == 0) return NULL;

    NSData *newSPS = nil;
    NSData *newPPS = nil;
    for (NSData *unit in units) {
        const uint8_t *bytes = unit.bytes;
        if (unit.length == 0) continue;
        uint8_t type = bytes[0] & 0x1F;
        if (type == 7) newSPS = unit;
        else if (type == 8) newPPS = unit;
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
        }
        if (!_formatDescription || (!keyframe && !self.hasPicture)) return NULL;
    }

    NSMutableData *avcc = [NSMutableData dataWithCapacity:accessUnit.length];
    for (NSData *unit in units) {
        if (unit.length == 0) continue;
        uint8_t type = ((const uint8_t *)unit.bytes)[0] & 0x1F;
        if (type == 7 || type == 8) continue;
        uint32_t lengthBE = CFSwapInt32HostToBig((uint32_t)unit.length);
        [avcc appendBytes:&lengthBE length:sizeof(lengthBE)];
        [avcc appendData:unit];
    }
    if (avcc.length == 0) return NULL;

    CMBlockBufferRef block = NULL;
    OSStatus status = CMBlockBufferCreateWithMemoryBlock(
        kCFAllocatorDefault, NULL, avcc.length, kCFAllocatorDefault, NULL,
        0, avcc.length, 0, &block);
    if (status != kCMBlockBufferNoErr || !block) return NULL;
    status = CMBlockBufferReplaceDataBytes(avcc.bytes, block, 0, avcc.length);
    if (status != kCMBlockBufferNoErr) {
        CFRelease(block);
        return NULL;
    }

    CMSampleTimingInfo timing = {
        .duration = kCMTimeInvalid,
        .presentationTimeStamp = kCMTimeInvalid,
        .decodeTimeStamp = kCMTimeInvalid,
    };
    size_t sampleSize = avcc.length;
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
    return sample;
}

- (void)finishQueuedFrame {
    @synchronized (self) {
        if (self.queuedFrames > 0) self.queuedFrames--;
    }
}

@end
