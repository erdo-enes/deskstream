#import "DSGamepadManager.h"

#import <CoreHaptics/CoreHaptics.h>
#import <GameController/GameController.h>

enum {
    DSXInputDPadUp = 0x0001, DSXInputDPadDown = 0x0002,
    DSXInputDPadLeft = 0x0004, DSXInputDPadRight = 0x0008,
    DSXInputStart = 0x0010, DSXInputBack = 0x0020,
    DSXInputLeftThumb = 0x0040, DSXInputRightThumb = 0x0080,
    DSXInputLeftShoulder = 0x0100, DSXInputRightShoulder = 0x0200,
    DSXInputGuide = 0x0400, DSXInputA = 0x1000, DSXInputB = 0x2000,
    DSXInputX = 0x4000, DSXInputY = 0x8000,
};

static void DSPadPutU16(uint8_t *p, NSUInteger offset, uint16_t value) {
    p[offset] = value >> 8; p[offset + 1] = value;
}
static void DSPadPutU32(uint8_t *p, NSUInteger offset, uint32_t value) {
    p[offset] = value >> 24; p[offset + 1] = value >> 16;
    p[offset + 2] = value >> 8; p[offset + 3] = value;
}
static int16_t DSAxis(float value) {
    value = fmaxf(-1.0f, fminf(1.0f, value));
    return value <= -1.0f ? INT16_MIN : (int16_t)lrintf(value * INT16_MAX);
}

static const void *DSGamepadQueueKey = &DSGamepadQueueKey;

@interface DSGamepadManager ()
@property (nonatomic, copy) DSGamepadDatagramBlock sendDatagram;
@property (nonatomic, copy) DSGamepadControlBlock sendControl;
@property (nonatomic) dispatch_queue_t queue;
@property (nonatomic) dispatch_source_t timer;
@property (nonatomic, strong) NSArray<GCController *> *controllers;
@property (nonatomic, strong) NSMutableArray<NSData *> *lastStates;
@property (nonatomic, strong) NSMutableArray<NSNumber *> *sequences;
@property (nonatomic, strong) NSMutableArray<NSNumber *> *lastSentMs;
@property (nonatomic, strong) NSMutableDictionary<NSNumber *, CHHapticEngine *> *hapticEngines;
@property (nonatomic, strong) NSMutableDictionary<NSNumber *, id<CHHapticAdvancedPatternPlayer>> *hapticPlayers;
@property (nonatomic) BOOL negotiated;
@property (atomic, readwrite) NSUInteger controllerCount;
@end

@implementation DSGamepadManager

@synthesize enabled = _enabled;

- (instancetype)initWithDatagramSender:(DSGamepadDatagramBlock)sendDatagram
                          controlSender:(DSGamepadControlBlock)sendControl {
    self = [super init];
    if (self) {
        _sendDatagram = [sendDatagram copy];
        _sendControl = [sendControl copy];
        _queue = dispatch_queue_create("com.deskstream.macos.gamepad", DISPATCH_QUEUE_SERIAL);
        dispatch_queue_set_specific(_queue, DSGamepadQueueKey, (void *)DSGamepadQueueKey, NULL);
        _controllers = @[];
        _lastStates = [NSMutableArray array];
        _sequences = [NSMutableArray arrayWithArray:@[@0, @0, @0, @0]];
        _lastSentMs = [NSMutableArray array];
        _hapticEngines = [NSMutableDictionary dictionary];
        _hapticPlayers = [NSMutableDictionary dictionary];

        [[NSNotificationCenter defaultCenter] addObserver:self
            selector:@selector(controllerChanged:) name:GCControllerDidConnectNotification object:nil];
        [[NSNotificationCenter defaultCenter] addObserver:self
            selector:@selector(controllerChanged:) name:GCControllerDidDisconnectNotification object:nil];

        _timer = dispatch_source_create(DISPATCH_SOURCE_TYPE_TIMER, 0, 0, _queue);
        dispatch_source_set_timer(_timer, dispatch_time(DISPATCH_TIME_NOW, 0),
                                  NSEC_PER_SEC / 120, NSEC_PER_MSEC);
        __weak typeof(self) weakSelf = self;
        dispatch_source_set_event_handler(_timer, ^{ [weakSelf sendDueSnapshots]; });
        dispatch_resume(_timer);
        [self refreshControllers];
    }
    return self;
}

- (void)dealloc {
    [[NSNotificationCenter defaultCenter] removeObserver:self];
    if (_timer) dispatch_source_cancel(_timer);
    [self performSynchronouslyOnQueue:^{ [self stopOnQueue]; }];
}

- (BOOL)isEnabled {
    @synchronized (self) { return _enabled; }
}

- (void)setEnabled:(BOOL)enabled {
    @synchronized (self) {
        if (_enabled == enabled) return;
        _enabled = enabled;
    }
    if (enabled) [self refreshControllers];
    else [self stop];
}

- (void)controllerChanged:(NSNotification *)notification {
    (void)notification;
    [self refreshControllers];
}

- (void)refreshControllers {
    NSArray<GCController *> *available = [GCController.controllers filteredArrayUsingPredicate:
        [NSPredicate predicateWithBlock:^BOOL(GCController *controller, NSDictionary *bindings) {
            (void)bindings;
            return controller.extendedGamepad != nil;
        }]];
    if (available.count > 4) available = [available subarrayWithRange:NSMakeRange(0, 4)];

    dispatch_async(self.queue, ^{
        if (self.negotiated) [self sendNeutralSnapshots];
        [self stopHapticsOnQueue];
        self.controllers = available;
        self.controllerCount = available.count;
        [self.lastStates removeAllObjects];
        [self.lastSentMs removeAllObjects];
        for (NSUInteger i = 0; i < available.count; i++) {
            [self.lastStates addObject:NSData.data];
            [self.lastSentMs addObject:@0];
            available[i].playerIndex = (GCControllerPlayerIndex)i;
        }
        if (self.enabled) {
            if (available.count) {
                self.sendControl(@{@"type":@"GAMEPAD_START", @"controllers":@(available.count)});
                self.negotiated = YES;
            } else if (self.negotiated) {
                self.sendControl(@{@"type":@"GAMEPAD_STOP"});
                self.negotiated = NO;
            }
        } else if (self.negotiated) {
            self.sendControl(@{@"type":@"GAMEPAD_STOP"});
            self.negotiated = NO;
        }
        NSMutableArray<NSString *> *names = [NSMutableArray arrayWithCapacity:available.count];
        for (GCController *controller in available)
            [names addObject:controller.vendorName ?: @"Game Controller"];
        dispatch_async(dispatch_get_main_queue(), ^{
            if (self.inventoryChangedHandler) self.inventoryChangedHandler(names);
        });
    });
}

- (NSData *)stateForController:(GCController *)controller slot:(NSUInteger)slot sequence:(uint32_t)sequence {
    GCExtendedGamepad *g = controller.extendedGamepad;
    if (!g) return NSData.data;
    uint16_t buttons = 0;
    if (g.dpad.up.isPressed) buttons |= DSXInputDPadUp;
    if (g.dpad.down.isPressed) buttons |= DSXInputDPadDown;
    if (g.dpad.left.isPressed) buttons |= DSXInputDPadLeft;
    if (g.dpad.right.isPressed) buttons |= DSXInputDPadRight;
    if (g.buttonMenu.isPressed) buttons |= DSXInputStart;
    if (g.buttonOptions.isPressed) buttons |= DSXInputBack;
    if (g.leftThumbstickButton.isPressed) buttons |= DSXInputLeftThumb;
    if (g.rightThumbstickButton.isPressed) buttons |= DSXInputRightThumb;
    if (g.leftShoulder.isPressed) buttons |= DSXInputLeftShoulder;
    if (g.rightShoulder.isPressed) buttons |= DSXInputRightShoulder;
    if (g.buttonHome.isPressed) buttons |= DSXInputGuide;
    if (g.buttonA.isPressed) buttons |= DSXInputA;
    if (g.buttonB.isPressed) buttons |= DSXInputB;
    if (g.buttonX.isPressed) buttons |= DSXInputX;
    if (g.buttonY.isPressed) buttons |= DSXInputY;

    uint8_t bytes[24] = {'D','S','G','P',1,(uint8_t)slot};
    DSPadPutU16(bytes, 6, buttons);
    bytes[8] = (uint8_t)lrintf(fmaxf(0, fminf(1, g.leftTrigger.value)) * 255);
    bytes[9] = (uint8_t)lrintf(fmaxf(0, fminf(1, g.rightTrigger.value)) * 255);
    DSPadPutU16(bytes, 10, (uint16_t)DSAxis(g.leftThumbstick.xAxis.value));
    DSPadPutU16(bytes, 12, (uint16_t)DSAxis(g.leftThumbstick.yAxis.value));
    DSPadPutU16(bytes, 14, (uint16_t)DSAxis(g.rightThumbstick.xAxis.value));
    DSPadPutU16(bytes, 16, (uint16_t)DSAxis(g.rightThumbstick.yAxis.value));
    DSPadPutU32(bytes, 18, sequence);
    bytes[22] = bytes[23] = 0;
    return [NSData dataWithBytes:bytes length:sizeof(bytes)];
}

- (void)sendDueSnapshots {
    if (!self.enabled || !self.negotiated) return;
    uint64_t now = (uint64_t)(NSProcessInfo.processInfo.systemUptime * 1000.0);
    for (NSUInteger i = 0; i < self.controllers.count; i++) {
        uint32_t sequence = self.sequences[i].unsignedIntValue;
        NSData *packet = [self stateForController:self.controllers[i] slot:i sequence:sequence];
        if (packet.length != 24) continue;
        NSData *state = [packet subdataWithRange:NSMakeRange(6, 12)];
        BOOL changed = ![state isEqualToData:self.lastStates[i]];
        BOOL heartbeat = now - self.lastSentMs[i].unsignedLongLongValue >= 250;
        if (!changed && !heartbeat) continue;
        self.sendDatagram(packet);
        self.lastStates[i] = state;
        self.sequences[i] = @(sequence + 1);
        self.lastSentMs[i] = @(now);
    }
}

- (void)sendNeutralSnapshots {
    for (NSUInteger i = 0; i < self.controllers.count; i++) {
        uint32_t sequence = self.sequences[i].unsignedIntValue;
        uint8_t bytes[24] = {'D','S','G','P',1,(uint8_t)i};
        DSPadPutU32(bytes, 18, sequence);
        self.sendDatagram([NSData dataWithBytes:bytes length:sizeof(bytes)]);
        self.sequences[i] = @(sequence + 1);
    }
}

- (void)handleRumbleForController:(NSUInteger)controllerID
                       largeMotor:(uint8_t)largeMotor
                       smallMotor:(uint8_t)smallMotor {
    dispatch_async(self.queue, ^{
        if (controllerID >= self.controllers.count) return;
        NSNumber *key = @(controllerID);
        id<CHHapticAdvancedPatternPlayer> oldPlayer = self.hapticPlayers[key];
        [oldPlayer stopAtTime:0 error:nil];
        [self.hapticPlayers removeObjectForKey:key];
        if (!self.enabled || !self.negotiated || (largeMotor == 0 && smallMotor == 0)) return;

        GCController *controller = self.controllers[controllerID];
        CHHapticEngine *engine = self.hapticEngines[key];
        if (!engine && controller.haptics) {
            engine = [controller.haptics createEngineWithLocality:GCHapticsLocalityDefault];
            if (engine) {
                NSError *startError = nil;
                if ([engine startAndReturnError:&startError]) self.hapticEngines[key] = engine;
                else engine = nil;
            }
        }
        if (!engine) return;
        float intensity = MAX(largeMotor, smallMotor) / 255.0f;
        float sharpness = smallMotor / 255.0f;
        CHHapticEvent *event = [[CHHapticEvent alloc]
            initWithEventType:CHHapticEventTypeHapticContinuous
                  parameters:@[
                      [[CHHapticEventParameter alloc] initWithParameterID:CHHapticEventParameterIDHapticIntensity value:intensity],
                      [[CHHapticEventParameter alloc] initWithParameterID:CHHapticEventParameterIDHapticSharpness value:sharpness],
                  ] relativeTime:0 duration:1.0];
        CHHapticPattern *pattern = [[CHHapticPattern alloc] initWithEvents:@[event] parameters:@[] error:nil];
        id<CHHapticAdvancedPatternPlayer> player = [engine createAdvancedPlayerWithPattern:pattern error:nil];
        player.loopEnabled = YES;
        [player startAtTime:0 error:nil];
        if (player) self.hapticPlayers[key] = player;
    });
}

- (void)stop {
    [self performSynchronouslyOnQueue:^{ [self stopOnQueue]; }];
}

- (void)stopOnQueue {
    if (self.negotiated) {
        [self sendNeutralSnapshots];
        self.sendControl(@{@"type":@"GAMEPAD_STOP"});
        self.negotiated = NO;
    }
    [self stopHapticsOnQueue];
}

- (void)stopHapticsOnQueue {
    for (id<CHHapticAdvancedPatternPlayer> player in self.hapticPlayers.allValues)
        [player stopAtTime:0 error:nil];
    for (CHHapticEngine *engine in self.hapticEngines.allValues)
        [engine stopWithCompletionHandler:nil];
    [self.hapticPlayers removeAllObjects];
    [self.hapticEngines removeAllObjects];
}

- (void)performSynchronouslyOnQueue:(dispatch_block_t)block {
    if (dispatch_get_specific(DSGamepadQueueKey) == DSGamepadQueueKey) block();
    else dispatch_sync(self.queue, block);
}

@end
