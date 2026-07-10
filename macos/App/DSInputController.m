#import "DSInputController.h"

#import <CoreGraphics/CoreGraphics.h>

static void DSPutU32(uint8_t *dst, NSUInteger offset, uint32_t value) {
    dst[offset] = (uint8_t)(value >> 24);
    dst[offset + 1] = (uint8_t)(value >> 16);
    dst[offset + 2] = (uint8_t)(value >> 8);
    dst[offset + 3] = (uint8_t)value;
}

static const void *DSInputQueueKey = &DSInputQueueKey;

static NSDictionary<NSNumber *, NSNumber *> *DSMacKeyToHIDUsage(void) {
    static NSDictionary<NSNumber *, NSNumber *> *map;
    static dispatch_once_t once;
    dispatch_once(&once, ^{
        map = @{
            @0:@4, @1:@22, @2:@7, @3:@9, @4:@11, @5:@10, @6:@29, @7:@27,
            @8:@6, @9:@25, @11:@5, @12:@20, @13:@26, @14:@8, @15:@21,
            @16:@28, @17:@23, @18:@30, @19:@31, @20:@32, @21:@33, @22:@35,
            @23:@34, @24:@46, @25:@38, @26:@36, @27:@45, @28:@37, @29:@39,
            @30:@48, @31:@18, @32:@24, @33:@47, @34:@12, @35:@19, @36:@40,
            @37:@15, @38:@13, @39:@52, @40:@14, @41:@51, @42:@49, @43:@54,
            @44:@56, @45:@17, @46:@16, @47:@55, @48:@43, @49:@44, @50:@53,
            @51:@42, @53:@41, @54:@231, @55:@227, @56:@225, @57:@57,
            @58:@226, @59:@224, @60:@229, @61:@230, @62:@228,
            @65:@99, @67:@85, @69:@87, @71:@83, @75:@84, @76:@88, @78:@86,
            @81:@103, @82:@98, @83:@89, @84:@90, @85:@91, @86:@92, @87:@93,
            @88:@94, @89:@95, @91:@96, @92:@97,
            @96:@62, @97:@63, @98:@64, @99:@60, @100:@65, @101:@66,
            @103:@68, @105:@104, @107:@105, @109:@67, @111:@69, @113:@106,
            @114:@73, @115:@74, @116:@75, @117:@76, @118:@61, @119:@77,
            @120:@59, @121:@78, @122:@58, @123:@80, @124:@79, @125:@81,
            @126:@82,
        };
    });
    return map;
}

@interface DSInputController ()
@property (nonatomic, copy) DSInputDatagramBlock sendDatagram;
@property (nonatomic, copy) DSInputControlBlock sendControl;
@property (nonatomic) dispatch_queue_t motionQueue;
@property (nonatomic) dispatch_source_t motionTimer;
@property (atomic, readwrite, getter=isPointerCaptured) BOOL pointerCaptured;
@property (nonatomic) BOOL localPointerSuppressed;
@property (nonatomic) double accumulatedX;
@property (nonatomic) double accumulatedY;
@property (nonatomic) double wheelX;
@property (nonatomic) double wheelY;
@property (nonatomic) BOOL hasAbsolute;
@property (nonatomic) int32_t absoluteX;
@property (nonatomic) int32_t absoluteY;
@property (nonatomic) uint32_t motionSequence;
@property (nonatomic) uint32_t buttonSequence;
@property (nonatomic) uint32_t keyboardSequence;
@property (nonatomic, strong) NSMutableSet<NSNumber *> *pressedUsages;
@end

@implementation DSStreamInputView {
    NSTrackingArea *_trackingArea;
}

- (BOOL)acceptsFirstResponder { return YES; }

- (void)updateTrackingAreas {
    [super updateTrackingAreas];
    if (_trackingArea) [self removeTrackingArea:_trackingArea];
    _trackingArea = [[NSTrackingArea alloc]
        initWithRect:NSZeroRect
             options:NSTrackingMouseMoved | NSTrackingActiveInKeyWindow | NSTrackingInVisibleRect
               owner:self
            userInfo:nil];
    [self addTrackingArea:_trackingArea];
}

- (void)mouseDown:(NSEvent *)event {
    [self.window makeFirstResponder:self];
    [self.inputController handleMouseButton:event down:YES];
}
- (void)mouseUp:(NSEvent *)event { [self.inputController handleMouseButton:event down:NO]; }
- (void)rightMouseDown:(NSEvent *)event { [self.inputController handleMouseButton:event down:YES]; }
- (void)rightMouseUp:(NSEvent *)event { [self.inputController handleMouseButton:event down:NO]; }
- (void)otherMouseDown:(NSEvent *)event { [self.inputController handleMouseButton:event down:YES]; }
- (void)otherMouseUp:(NSEvent *)event { [self.inputController handleMouseButton:event down:NO]; }
- (void)mouseMoved:(NSEvent *)event { [self.inputController handleMouseMotion:event]; }
- (void)mouseDragged:(NSEvent *)event { [self.inputController handleMouseMotion:event]; }
- (void)rightMouseDragged:(NSEvent *)event { [self.inputController handleMouseMotion:event]; }
- (void)otherMouseDragged:(NSEvent *)event { [self.inputController handleMouseMotion:event]; }
- (void)scrollWheel:(NSEvent *)event { [self.inputController handleScroll:event]; }
- (void)keyDown:(NSEvent *)event { [self.inputController handleKeyEvent:event down:YES]; }
- (void)keyUp:(NSEvent *)event { [self.inputController handleKeyEvent:event down:NO]; }
- (void)flagsChanged:(NSEvent *)event { [self.inputController handleFlagsChanged:event]; }

@end

@implementation DSInputController

@synthesize enabled = _enabled;
@synthesize keyboardEnabled = _keyboardEnabled;
@synthesize pointerCaptured = _pointerCaptured;

- (instancetype)initWithView:(DSStreamInputView *)view
                sendDatagram:(DSInputDatagramBlock)sendDatagram
                  sendControl:(DSInputControlBlock)sendControl {
    self = [super init];
    if (self) {
        _view = view;
        view.inputController = self;
        _sendDatagram = [sendDatagram copy];
        _sendControl = [sendControl copy];
        _pressedUsages = [NSMutableSet set];
        _motionQueue = dispatch_queue_create("com.deskstream.macos.input", DISPATCH_QUEUE_SERIAL);
        dispatch_queue_set_specific(_motionQueue, DSInputQueueKey, (void *)DSInputQueueKey, NULL);
        _motionTimer = dispatch_source_create(DISPATCH_SOURCE_TYPE_TIMER, 0, 0, _motionQueue);
        dispatch_source_set_timer(_motionTimer, dispatch_time(DISPATCH_TIME_NOW, 0),
                                  NSEC_PER_SEC / 120, NSEC_PER_MSEC);
        __weak typeof(self) weakSelf = self;
        dispatch_source_set_event_handler(_motionTimer, ^{ [weakSelf flushMotion]; });
        dispatch_resume(_motionTimer);

        [[NSNotificationCenter defaultCenter]
            addObserver:self selector:@selector(windowLostFocus:)
            name:NSWindowDidResignKeyNotification object:view.window];
        [[NSNotificationCenter defaultCenter]
            addObserver:self selector:@selector(applicationResigned:)
            name:NSApplicationDidResignActiveNotification object:nil];
    }
    return self;
}

- (void)dealloc {
    [[NSNotificationCenter defaultCenter] removeObserver:self];
    @synchronized (self) { _enabled = NO; }
    if (_motionTimer) dispatch_source_cancel(_motionTimer);
    [self performSynchronouslyOnMotionQueue:^{}];
    [self releaseAllInput];
    [self restoreLocalPointer];
}

- (BOOL)isEnabled {
    @synchronized (self) { return _enabled; }
}

- (void)setEnabled:(BOOL)enabled {
    @synchronized (self) {
        if (_enabled == enabled) return;
        _enabled = enabled;
    }
    if (!enabled) {
        [self performSynchronouslyOnMotionQueue:^{}];
        [self setPointerCaptured:NO];
        [self releaseAllInput];
    }
}

- (BOOL)isKeyboardEnabled {
    @synchronized (self) { return _keyboardEnabled; }
}

- (void)setKeyboardEnabled:(BOOL)keyboardEnabled {
    @synchronized (self) {
        if (_keyboardEnabled == keyboardEnabled) return;
        _keyboardEnabled = keyboardEnabled;
    }
    if (!keyboardEnabled) [self sendKeyboardReset];
}

- (BOOL)isPointerCaptured {
    @synchronized (self) { return _pointerCaptured; }
}

- (void)setPointerCaptured:(BOOL)captured {
    if (!NSThread.isMainThread) {
        dispatch_async(dispatch_get_main_queue(), ^{ [self setPointerCaptured:captured]; });
        return;
    }
    if (captured && !self.enabled) return;
    @synchronized (self) {
        if (_pointerCaptured == captured) return;
        _pointerCaptured = captured;
        self.hasAbsolute = NO;
        self.accumulatedX = self.accumulatedY = 0;
    }
    if (captured) {
        [self.view.window makeFirstResponder:self.view];
        CGError result = CGAssociateMouseAndMouseCursorPosition(false);
        if (result == kCGErrorSuccess) {
            @synchronized (self) { self.localPointerSuppressed = YES; }
            [NSCursor hide];
        } else {
            @synchronized (self) { _pointerCaptured = NO; }
            captured = NO;
        }
    } else {
        [self restoreLocalPointer];
        [self sendMouseReset];
        [self sendKeyboardReset];
    }
    if (self.captureChangedHandler) self.captureChangedHandler(captured);
}

- (void)restoreLocalPointer {
    __block BOOL shouldRestore = NO;
    @synchronized (self) {
        if (self.localPointerSuppressed) {
            self.localPointerSuppressed = NO;
            shouldRestore = YES;
        }
    }
    if (!shouldRestore) return;
    dispatch_block_t restore = ^{
        CGAssociateMouseAndMouseCursorPosition(true);
        [NSCursor unhide];
    };
    if (NSThread.isMainThread) restore();
    else dispatch_async(dispatch_get_main_queue(), restore);
}

- (void)windowLostFocus:(NSNotification *)notification {
    if (notification.object != self.view.window) return;
    [self setPointerCaptured:NO];
    [self releaseAllInput];
}

- (void)applicationResigned:(NSNotification *)notification {
    (void)notification;
    [self setPointerCaptured:NO];
    [self releaseAllInput];
}

- (void)handleMouseMotion:(NSEvent *)event {
    if (!self.enabled) return;
    @synchronized (self) {
        if (_pointerCaptured) {
            self.accumulatedX += event.deltaX;
            self.accumulatedY += event.deltaY;
        } else if (self.view.bounds.size.width > 1 && self.view.bounds.size.height > 1) {
            NSPoint point = [self.view convertPoint:event.locationInWindow fromView:nil];
            double nx = MAX(0, MIN(1, point.x / self.view.bounds.size.width));
            double ny = MAX(0, MIN(1, 1.0 - point.y / self.view.bounds.size.height));
            self.absoluteX = (int32_t)llround(nx * 65535.0);
            self.absoluteY = (int32_t)llround(ny * 65535.0);
            self.hasAbsolute = YES;
        }
    }
}

- (void)handleMouseButton:(NSEvent *)event down:(BOOL)down {
    if (!self.enabled) return;
    [self handleMouseMotion:event]; // position before a direct-mode click
    // Flush accumulated motion before the reliable TCP transition. UDP/TCP do not share an
    // ordering domain, but emitting the position first minimizes clicks at the old cursor.
    [self performSynchronouslyOnMotionQueue:^{ [self flushMotion]; }];
    NSString *button = nil;
    switch (event.buttonNumber) {
        case 0: button = @"left"; break;
        case 1: button = @"right"; break;
        case 2: button = @"middle"; break;
        case 3: button = @"back"; break;
        case 4: button = @"forward"; break;
        default: return;
    }
    self.sendControl(@{
        @"type": @"MOUSE_BUTTON",
        @"sequence": @(self.buttonSequence++),
        @"button": button,
        @"down": @(down),
    });
}

- (void)handleScroll:(NSEvent *)event {
    if (!self.enabled) return;
    double scale = event.hasPreciseScrollingDeltas ? 6.0 : 120.0;
    @synchronized (self) {
        self.wheelX += event.scrollingDeltaX * scale;
        self.wheelY += event.scrollingDeltaY * scale;
    }
}

- (void)handleKeyEvent:(NSEvent *)event down:(BOOL)down {
    if (!self.enabled || !self.keyboardEnabled || (down && event.isARepeat)) return;
    if (down && event.keyCode == 53 &&
        (event.modifierFlags & NSEventModifierFlagControl) &&
        (event.modifierFlags & NSEventModifierFlagOption)) {
        [self setPointerCaptured:NO];
        return;
    }
    NSNumber *usage = DSMacKeyToHIDUsage()[@(event.keyCode)];
    if (usage) [self sendUsage:usage.unsignedShortValue down:down];
}

- (void)handleFlagsChanged:(NSEvent *)event {
    if (!self.enabled || !self.keyboardEnabled) return;
    NSNumber *usage = DSMacKeyToHIDUsage()[@(event.keyCode)];
    if (!usage) return;
    switch (event.keyCode) {
        case 54: case 55: case 56: case 57: case 58:
        case 59: case 60: case 61: case 62:
            break;
        default: return;
    }
    // Generic NSEvent modifier flags cannot distinguish releasing the left key while the
    // right key remains held. Query the physical key so each HID usage gets its own release.
    BOOL down = CGEventSourceKeyState(kCGEventSourceStateHIDSystemState,
                                      (CGKeyCode)event.keyCode);
    [self sendUsage:usage.unsignedShortValue down:down];
}

- (void)sendUsage:(uint16_t)usage down:(BOOL)down {
    NSNumber *key = @(usage);
    @synchronized (self.pressedUsages) {
        if (down) {
            if ([self.pressedUsages containsObject:key]) return;
            [self.pressedUsages addObject:key];
        } else {
            if (![self.pressedUsages containsObject:key]) return;
            [self.pressedUsages removeObject:key];
        }
    }
    self.sendControl(@{
        @"type": @"KEYBOARD_KEY",
        @"sequence": @(self.keyboardSequence++),
        @"usage": @(usage),
        @"down": @(down),
    });
}

- (void)flushMotion {
    if (!self.enabled) return;
    BOOL absolute = NO;
    int32_t x = 0, y = 0, wheelX = 0, wheelY = 0;
    @synchronized (self) {
        absolute = !_pointerCaptured && self.hasAbsolute;
        if (absolute) {
            x = self.absoluteX; y = self.absoluteY; self.hasAbsolute = NO;
        } else {
            x = (int32_t)llround(self.accumulatedX);
            y = (int32_t)llround(self.accumulatedY);
            self.accumulatedX -= x; self.accumulatedY -= y;
        }
        wheelX = (int32_t)llround(self.wheelX);
        wheelY = (int32_t)llround(self.wheelY);
        self.wheelX -= wheelX; self.wheelY -= wheelY;
    }
    if (x == 0 && y == 0 && wheelX == 0 && wheelY == 0 && !absolute) return;

    uint8_t bytes[28] = {'D','S','M','I',1,(uint8_t)(absolute ? 1 : 0),0,0};
    DSPutU32(bytes, 8, self.motionSequence++);
    DSPutU32(bytes, 12, (uint32_t)x);
    DSPutU32(bytes, 16, (uint32_t)y);
    DSPutU32(bytes, 20, (uint32_t)wheelX);
    DSPutU32(bytes, 24, (uint32_t)wheelY);
    self.sendDatagram([NSData dataWithBytes:bytes length:sizeof(bytes)]);
}

- (void)sendMouseReset { self.sendControl(@{@"type": @"MOUSE_RESET"}); }

- (void)sendKeyboardReset {
    @synchronized (self.pressedUsages) { [self.pressedUsages removeAllObjects]; }
    self.sendControl(@{@"type": @"KEYBOARD_RESET"});
}

- (void)releaseAllInput {
    [self sendMouseReset];
    [self sendKeyboardReset];
    @synchronized (self) {
        self.accumulatedX = self.accumulatedY = 0;
        self.wheelX = self.wheelY = 0;
        self.hasAbsolute = NO;
    }
}

- (void)performSynchronouslyOnMotionQueue:(dispatch_block_t)block {
    if (dispatch_get_specific(DSInputQueueKey) == DSInputQueueKey) block();
    else dispatch_sync(self.motionQueue, block);
}

@end
