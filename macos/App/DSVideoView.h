#import <AppKit/AppKit.h>

NS_ASSUME_NONNULL_BEGIN

/// Hardware-backed H.264 renderer. Accepts complete Annex-B access units and displays them
/// immediately without a media timeline or jitter buffer.
@interface DSVideoView : NSView

@property (nonatomic, copy, nullable) dispatch_block_t requestIDRHandler;
@property (nonatomic, readonly) BOOL hasPicture;

- (void)enqueueAnnexBAccessUnit:(NSData *)accessUnit keyframe:(BOOL)keyframe;
- (void)resetRenderer;

@end

NS_ASSUME_NONNULL_END
