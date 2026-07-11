#import <AppKit/AppKit.h>
#import <CoreGraphics/CoreGraphics.h>

NS_ASSUME_NONNULL_BEGIN

/// Hardware-backed H.264 renderer. Accepts complete Annex-B access units and displays them
/// immediately without a media timeline or jitter buffer.
@interface DSVideoView : NSView

/// Called immediately when a compressed reference frame is dropped locally. The owner must stop
/// its frame assembler at the next keyframe before another access unit is delivered here.
@property (nonatomic, copy, nullable) dispatch_block_t discardUntilKeyframeHandler;

/// Called when it is safe to send a replacement IDR. If the renderer itself needed flushing,
/// this is deliberately delayed until the asynchronous flush has completed.
@property (nonatomic, copy, nullable) dispatch_block_t requestIDRHandler;
@property (nonatomic, readonly) BOOL hasPicture;

- (void)enqueueAnnexBAccessUnit:(NSData *)accessUnit keyframe:(BOOL)keyframe;
- (void)resetRenderer;

/// Runs requestIDRHandler immediately when no renderer flush is active, otherwise coalesces the
/// request and runs it once after the in-flight asynchronous flush completes.
- (void)requestKeyframeWhenRendererReady;

/// Prepares one screenshot. Returns YES when the macOS 13/14.0-14.3 fallback decoder needs a
/// fresh IDR. On macOS 14.4+, the displayed pixel buffer is copied directly and this returns NO.
/// The fallback decoder tears itself down after producing one pixel buffer.
- (BOOL)prepareScreenshotCapture;

/// Cancels an outstanding fallback screenshot decode and releases its cached pixel buffer.
- (void)cancelScreenshotCapture;

/// Returns a copy of the most recently displayed decoded frame as a CGImage, or NULL if no
/// frame has been displayed yet. Safe to call from the main thread at any time; the caller
/// owns the returned image and must release it.
- (nullable CGImageRef)copyCurrentFrameImage CF_RETURNS_RETAINED;

@end

NS_ASSUME_NONNULL_END
