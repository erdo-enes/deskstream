#import <AppKit/AppKit.h>

NS_ASSUME_NONNULL_BEGIN

typedef void (^DSInputDatagramBlock)(NSData *datagram);
typedef void (^DSInputControlBlock)(NSDictionary<NSString *, id> *message);

@class DSInputController;

/// Video surface that deliberately owns foreground keyboard and pointer events while the
/// stream is active. System-wide capture is never used, so no Accessibility permission is
/// required.
@interface DSStreamInputView : NSView
@property (nonatomic, weak, nullable) DSInputController *inputController;
@end

@interface DSInputController : NSObject

@property (nonatomic, weak) DSStreamInputView *view;
@property (atomic, getter=isEnabled) BOOL enabled;
@property (atomic, getter=isKeyboardEnabled) BOOL keyboardEnabled;
@property (atomic, readonly, getter=isPointerCaptured) BOOL pointerCaptured;
@property (nonatomic, copy, nullable) void (^captureChangedHandler)(BOOL captured);

- (instancetype)initWithView:(DSStreamInputView *)view
                sendDatagram:(DSInputDatagramBlock)sendDatagram
                  sendControl:(DSInputControlBlock)sendControl;
- (void)setPointerCaptured:(BOOL)captured;
- (void)releaseAllInput;

// Called by DSStreamInputView.
- (void)handleKeyEvent:(NSEvent *)event down:(BOOL)down;
- (void)handleFlagsChanged:(NSEvent *)event;
- (void)handleMouseMotion:(NSEvent *)event;
- (void)handleMouseButton:(NSEvent *)event down:(BOOL)down;
- (void)handleScroll:(NSEvent *)event;

@end

NS_ASSUME_NONNULL_END
