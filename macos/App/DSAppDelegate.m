#import "DSAppDelegate.h"

#import "DSAudioPlayer.h"
#import "DSControlClient.h"
#import "DSCredentialStore.h"
#import "DSDiscoveryService.h"
#import "DSFrameAssembler.h"
#import "DSFrameTraceCollector.h"
#import "DSGamepadManager.h"
#import "DSInputController.h"
#import "DSProtocol.h"
#import "DSUDPSocket.h"
#import "DSVideoView.h"

#import <QuartzCore/QuartzCore.h>
#import <UniformTypeIdentifiers/UniformTypeIdentifiers.h>

static const uint16_t DSDefaultControlPort = 47801;
static const NSInteger DSNativeMaxBitrateKbps = 20000;
static const NSInteger DS720pMaxBitrateKbps = 8000;
static NSString * const DSQualityDefaultsKey = @"DSQuality";
static NSString * const DSQuality720MigrationDefaultsKey = @"DSQuality720MigrationV1";

@interface DSAppDelegate ()
@property (nonatomic, strong) NSWindow *window;
@property (nonatomic, strong) NSView *rootView;
@property (nonatomic, strong) NSStackView *connectPanel;
@property (nonatomic, strong) NSPopUpButton *serverPopup;
@property (nonatomic, strong) NSTextField *hostField;
@property (nonatomic, strong) NSTextField *connectionStatus;
@property (nonatomic, strong) NSButton *connectButton;
@property (nonatomic, strong) DSStreamInputView *streamView;
@property (nonatomic, strong) DSVideoView *videoView;
@property (nonatomic, strong) NSView *remoteCursorView;
@property (nonatomic, strong) NSTextField *streamStatus;
@property (nonatomic, strong) NSTextField *streamHint;
@property (nonatomic, strong) NSButton *captureButton;
@property (nonatomic, strong) NSButton *muteButton;
@property (nonatomic, strong) NSButton *statsButton;
@property (nonatomic, strong) NSButton *saveFrameButton;
@property (nonatomic, strong) NSPopUpButton *qualityPopup;
@property (nonatomic, strong) NSView *statusDot;
@property (nonatomic, strong) NSTextField *statsOverlayLabel;
@property (nonatomic) BOOL statsOverlayVisible;
@property (nonatomic) NSUInteger statusMessageToken;
@property (nonatomic, strong) NSMenuItem *muteMenuItem;
@property (nonatomic, strong) NSMenuItem *statsMenuItem;
@property (nonatomic, strong) NSMenuItem *qualityNativeMenuItem;
@property (nonatomic, strong) NSMenuItem *quality720pMenuItem;
@property (nonatomic, strong) NSMutableArray<DSDiscoveredServer *> *servers;

@property (nonatomic, strong) DSDiscoveryService *discovery;
@property (nonatomic, strong) DSCredentialStore *credentials;
@property (nonatomic, strong) DSControlClient *control;
@property (nonatomic, strong) DSUDPSocket *mediaSocket;
@property (nonatomic, strong) DSUDPSocket *audioSocket;
@property (nonatomic, strong) DSFrameAssembler *frameAssembler;
@property (nonatomic, strong) DSFrameTraceCollector *frameTraceCollector;
@property (nonatomic, strong) DSAudioPlayer *audioPlayer;
@property (nonatomic, strong) DSInputController *inputController;
@property (nonatomic, strong) DSGamepadManager *gamepadManager;
@property (nonatomic) dispatch_queue_t mediaQueue;
@property (nonatomic) dispatch_queue_t audioQueue;
@property (nonatomic) dispatch_source_t mediaPunchTimer;
@property (nonatomic) dispatch_source_t audioPunchTimer;
@property (nonatomic) dispatch_source_t statsTimer;

@property (nonatomic, copy) NSString *serverHost;
@property (nonatomic) uint16_t controlPort;
@property (nonatomic) uint16_t mediaPort;
@property (nonatomic) uint16_t audioPort;
@property (nonatomic, copy) NSString *pairingToken;
@property (nonatomic) BOOL wantsConnection;
@property (nonatomic) BOOL streaming;
@property (nonatomic) BOOL streamRequested;
@property (nonatomic) BOOL mediaReceived;
@property (nonatomic) BOOL audioReceived;
@property (nonatomic) BOOL restartRequested;
@property (nonatomic) NSTimeInterval lastMediaAt;
@property (nonatomic) NSUInteger streamWidth;
@property (nonatomic) NSUInteger streamHeight;
@property (nonatomic) NSInteger currentBitrate;
@property (nonatomic) NSUInteger intervalFrames;
@property (nonatomic) NSUInteger intervalDrops;
@property (nonatomic) NSUInteger intervalBytes;
@property (nonatomic) NSTimeInterval lastIDRAt;
@property (nonatomic, copy) NSString *quality;
@end

@implementation DSAppDelegate

@synthesize quality = _quality;

- (NSString *)quality {
    return _quality ?: @"720p";
}

- (void)setQuality:(NSString *)quality {
    NSString *normalized = [quality isEqualToString:@"720p"] ? @"720p" : @"native";
    if ([_quality isEqualToString:normalized]) return;
    _quality = normalized;
    [NSUserDefaults.standardUserDefaults setObject:_quality forKey:DSQualityDefaultsKey];
}

- (void)applicationDidFinishLaunching:(NSNotification *)notification {
    (void)notification;
    self.servers = [NSMutableArray array];
    self.credentials = [[DSCredentialStore alloc] init];
    dispatch_queue_attr_t mediaAttributes = dispatch_queue_attr_make_with_qos_class(
        DISPATCH_QUEUE_SERIAL, QOS_CLASS_USER_INTERACTIVE, 0);
    dispatch_queue_attr_t audioAttributes = dispatch_queue_attr_make_with_qos_class(
        DISPATCH_QUEUE_SERIAL, QOS_CLASS_USER_INITIATED, 0);
    self.mediaQueue = dispatch_queue_create("com.deskstream.macos.media", mediaAttributes);
    self.audioQueue = dispatch_queue_create("com.deskstream.macos.audio-network", audioAttributes);
    self.audioPlayer = [[DSAudioPlayer alloc] init];
    NSUserDefaults *defaults = NSUserDefaults.standardUserDefaults;
    if (![defaults boolForKey:DSQuality720MigrationDefaultsKey]) {
        _quality = @"720p";
        [defaults setObject:_quality forKey:DSQualityDefaultsKey];
        [defaults setBool:YES forKey:DSQuality720MigrationDefaultsKey];
    } else {
        NSString *storedQuality = [defaults stringForKey:DSQualityDefaultsKey];
        _quality = [storedQuality isEqualToString:@"native"] ? @"native" : @"720p";
    }
    [self buildMainMenu];
    [self buildWindow];
    [self buildStreamingComponents];
    [self syncQualityControls];
    [self startDiscovery];

    NSNotificationCenter *workspace = NSWorkspace.sharedWorkspace.notificationCenter;
    [workspace addObserver:self selector:@selector(systemWillSleep:)
                      name:NSWorkspaceWillSleepNotification object:nil];
    [workspace addObserver:self selector:@selector(systemDidWake:)
                      name:NSWorkspaceDidWakeNotification object:nil];

    [self.window makeKeyAndOrderFront:nil];
    [NSApp activateIgnoringOtherApps:YES];
}

- (BOOL)applicationShouldTerminateAfterLastWindowClosed:(NSApplication *)sender {
    (void)sender; return YES;
}

- (void)applicationWillTerminate:(NSNotification *)notification {
    (void)notification;
    [self stopSessionAndNotifyServer:YES];
    [self.control disconnect];
    [self.discovery stop];
    [NSWorkspace.sharedWorkspace.notificationCenter removeObserver:self];
}

- (void)windowWillClose:(NSNotification *)notification {
    (void)notification;
    [NSApp terminate:nil];
}

#pragma mark - Menu bar

- (void)buildMainMenu {
    NSString *appName = NSRunningApplication.currentApplication.localizedName ?: @"DeskStream";
    NSMenu *mainMenu = [[NSMenu alloc] init];

    NSMenuItem *appMenuItem = [[NSMenuItem alloc] init];
    NSMenu *appMenu = [[NSMenu alloc] init];
    [appMenu addItemWithTitle:[NSString stringWithFormat:@"About %@", appName]
                        action:@selector(orderFrontStandardAboutPanel:) keyEquivalent:@""];
    [appMenu addItem:[NSMenuItem separatorItem]];
    [appMenu addItemWithTitle:[NSString stringWithFormat:@"Quit %@", appName]
                        action:@selector(terminate:) keyEquivalent:@"q"];
    appMenuItem.submenu = appMenu;
    [mainMenu addItem:appMenuItem];

    NSMenuItem *fileMenuItem = [[NSMenuItem alloc] init];
    NSMenu *fileMenu = [[NSMenu alloc] initWithTitle:@"File"];
    NSMenuItem *saveFrameItem = [fileMenu addItemWithTitle:@"Save Frame"
                                                     action:@selector(savePressed:) keyEquivalent:@"s"];
    saveFrameItem.target = self;
    fileMenuItem.submenu = fileMenu;
    [mainMenu addItem:fileMenuItem];

    NSMenuItem *streamMenuItem = [[NSMenuItem alloc] init];
    NSMenu *streamMenu = [[NSMenu alloc] initWithTitle:@"Stream"];
    self.muteMenuItem = [streamMenu addItemWithTitle:@"Mute" action:@selector(mutePressed:) keyEquivalent:@""];
    self.muteMenuItem.target = self;
    self.statsMenuItem = [streamMenu addItemWithTitle:@"Show Stats Overlay"
                                                action:@selector(statsPressed:) keyEquivalent:@"i"];
    self.statsMenuItem.target = self;
    [streamMenu addItem:[NSMenuItem separatorItem]];

    NSMenuItem *qualityItem = [[NSMenuItem alloc] initWithTitle:@"Quality" action:NULL keyEquivalent:@""];
    NSMenu *qualityMenu = [[NSMenu alloc] initWithTitle:@"Quality"];
    self.qualityNativeMenuItem = [qualityMenu addItemWithTitle:@"Native"
                                                          action:@selector(qualityMenuItemSelected:) keyEquivalent:@""];
    self.qualityNativeMenuItem.target = self;
    self.qualityNativeMenuItem.representedObject = @"native";
    self.quality720pMenuItem = [qualityMenu addItemWithTitle:@"720p"
                                                        action:@selector(qualityMenuItemSelected:) keyEquivalent:@""];
    self.quality720pMenuItem.target = self;
    self.quality720pMenuItem.representedObject = @"720p";
    qualityItem.submenu = qualityMenu;
    [streamMenu addItem:qualityItem];
    [streamMenu addItem:[NSMenuItem separatorItem]];

    NSMenuItem *disconnectItem = [streamMenu addItemWithTitle:@"Disconnect"
                                                        action:@selector(disconnectPressed:) keyEquivalent:@"w"];
    disconnectItem.target = self;
    streamMenuItem.submenu = streamMenu;
    [mainMenu addItem:streamMenuItem];

    NSMenuItem *windowMenuItem = [[NSMenuItem alloc] init];
    NSMenu *windowMenu = [[NSMenu alloc] initWithTitle:@"Window"];
    [windowMenu addItemWithTitle:@"Minimize" action:@selector(performMiniaturize:) keyEquivalent:@"m"];
    [windowMenu addItemWithTitle:@"Zoom" action:@selector(performZoom:) keyEquivalent:@""];
    NSMenuItem *fullScreenItem = [windowMenu addItemWithTitle:@"Enter Full Screen"
                                                        action:@selector(toggleFullScreen:) keyEquivalent:@"f"];
    fullScreenItem.keyEquivalentModifierMask = NSEventModifierFlagCommand | NSEventModifierFlagControl;
    windowMenuItem.submenu = windowMenu;
    [mainMenu addItem:windowMenuItem];
    NSApp.windowsMenu = windowMenu;

    NSMenuItem *helpMenuItem = [[NSMenuItem alloc] init];
    NSMenu *helpMenu = [[NSMenu alloc] initWithTitle:@"Help"];
    [helpMenu addItemWithTitle:[NSString stringWithFormat:@"%@ Help", appName] action:NULL keyEquivalent:@""];
    helpMenuItem.submenu = helpMenu;
    [mainMenu addItem:helpMenuItem];
    NSApp.helpMenu = helpMenu;

    NSApp.mainMenu = mainMenu;
}

#pragma mark - UI

- (NSTextField *)label:(NSString *)text size:(CGFloat)size {
    NSTextField *label = [NSTextField labelWithString:text];
    label.font = [NSFont systemFontOfSize:size];
    label.textColor = NSColor.labelColor;
    label.alignment = NSTextAlignmentCenter;
    label.translatesAutoresizingMaskIntoConstraints = NO;
    return label;
}

- (NSButton *)button:(NSString *)title action:(SEL)action {
    NSButton *button = [NSButton buttonWithTitle:title target:self action:action];
    button.bezelStyle = NSBezelStyleRounded;
    button.translatesAutoresizingMaskIntoConstraints = NO;
    return button;
}

- (void)buildWindow {
    NSRect frame = NSMakeRect(0, 0, 1280, 760);
    self.window = [[NSWindow alloc]
        initWithContentRect:frame
                  styleMask:NSWindowStyleMaskTitled | NSWindowStyleMaskClosable |
                            NSWindowStyleMaskMiniaturizable | NSWindowStyleMaskResizable
                    backing:NSBackingStoreBuffered defer:NO];
    self.window.title = @"DeskStream";
    self.window.delegate = self;
    self.window.minSize = NSMakeSize(900, 540);
    [self.window center];

    self.rootView = [[NSView alloc] initWithFrame:frame];
    self.rootView.wantsLayer = YES;
    self.rootView.layer.backgroundColor = NSColor.windowBackgroundColor.CGColor;
    self.window.contentView = self.rootView;

    NSTextField *title = [self label:@"DeskStream" size:30];
    title.font = [NSFont systemFontOfSize:30 weight:NSFontWeightSemibold];
    NSTextField *subtitle = [self label:@"Low-latency Windows streaming on this LAN" size:14];
    subtitle.textColor = NSColor.secondaryLabelColor;

    self.serverPopup = [[NSPopUpButton alloc] initWithFrame:NSZeroRect pullsDown:NO];
    self.serverPopup.translatesAutoresizingMaskIntoConstraints = NO;
    [self.serverPopup addItemWithTitle:@"Manual IP address"];
    self.serverPopup.target = self;
    self.serverPopup.action = @selector(serverSelectionChanged:);

    self.hostField = [[NSTextField alloc] initWithFrame:NSZeroRect];
    self.hostField.placeholderString = @"Windows PC address (for example 192.168.1.59)";
    self.hostField.translatesAutoresizingMaskIntoConstraints = NO;

    NSButton *refresh = [self button:@"Refresh LAN" action:@selector(refreshPressed:)];
    self.connectButton = [self button:@"Connect" action:@selector(connectPressed:)];
    self.connectButton.keyEquivalent = @"\r";
    NSStackView *actions = [NSStackView stackViewWithViews:@[refresh, self.connectButton]];
    actions.orientation = NSUserInterfaceLayoutOrientationHorizontal;
    actions.spacing = 10;
    actions.distribution = NSStackViewDistributionFillEqually;

    self.connectionStatus = [self label:@"Searching for DeskStream servers…" size:13];
    self.connectionStatus.textColor = NSColor.secondaryLabelColor;
    self.connectionStatus.maximumNumberOfLines = 3;

    NSString *version = [NSBundle.mainBundle objectForInfoDictionaryKey:@"CFBundleShortVersionString"] ?: @"0.8.1";
    NSTextField *versionLabel = [self label:[NSString stringWithFormat:@"Client Version: %@", version] size:11];
    versionLabel.textColor = NSColor.tertiaryLabelColor;

    self.connectPanel = [NSStackView stackViewWithViews:@[
        title, subtitle, self.serverPopup, self.hostField, actions, self.connectionStatus, versionLabel
    ]];
    self.connectPanel.orientation = NSUserInterfaceLayoutOrientationVertical;
    self.connectPanel.alignment = NSLayoutAttributeCenterX;
    self.connectPanel.spacing = 14;
    self.connectPanel.translatesAutoresizingMaskIntoConstraints = NO;
    [self.rootView addSubview:self.connectPanel];

    [NSLayoutConstraint activateConstraints:@[
        [self.connectPanel.centerXAnchor constraintEqualToAnchor:self.rootView.centerXAnchor],
        [self.connectPanel.centerYAnchor constraintEqualToAnchor:self.rootView.centerYAnchor],
        [self.connectPanel.widthAnchor constraintLessThanOrEqualToConstant:560],
        [self.connectPanel.leadingAnchor constraintGreaterThanOrEqualToAnchor:self.rootView.leadingAnchor constant:40],
        [self.serverPopup.widthAnchor constraintEqualToConstant:500],
        [self.hostField.widthAnchor constraintEqualToConstant:500],
        [actions.widthAnchor constraintEqualToConstant:500],
        [self.connectionStatus.widthAnchor constraintEqualToConstant:500],
        [versionLabel.widthAnchor constraintEqualToConstant:500],
    ]];

    self.streamView = [[DSStreamInputView alloc] initWithFrame:frame];
    self.streamView.translatesAutoresizingMaskIntoConstraints = NO;
    self.streamView.wantsLayer = YES;
    self.streamView.layer.backgroundColor = NSColor.blackColor.CGColor;
    self.streamView.hidden = YES;
    [self.rootView addSubview:self.streamView];
    [NSLayoutConstraint activateConstraints:@[
        [self.streamView.leadingAnchor constraintEqualToAnchor:self.rootView.leadingAnchor],
        [self.streamView.trailingAnchor constraintEqualToAnchor:self.rootView.trailingAnchor],
        [self.streamView.topAnchor constraintEqualToAnchor:self.rootView.topAnchor],
        [self.streamView.bottomAnchor constraintEqualToAnchor:self.rootView.bottomAnchor],
    ]];

    self.videoView = [[DSVideoView alloc] initWithFrame:frame];
    self.videoView.translatesAutoresizingMaskIntoConstraints = NO;
    [self.streamView addSubview:self.videoView];
    [NSLayoutConstraint activateConstraints:@[
        [self.videoView.leadingAnchor constraintEqualToAnchor:self.streamView.leadingAnchor],
        [self.videoView.trailingAnchor constraintEqualToAnchor:self.streamView.trailingAnchor],
        [self.videoView.topAnchor constraintEqualToAnchor:self.streamView.topAnchor],
        [self.videoView.bottomAnchor constraintEqualToAnchor:self.streamView.bottomAnchor],
    ]];

    self.remoteCursorView = [[NSView alloc] initWithFrame:NSMakeRect(0, 0, 12, 18)];
    self.remoteCursorView.wantsLayer = YES;
    self.remoteCursorView.hidden = YES;
    CAShapeLayer *cursorShape = [CAShapeLayer layer];
    CGMutablePathRef cursorPath = CGPathCreateMutable();
    CGPathMoveToPoint(cursorPath, NULL, 0, 18);
    CGPathAddLineToPoint(cursorPath, NULL, 0, 1);
    CGPathAddLineToPoint(cursorPath, NULL, 5, 6);
    CGPathAddLineToPoint(cursorPath, NULL, 8, 0);
    CGPathAddLineToPoint(cursorPath, NULL, 11, 2);
    CGPathAddLineToPoint(cursorPath, NULL, 8, 8);
    CGPathAddLineToPoint(cursorPath, NULL, 12, 8);
    CGPathCloseSubpath(cursorPath);
    cursorShape.path = cursorPath;
    cursorShape.fillColor = NSColor.whiteColor.CGColor;
    cursorShape.strokeColor = NSColor.blackColor.CGColor;
    cursorShape.lineWidth = 1.2;
    cursorShape.frame = self.remoteCursorView.bounds;
    [self.remoteCursorView.layer addSublayer:cursorShape];
    CGPathRelease(cursorPath);
    [self.streamView addSubview:self.remoteCursorView];

    NSVisualEffectView *toolbar = [[NSVisualEffectView alloc] initWithFrame:NSZeroRect];
    toolbar.material = NSVisualEffectMaterialHUDWindow;
    toolbar.blendingMode = NSVisualEffectBlendingModeWithinWindow;
    toolbar.state = NSVisualEffectStateActive;
    toolbar.translatesAutoresizingMaskIntoConstraints = NO;
    toolbar.wantsLayer = YES;
    toolbar.layer.cornerRadius = 9;

    self.statusDot = [[NSView alloc] initWithFrame:NSMakeRect(0, 0, 10, 10)];
    self.statusDot.wantsLayer = YES;
    self.statusDot.layer.cornerRadius = 5;
    self.statusDot.layer.backgroundColor = NSColor.systemGrayColor.CGColor;
    self.statusDot.translatesAutoresizingMaskIntoConstraints = NO;
    self.statusDot.toolTip = @"Connection state";
    [NSLayoutConstraint activateConstraints:@[
        [self.statusDot.widthAnchor constraintEqualToConstant:10],
        [self.statusDot.heightAnchor constraintEqualToConstant:10],
    ]];

    self.streamStatus = [self label:@"Connecting…" size:12];
    self.streamStatus.alignment = NSTextAlignmentLeft;
    self.captureButton = [self button:@"Capture Input" action:@selector(capturePressed:)];
    self.captureButton.toolTip = @"Capture the keyboard and mouse for direct control (⌃⌥Esc releases)";
    self.muteButton = [self button:@"Mute" action:@selector(mutePressed:)];
    self.muteButton.toolTip = @"Mute or unmute remote system audio";
    self.statsButton = [self button:@"Stats" action:@selector(statsPressed:)];
    self.statsButton.toolTip = @"Show or hide the on-screen stats overlay";
    self.qualityPopup = [[NSPopUpButton alloc] initWithFrame:NSZeroRect pullsDown:NO];
    self.qualityPopup.translatesAutoresizingMaskIntoConstraints = NO;
    [self.qualityPopup addItemWithTitle:@"Native"];
    [self.qualityPopup addItemWithTitle:@"720p"];
    self.qualityPopup.target = self;
    self.qualityPopup.action = @selector(qualityPopupChanged:);
    self.qualityPopup.toolTip = @"Stream quality: native resolution or a 720-line downscale";
    self.saveFrameButton = [self button:@"Save Frame" action:@selector(savePressed:)];
    self.saveFrameButton.toolTip = @"Save the current frame as a PNG (⌘S)";
    NSButton *fullscreen = [self button:@"Full Screen" action:@selector(fullscreenPressed:)];
    fullscreen.toolTip = @"Toggle full screen";
    NSButton *disconnect = [self button:@"Disconnect" action:@selector(disconnectPressed:)];
    disconnect.toolTip = @"Disconnect from the PC";
    NSStackView *toolbarStack = [NSStackView stackViewWithViews:@[
        self.statusDot, self.streamStatus, self.captureButton, self.muteButton, self.statsButton,
        self.qualityPopup, self.saveFrameButton, fullscreen, disconnect
    ]];
    toolbarStack.orientation = NSUserInterfaceLayoutOrientationHorizontal;
    toolbarStack.spacing = 8;
    toolbarStack.edgeInsets = NSEdgeInsetsMake(7, 10, 7, 10);
    toolbarStack.translatesAutoresizingMaskIntoConstraints = NO;
    [toolbar addSubview:toolbarStack];
    [self.streamView addSubview:toolbar];
    [NSLayoutConstraint activateConstraints:@[
        [toolbar.topAnchor constraintEqualToAnchor:self.streamView.topAnchor constant:12],
        [toolbar.leadingAnchor constraintEqualToAnchor:self.streamView.leadingAnchor constant:12],
        [toolbar.trailingAnchor constraintLessThanOrEqualToAnchor:self.streamView.trailingAnchor constant:-12],
        [toolbarStack.leadingAnchor constraintEqualToAnchor:toolbar.leadingAnchor],
        [toolbarStack.trailingAnchor constraintEqualToAnchor:toolbar.trailingAnchor],
        [toolbarStack.topAnchor constraintEqualToAnchor:toolbar.topAnchor],
        [toolbarStack.bottomAnchor constraintEqualToAnchor:toolbar.bottomAnchor],
        [self.streamStatus.widthAnchor constraintGreaterThanOrEqualToConstant:300],
    ]];

    self.streamHint = [self label:@"Click to control directly · Capture Input for games · ⌃⌥Esc releases capture" size:12];
    self.streamHint.textColor = NSColor.whiteColor;
    self.streamHint.wantsLayer = YES;
    self.streamHint.layer.backgroundColor = [NSColor colorWithWhite:0 alpha:0.62].CGColor;
    self.streamHint.layer.cornerRadius = 6;
    [self.streamView addSubview:self.streamHint];
    [NSLayoutConstraint activateConstraints:@[
        [self.streamHint.bottomAnchor constraintEqualToAnchor:self.streamView.bottomAnchor constant:-14],
        [self.streamHint.centerXAnchor constraintEqualToAnchor:self.streamView.centerXAnchor],
        [self.streamHint.widthAnchor constraintLessThanOrEqualToAnchor:self.streamView.widthAnchor constant:-40],
    ]];

    self.statsOverlayLabel = [self label:@"" size:11];
    self.statsOverlayLabel.alignment = NSTextAlignmentRight;
    self.statsOverlayLabel.textColor = NSColor.whiteColor;
    self.statsOverlayLabel.wantsLayer = YES;
    self.statsOverlayLabel.layer.backgroundColor = [NSColor colorWithWhite:0 alpha:0.62].CGColor;
    self.statsOverlayLabel.layer.cornerRadius = 5;
    self.statsOverlayLabel.hidden = YES;
    self.statsOverlayLabel.toolTip = @"Local capture stats";
    [self.streamView addSubview:self.statsOverlayLabel];
    [NSLayoutConstraint activateConstraints:@[
        [self.statsOverlayLabel.topAnchor constraintEqualToAnchor:self.streamView.topAnchor constant:12],
        [self.statsOverlayLabel.trailingAnchor constraintEqualToAnchor:self.streamView.trailingAnchor constant:-12],
    ]];
}

- (void)buildStreamingComponents {
    __weak typeof(self) weakSelf = self;
    self.frameTraceCollector = [[DSFrameTraceCollector alloc] init];
    self.inputController = [[DSInputController alloc]
        initWithView:self.streamView
        sendDatagram:^(NSData *datagram) { [weakSelf sendMediaDatagram:datagram]; }
        sendControl:^(NSDictionary<NSString *,id> *message) { [weakSelf sendControl:message]; }];
    self.inputController.captureChangedHandler = ^(BOOL captured) {
        weakSelf.captureButton.title = captured ? @"Release ⌃⌥Esc" : @"Capture Input";
        if (!captured) weakSelf.remoteCursorView.hidden = YES;
    };
    self.gamepadManager = [[DSGamepadManager alloc]
        initWithDatagramSender:^(NSData *datagram) { [weakSelf sendMediaDatagram:datagram]; }
        controlSender:^(NSDictionary<NSString *,id> *message) { [weakSelf sendControl:message]; }];
    self.gamepadManager.inventoryChangedHandler = ^(NSArray<NSString *> *names) {
        [weakSelf updateStreamStatusWithSuffix:names.count
            ? [NSString stringWithFormat:@"%lu controller%@", names.count, names.count == 1 ? @"" : @"s"]
            : @"no controller"];
    };
    self.videoView.discardUntilKeyframeHandler = ^{
        [weakSelf.frameAssembler requestDiscardUntilKeyframe];
    };
    self.videoView.requestIDRHandler = ^{ [weakSelf sendIDRRequestNow]; };
    self.videoView.frameTraceHandler = ^(uint32_t frameID, uint64_t decodeSubmit,
                                         uint64_t presentEnqueue) {
        [weakSelf.frameTraceCollector recordRendererSubmissionForFrameID:frameID
                                                decodeSubmitMicroseconds:decodeSubmit
                                               presentEnqueueMicroseconds:presentEnqueue];
    };

    self.frameAssembler = [[DSFrameAssembler alloc]
        initWithOutputHandler:^(NSData *accessUnit, BOOL keyframe, uint32_t frameID,
                                uint32_t pts, uint16_t pipelineDelay) {
            (void)pts; (void)pipelineDelay;
            [weakSelf.frameTraceCollector recordAssembleForFrameID:frameID
                                                    atMicroseconds:DSMonotonicMicroseconds()];
            @synchronized (weakSelf) { weakSelf.intervalFrames++; }
            [weakSelf.videoView enqueueAnnexBAccessUnit:accessUnit keyframe:keyframe frameID:frameID];
        } dropHandler:^{
            @synchronized (weakSelf) { weakSelf.intervalDrops++; }
            [weakSelf requestIDR];
        }];
}

- (void)showConnectUI {
    self.connectPanel.hidden = NO;
    self.streamView.hidden = YES;
    self.remoteCursorView.hidden = YES;
}

- (void)showStreamUI {
    self.connectPanel.hidden = YES;
    self.streamView.hidden = NO;
    [self.window makeFirstResponder:self.streamView];
}

#pragma mark - Discovery and connection

- (void)startDiscovery {
    __weak typeof(self) weakSelf = self;
    self.discovery = [[DSDiscoveryService alloc]
        initWithCallbackQueue:dispatch_get_main_queue()
        handler:^(DSDiscoveredServer *server) { [weakSelf discoveredServer:server]; }];
    NSError *error = nil;
    if (![self.discovery start:&error]) {
        self.connectionStatus.stringValue = [NSString stringWithFormat:@"LAN discovery unavailable: %@ · Manual IP still works", error.localizedDescription];
    }
}

- (void)discoveredServer:(DSDiscoveredServer *)server {
    NSUInteger index = [self.servers indexOfObjectPassingTest:^BOOL(DSDiscoveredServer *candidate,
                                                                    NSUInteger idx, BOOL *stop) {
        (void)idx; (void)stop;
        return [candidate.host isEqualToString:server.host] && candidate.controlPort == server.controlPort;
    }];
    if (index == NSNotFound) {
        [self.servers addObject:server];
        [self.serverPopup addItemWithTitle:[NSString stringWithFormat:@"%@ — %@", server.name, server.host]];
        if (self.servers.count == 1 && self.hostField.stringValue.length == 0)
            [self.serverPopup selectItemAtIndex:1];
    }
    self.connectionStatus.stringValue = [NSString stringWithFormat:@"Found %lu DeskStream server%@ on this LAN",
        self.servers.count, self.servers.count == 1 ? @"" : @"s"];
}

- (void)refreshPressed:(id)sender { (void)sender; [self.discovery probeNow]; }

- (void)serverSelectionChanged:(id)sender {
    (void)sender;
    NSInteger selected = self.serverPopup.indexOfSelectedItem;
    self.hostField.enabled = selected <= 0;
    if (selected > 0 && (NSUInteger)selected <= self.servers.count)
        self.hostField.stringValue = self.servers[(NSUInteger)selected - 1].host;
}

- (void)connectPressed:(id)sender {
    (void)sender;
    NSString *host = [self.hostField.stringValue stringByTrimmingCharactersInSet:NSCharacterSet.whitespaceAndNewlineCharacterSet];
    uint16_t port = DSDefaultControlPort;
    NSInteger selected = self.serverPopup.indexOfSelectedItem;
    if (selected > 0 && (NSUInteger)selected <= self.servers.count) {
        DSDiscoveredServer *server = self.servers[(NSUInteger)selected - 1];
        host = server.host; port = server.controlPort;
    }
    if (host.length == 0) {
        self.connectionStatus.stringValue = @"Enter the Windows PC address or select a discovered server.";
        return;
    }
    [self connectToHost:host port:port];
}

- (void)connectToHost:(NSString *)host port:(uint16_t)port {
    [self.control disconnect];
    [self stopSessionAndNotifyServer:NO];
    self.serverHost = host;
    self.controlPort = port;
    self.wantsConnection = YES;
    self.streamRequested = NO;
    self.restartRequested = NO;
    self.connectButton.enabled = NO;
    self.connectionStatus.stringValue = [NSString stringWithFormat:@"Connecting to %@…", host];
    [self.discovery stop];

    NSError *credentialError = nil;
    self.pairingToken = [self.credentials tokenForServer:host error:&credentialError] ?: @"";
    __weak typeof(self) weakSelf = self;
    self.control = [[DSControlClient alloc]
        initWithHost:host port:port callbackQueue:dispatch_get_main_queue()
        messageHandler:^(NSDictionary<NSString *,id> *message) { [weakSelf handleControlMessage:message]; }
        stateHandler:^(DSControlState state) { [weakSelf handleControlState:state]; }
        errorHandler:^(NSError *error) { [weakSelf handleControlError:error]; }];
    self.control.reconnectHandler = ^(NSUInteger attempt, NSTimeInterval delay) {
        weakSelf.connectionStatus.stringValue = [NSString stringWithFormat:@"Connection lost · retry %lu in %.1f s", attempt, delay];
        weakSelf.streamStatus.stringValue = weakSelf.connectionStatus.stringValue;
    };
    [self.control connect];
}

- (void)handleControlState:(DSControlState)state {
    switch (state) {
        case DSControlStateConnecting:
            self.connectionStatus.stringValue = @"Connecting…";
            [self setConnectionIndicatorColor:NSColor.systemGrayColor];
            break;
        case DSControlStateConnected:
            [self sendHello];
            break;
        case DSControlStateReconnecting:
            [self stopSessionAndNotifyServer:NO];
            self.streamStatus.stringValue = @"Reconnecting to the PC…";
            [self setConnectionIndicatorColor:NSColor.systemOrangeColor];
            break;
        case DSControlStateDisconnected:
            self.connectButton.enabled = YES;
            [self setConnectionIndicatorColor:NSColor.systemGrayColor];
            if (!self.wantsConnection) [self showConnectUI];
            break;
    }
}

- (void)setConnectionIndicatorColor:(NSColor *)color {
    self.statusDot.layer.backgroundColor = color.CGColor;
}

- (void)handleControlError:(NSError *)error {
    self.connectionStatus.stringValue = [NSString stringWithFormat:@"%@", error.localizedDescription];
    self.streamStatus.stringValue = [NSString stringWithFormat:@"Connection problem · %@", error.localizedDescription];
}

- (void)sendHello {
    NSError *error = nil;
    NSString *clientID = [self.credentials clientIdentifier:&error];
    if (!clientID) {
        [self handleControlError:error];
        return;
    }
    NSString *name = NSHost.currentHost.localizedName ?: @"Apple-silicon Mac";
    [self sendControl:@{@"type":@"HELLO", @"ver":@1, @"clientId":clientID,
                        @"clientName":name, @"token":self.pairingToken ?: @""}];
    self.connectionStatus.stringValue = @"Authenticating…";
}

- (void)handleControlMessage:(NSDictionary<NSString *,id> *)message {
    NSString *type = [message[@"type"] isKindOfClass:NSString.class] ? message[@"type"] : @"";
    if ([type isEqualToString:@"PAIR_REQUIRED"]) {
        if (self.pairingToken.length) [self.credentials removeTokenForServer:self.serverHost error:nil];
        self.pairingToken = @"";
        [self sendControl:@{@"type":@"PAIR_REQUEST"}];
        [self presentPairingPromptWithAttempts:3];
    } else if ([type isEqualToString:@"PAIR_OK"]) {
        NSString *token = [message[@"token"] isKindOfClass:NSString.class] ? message[@"token"] : @"";
        if (token.length) {
            self.pairingToken = token;
            [self.credentials setToken:token forServer:self.serverHost error:nil];
            [self sendHello];
        }
    } else if ([type isEqualToString:@"PAIR_FAIL"]) {
        NSInteger attempts = [message[@"attemptsLeft"] integerValue];
        [self presentPairingPromptWithAttempts:attempts];
    } else if ([type isEqualToString:@"HELLO_OK"]) {
        self.connectionStatus.stringValue = @"Authenticated · starting stream…";
        if (!self.streamRequested) {
            self.streamRequested = YES;
            [self sendControl:[self startStreamMessage]];
        }
    } else if ([type isEqualToString:@"STREAM_STARTED"]) {
        [self beginStreamWithMessage:message];
    } else if ([type isEqualToString:@"AUDIO_STARTED"]) {
        [self beginAudioWithMessage:message];
    } else if ([type isEqualToString:@"AUDIO_UNAVAILABLE"]) {
        [self updateStreamStatusWithSuffix:@"audio unavailable"];
    } else if ([type isEqualToString:@"INPUT_STARTED"]) {
        BOOL mouse = [message[@"mouse"] boolValue];
        BOOL keyboard = [message[@"keyboard"] boolValue];
        self.inputController.enabled = mouse;
        self.inputController.keyboardEnabled = keyboard;
        [self updateStreamStatusWithSuffix:keyboard ? @"mouse + keyboard ready" : @"mouse only · update server for keyboard"];
    } else if ([type isEqualToString:@"INPUT_UNAVAILABLE"]) {
        self.inputController.enabled = NO;
        [self updateStreamStatusWithSuffix:@"remote input unavailable"];
    } else if ([type isEqualToString:@"GAMEPAD_RUMBLE"]) {
        [self.gamepadManager handleRumbleForController:[message[@"controllerId"] unsignedIntegerValue]
                                           largeMotor:(uint8_t)[message[@"largeMotor"] unsignedIntValue]
                                           smallMotor:(uint8_t)[message[@"smallMotor"] unsignedIntValue]];
    } else if ([type isEqualToString:@"GAMEPAD_UNAVAILABLE"]) {
        [self updateStreamStatusWithSuffix:@"ViGEm gamepad unavailable"];
    } else if ([type isEqualToString:@"BITRATE"]) {
        self.currentBitrate = [message[@"kbps"] integerValue];
        [self updateStreamStatusWithSuffix:nil];
    } else if ([type isEqualToString:@"PONG"]) {
        uint64_t t0 = [message[@"t0Us"] unsignedLongLongValue];
        uint64_t t1 = [message[@"t1Us"] unsignedLongLongValue];
        uint64_t t2 = [message[@"t2Us"] unsignedLongLongValue];
        uint64_t t3 = DSMonotonicMicroseconds();
        uint64_t elapsed = t3 >= t0 ? t3 - t0 : 0;
        uint64_t serverWork = t2 >= t1 ? t2 - t1 : UINT64_MAX;
        if (t0 > 0 && t1 >= t0 && t2 >= t1 && elapsed >= serverWork) {
            uint64_t roundTrip = elapsed - serverWork;
            int64_t offset = ((int64_t)t1 - (int64_t)t0 +
                              (int64_t)t2 - (int64_t)t3) / 2;
            [self.frameTraceCollector updateClockOffsetMicroseconds:offset
                                              roundTripMicroseconds:roundTrip];
        }
    } else if ([type isEqualToString:@"STREAM_STOPPED"]) {
        self.streaming = NO;
        self.streamRequested = NO;
        if (self.restartRequested && self.wantsConnection) {
            self.restartRequested = NO;
            self.streamRequested = YES;
            [self sendControl:[self startStreamMessage]];
        }
    } else if ([type isEqualToString:@"ERROR"]) {
        NSString *detail = [message[@"message"] isKindOfClass:NSString.class] ? message[@"message"] : @"Server error";
        self.connectionStatus.stringValue = detail;
        self.streamStatus.stringValue = detail;
    }
}

- (void)presentPairingPromptWithAttempts:(NSInteger)attempts {
    NSAlert *alert = [[NSAlert alloc] init];
    alert.messageText = @"Pair with the Windows PC";
    alert.informativeText = [NSString stringWithFormat:@"Enter the six-digit PIN shown by the server.%@",
        attempts < 3 ? [NSString stringWithFormat:@" %ld attempt%@ left.", attempts, attempts == 1 ? @"" : @"s"] : @""];
    [alert addButtonWithTitle:@"Pair"];
    [alert addButtonWithTitle:@"Cancel"];
    NSTextField *pin = [[NSTextField alloc] initWithFrame:NSMakeRect(0, 0, 220, 28)];
    pin.placeholderString = @"000000";
    alert.accessoryView = pin;
    [alert beginSheetModalForWindow:self.window completionHandler:^(NSModalResponse response) {
        if (response == NSAlertFirstButtonReturn) {
            NSString *value = [pin.stringValue stringByTrimmingCharactersInSet:NSCharacterSet.whitespaceCharacterSet];
            [self sendControl:@{@"type":@"PAIR_CODE", @"pin":value}];
        } else {
            [self disconnectPressed:nil];
        }
    }];
}

#pragma mark - Stream/media/audio

- (void)beginStreamWithMessage:(NSDictionary *)message {
    [self stopSessionAndNotifyServer:NO];
    self.streaming = YES;
    self.streamRequested = YES;
    self.mediaPort = (uint16_t)[message[@"mediaPort"] unsignedIntValue];
    self.streamWidth = [message[@"width"] unsignedIntegerValue];
    self.streamHeight = [message[@"height"] unsignedIntegerValue];
    self.currentBitrate = 8000;
    self.mediaReceived = NO;
    self.lastMediaAt = NSProcessInfo.processInfo.systemUptime;
    self.lastIDRAt = 0;
    [self.frameTraceCollector reset];
    // stopSessionAndNotifyServer: already reset both the assembler and renderer. Do not enqueue a
    // second asynchronous renderer flush here: it could consume the startup IDR from the server.
    [self showStreamUI];
    [self setConnectionIndicatorColor:NSColor.systemGreenColor];

    __weak typeof(self) weakSelf = self;
    NSError *error = nil;
    self.mediaSocket = [[DSUDPSocket alloc]
        initWithExpectedHost:self.serverHost expectedPort:self.mediaPort queue:self.mediaQueue
        rawHandler:^(const void *bytes, size_t length) {
            [weakSelf consumeMediaBytes:bytes length:length];
        } error:&error];
    if (!self.mediaSocket || ![self.mediaSocket bindToPort:0 receiveBufferSize:256 * 1024 error:&error] ||
        ![self.mediaSocket start:&error]) {
        [self handleControlError:error];
        return;
    }
    [self sendControl:@{@"type":@"MEDIA_READY", @"port":@(self.mediaSocket.localPort)}];
    [self sendMediaHolePunch];
    [self installMediaPunchTimer];
    [self installStatsTimer];
    [self requestIDR];

    [self sendControl:@{@"type":@"AUDIO_START"}];
    [self sendControl:@{@"type":@"INPUT_START", @"mouse":@YES, @"keyboard":@YES}];
    self.gamepadManager.enabled = YES;
    [self updateStreamStatusWithSuffix:@"negotiating input + audio"];
}

- (void)consumeMediaBytes:(const void *)rawBytes length:(size_t)length {
    const uint8_t *bytes = rawBytes;
    DSFrameTrace trace;
    if (DSParseFrameTraceDatagram(bytes, length, &trace)) {
        [self.frameTraceCollector recordServerTrace:trace];
        return;
    }
    if (length == 4 && memcmp(bytes, "DSHB", 4) == 0) {
        self.mediaReceived = YES;
        self.lastMediaAt = NSProcessInfo.processInfo.systemUptime;
        // A static-desktop heartbeat proves the UDP path is alive, but it must not hide a lost
        // reference chain. Keep asking for an IDR until the assembler accepts one.
        if (self.frameAssembler.discardingUntilKeyframe) [self requestIDR];
        return;
    }
    if (length == 16 && memcmp(bytes, "DSMC", 4) == 0) {
        if (bytes[4] == 1) {
            self.lastMediaAt = NSProcessInfo.processInfo.systemUptime;
            [self updateRemoteCursorX:DSReadUInt16BigEndian(bytes + 12)
                                     y:DSReadUInt16BigEndian(bytes + 14)];
        }
        if (self.frameAssembler.discardingUntilKeyframe) [self requestIDR];
        return;
    }
    self.mediaReceived = YES;
    self.lastMediaAt = NSProcessInfo.processInfo.systemUptime;
    @synchronized (self) { self.intervalBytes += length; }
    DSMediaHeader mediaHeader;
    if (DSParseMediaDatagram(bytes, length, &mediaHeader, NULL)) {
        [self.frameTraceCollector recordReceiveForFrameID:mediaHeader.frameID
                                           atMicroseconds:DSMonotonicMicroseconds()];
    }
    // DSUDPSocket's raw callback is synchronous; the assembler copies packet payload into its
    // bounded frame state before returning, eliminating one NSData allocation/copy per packet.
    [self.frameAssembler consumeBytes:bytes length:length];
    // The server uses an effectively-infinite GOP and only emits a keyframe on REQUEST_IDR, so
    // once the assembler is discarding-until-keyframe a single lost or 300 ms-shadowed IDR
    // request would leave the picture black until the next natural media lull triggers the 1.5 s
    // watchdog — the "seconds of black then self-recovery" symptom. While discarding, keep
    // (re-)requesting on every arriving datagram; -requestIDR rate-limits to one per 0.3 s, which
    // dodges the server shadow and heals within a frame or two under continuous traffic.
    if (self.frameAssembler.discardingUntilKeyframe) [self requestIDR];
}

- (void)updateRemoteCursorX:(uint16_t)x y:(uint16_t)y {
    dispatch_async(dispatch_get_main_queue(), ^{
        if (!self.inputController.pointerCaptured || self.streamView.hidden) {
            self.remoteCursorView.hidden = YES;
            return;
        }
        NSRect bounds = self.videoView.frame;
        if (self.streamWidth && self.streamHeight) {
            CGFloat sourceAspect = (CGFloat)self.streamWidth / self.streamHeight;
            CGFloat viewAspect = bounds.size.width / MAX(1, bounds.size.height);
            if (viewAspect > sourceAspect) {
                CGFloat width = bounds.size.height * sourceAspect;
                bounds.origin.x += (bounds.size.width - width) / 2;
                bounds.size.width = width;
            } else {
                CGFloat height = bounds.size.width / sourceAspect;
                bounds.origin.y += (bounds.size.height - height) / 2;
                bounds.size.height = height;
            }
        }
        CGFloat px = bounds.origin.x + (CGFloat)x / 65535.0 * MAX(1, bounds.size.width - 1);
        CGFloat py = bounds.origin.y + (1.0 - (CGFloat)y / 65535.0) * MAX(1, bounds.size.height - 1);
        self.remoteCursorView.frameOrigin = NSMakePoint(px, py - self.remoteCursorView.frame.size.height);
        self.remoteCursorView.hidden = NO;
    });
}

- (void)beginAudioWithMessage:(NSDictionary *)message {
    self.audioPort = (uint16_t)[message[@"audioPort"] unsignedIntValue];
    NSUInteger sampleRate = [message[@"sampleRate"] unsignedIntegerValue];
    NSUInteger channels = [message[@"channels"] unsignedIntegerValue];
    NSUInteger packetSamples = [message[@"packetSamples"] unsignedIntegerValue];
    NSString *format = message[@"format"];
    if (![format isEqualToString:@"pcm_s16le"]) return;

    NSError *error = nil;
    if (![self.audioPlayer startWithSampleRate:sampleRate channels:channels
                                packetSamples:packetSamples error:&error]) {
        [self updateStreamStatusWithSuffix:@"audio output failed"];
        return;
    }
    self.audioReceived = NO;
    __weak typeof(self) weakSelf = self;
    self.audioSocket = [[DSUDPSocket alloc]
        initWithExpectedHost:self.serverHost expectedPort:self.audioPort queue:self.audioQueue
        handler:^(NSData *data, NSString *sourceHost, uint16_t sourcePort) {
            (void)sourceHost; (void)sourcePort;
            weakSelf.audioReceived = YES;
            [weakSelf.audioPlayer consumeAudioDatagram:data];
        } error:&error];
    if (!self.audioSocket || ![self.audioSocket bindToPort:0 receiveBufferSize:256 * 1024 error:&error] ||
        ![self.audioSocket start:&error]) {
        [self.audioPlayer stop];
        [self updateStreamStatusWithSuffix:@"audio socket failed"];
        return;
    }
    [self sendControl:@{@"type":@"AUDIO_READY", @"port":@(self.audioSocket.localPort)}];
    [self sendAudioHolePunch];
    [self installAudioPunchTimer];
}

- (void)sendMediaDatagram:(NSData *)datagram {
    if (!self.mediaSocket || self.mediaPort == 0) return;
    [self.mediaSocket sendData:datagram toHost:self.serverHost port:self.mediaPort error:nil];
}

- (void)sendMediaHolePunch {
    [self sendMediaDatagram:[@"DSMH" dataUsingEncoding:NSASCIIStringEncoding]];
}

- (void)sendAudioHolePunch {
    if (!self.audioSocket || self.audioPort == 0) return;
    [self.audioSocket sendData:[@"DSAH" dataUsingEncoding:NSASCIIStringEncoding]
                        toHost:self.serverHost port:self.audioPort error:nil];
}

- (void)installMediaPunchTimer {
    if (self.mediaPunchTimer) dispatch_source_cancel(self.mediaPunchTimer);
    self.mediaPunchTimer = dispatch_source_create(DISPATCH_SOURCE_TYPE_TIMER, 0, 0, self.mediaQueue);
    dispatch_source_set_timer(self.mediaPunchTimer, dispatch_time(DISPATCH_TIME_NOW, NSEC_PER_SEC),
                              NSEC_PER_SEC, 50 * NSEC_PER_MSEC);
    __weak typeof(self) weakSelf = self;
    dispatch_source_set_event_handler(self.mediaPunchTimer, ^{
        if (!weakSelf.mediaReceived) [weakSelf sendMediaHolePunch];
    });
    dispatch_resume(self.mediaPunchTimer);
}

- (void)installAudioPunchTimer {
    if (self.audioPunchTimer) dispatch_source_cancel(self.audioPunchTimer);
    self.audioPunchTimer = dispatch_source_create(DISPATCH_SOURCE_TYPE_TIMER, 0, 0, self.audioQueue);
    dispatch_source_set_timer(self.audioPunchTimer, dispatch_time(DISPATCH_TIME_NOW, NSEC_PER_SEC),
                              NSEC_PER_SEC, 50 * NSEC_PER_MSEC);
    __weak typeof(self) weakSelf = self;
    dispatch_source_set_event_handler(self.audioPunchTimer, ^{
        if (!weakSelf.audioReceived) [weakSelf sendAudioHolePunch];
    });
    dispatch_resume(self.audioPunchTimer);
}

- (void)installStatsTimer {
    if (self.statsTimer) dispatch_source_cancel(self.statsTimer);
    self.statsTimer = dispatch_source_create(DISPATCH_SOURCE_TYPE_TIMER, 0, 0, dispatch_get_main_queue());
    dispatch_source_set_timer(self.statsTimer, dispatch_time(DISPATCH_TIME_NOW, NSEC_PER_SEC),
                              NSEC_PER_SEC, 50 * NSEC_PER_MSEC);
    __weak typeof(self) weakSelf = self;
    dispatch_source_set_event_handler(self.statsTimer, ^{ [weakSelf flushStatsAndWatchdog]; });
    dispatch_resume(self.statsTimer);
}

- (void)flushStatsAndWatchdog {
    if (!self.streaming) return;
    NSUInteger frames, drops, bytes;
    @synchronized (self) {
        frames = self.intervalFrames; drops = self.intervalDrops; bytes = self.intervalBytes;
        self.intervalFrames = self.intervalDrops = self.intervalBytes = 0;
    }
    [self sendControl:@{@"type":@"STATS", @"framesOk":@(frames), @"framesDropped":@(drops),
                        @"bytes":@(bytes), @"intervalMs":@1000,
                        @"captureToReceiveP95Ms":@(-1), @"decodeToSurfaceP95Ms":@(-1)}];
    if (self.statsOverlayVisible) {
        self.statsOverlayLabel.stringValue = [NSString stringWithFormat:
            @"%lu fps · %lu dropped · %.2f Mbps received · %.1f Mbps target",
            (unsigned long)frames, (unsigned long)drops, bytes * 8.0 / 1000000.0,
            MAX(0, self.currentBitrate) / 1000.0];
    }
    NSTimeInterval silence = NSProcessInfo.processInfo.systemUptime - self.lastMediaAt;
    if (silence > 1.5) { [self sendMediaHolePunch]; [self requestIDR]; }
    if (silence > 6.0 && !self.restartRequested) {
        self.restartRequested = YES;
        [self stopSessionAndNotifyServer:YES];
        self.streamStatus.stringValue = @"Video stalled · rebuilding the stream…";
    }
    [self updateStreamStatusWithSuffix:nil];
}

- (void)requestIDR {
    [self.videoView requestKeyframeWhenRendererReady];
}

- (void)sendIDRRequestNow {
    NSTimeInterval now = NSProcessInfo.processInfo.systemUptime;
    @synchronized (self) {
        if (now - self.lastIDRAt < 0.3) return;
        self.lastIDRAt = now;
    }
    [self sendControl:@{@"type":@"REQUEST_IDR"}];
}

- (void)stopSessionAndNotifyServer:(BOOL)notify {
    BOOL wasStreaming = self.streaming || self.streamRequested;
    self.streaming = NO;
    self.inputController.enabled = NO;
    self.inputController.keyboardEnabled = NO;
    self.gamepadManager.enabled = NO;
    [self.audioPlayer stop];
    [self.mediaSocket stop]; self.mediaSocket = nil;
    [self.audioSocket stop]; self.audioSocket = nil;
    if (self.mediaPunchTimer) { dispatch_source_cancel(self.mediaPunchTimer); self.mediaPunchTimer = nil; }
    if (self.audioPunchTimer) { dispatch_source_cancel(self.audioPunchTimer); self.audioPunchTimer = nil; }
    if (self.statsTimer) { dispatch_source_cancel(self.statsTimer); self.statsTimer = nil; }
    [self.frameAssembler reset];
    [self.frameTraceCollector reset];
    [self.videoView resetRenderer];
    self.remoteCursorView.hidden = YES;
    self.mediaPort = self.audioPort = 0;
    self.mediaReceived = self.audioReceived = NO;
    if (notify && wasStreaming) {
        [self sendControl:@{@"type":@"INPUT_STOP"}];
        [self sendControl:@{@"type":@"GAMEPAD_STOP"}];
        [self sendControl:@{@"type":@"STOP_STREAM"}];
    }
    if (!self.restartRequested) self.streamRequested = NO;
}

- (void)sendControl:(NSDictionary<NSString *, id> *)message {
    [self.control sendMessage:message error:nil];
}

- (void)updateStreamStatusWithSuffix:(NSString * _Nullable)suffix {
    if (!self.streaming) return;
    NSString *base = [NSString stringWithFormat:@"Streaming %lu×%lu@60 · H.264 · %.1f Mbps",
        self.streamWidth, self.streamHeight, MAX(0, self.currentBitrate) / 1000.0];
    self.streamStatus.stringValue = suffix.length ? [base stringByAppendingFormat:@" · %@", suffix] : base;
}

#pragma mark - Quality selection

- (NSDictionary<NSString *, id> *)startStreamMessage {
    NSInteger maxBitrate = [self.quality isEqualToString:@"720p"]
        ? DS720pMaxBitrateKbps
        : DSNativeMaxBitrateKbps;
    return @{@"type":@"START_STREAM", @"maxBitrateKbps":@(maxBitrate),
             @"fps":@60, @"quality":self.quality};
}

- (NSString *)qualityDisplayName:(NSString *)quality {
    return [quality isEqualToString:@"720p"] ? @"720p" : @"Native";
}

- (void)syncQualityControls {
    [self.qualityPopup selectItemWithTitle:[self qualityDisplayName:self.quality]];
    self.qualityNativeMenuItem.state = [self.quality isEqualToString:@"native"] ? NSControlStateValueOn : NSControlStateValueOff;
    self.quality720pMenuItem.state = [self.quality isEqualToString:@"720p"] ? NSControlStateValueOn : NSControlStateValueOff;
}

- (void)applyQualitySelection:(NSString *)quality {
    if ([self.quality isEqualToString:quality]) return;
    self.quality = quality;
    [self syncQualityControls];
    // Reuse the existing restartRequested/STREAM_STOPPED restart mechanism (see
    // flushStatsAndWatchdog's stall-recovery path) instead of a new state machine: ask the
    // server to stop, and once it confirms with STREAM_STOPPED the handler above resends
    // START_STREAM with the new quality already included via -startStreamMessage.
    if (self.streaming) {
        self.restartRequested = YES;
        self.streamStatus.stringValue = [NSString stringWithFormat:@"Switching to %@…", [self qualityDisplayName:quality]];
        [self stopSessionAndNotifyServer:YES];
    }
}

- (void)qualityPopupChanged:(id)sender {
    (void)sender;
    [self applyQualitySelection:[self.qualityPopup.titleOfSelectedItem isEqualToString:@"720p"] ? @"720p" : @"native"];
}

- (void)qualityMenuItemSelected:(NSMenuItem *)sender {
    [self applyQualitySelection:[sender.representedObject isEqualToString:@"720p"] ? @"720p" : @"native"];
}

#pragma mark - Screenshot

- (NSString *)screenshotTimestampString {
    static NSDateFormatter *formatter;
    static dispatch_once_t once;
    dispatch_once(&once, ^{
        formatter = [[NSDateFormatter alloc] init];
        formatter.dateFormat = @"yyyyMMdd-HHmmss";
        formatter.locale = [NSLocale localeWithLocaleIdentifier:@"en_US_POSIX"];
    });
    return [formatter stringFromDate:[NSDate date]];
}

- (void)showTransientStreamMessage:(NSString *)message {
    self.streamStatus.stringValue = message;
    NSUInteger token = ++self.statusMessageToken;
    __weak typeof(self) weakSelf = self;
    dispatch_after(dispatch_time(DISPATCH_TIME_NOW, (int64_t)(2.5 * NSEC_PER_SEC)), dispatch_get_main_queue(), ^{
        typeof(self) strongSelf = weakSelf;
        if (!strongSelf || strongSelf.statusMessageToken != token) return;
        [strongSelf updateStreamStatusWithSuffix:nil];
    });
}

- (void)writeCGImage:(CGImageRef)image toURL:(NSURL *)url displayName:(NSString *)displayName {
    // Takes ownership of `image` (the retained result of -[DSVideoView copyCurrentFrameImage])
    // and releases it exactly once, after the background PNG encode completes.
    dispatch_async(dispatch_get_global_queue(QOS_CLASS_UTILITY, 0), ^{
        NSBitmapImageRep *rep = [[NSBitmapImageRep alloc] initWithCGImage:image];
        CGImageRelease(image);
        NSData *png = [rep representationUsingType:NSBitmapImageFileTypePNG properties:@{}];
        BOOL ok = png && [png writeToURL:url options:NSDataWritingAtomic error:nil];
        dispatch_async(dispatch_get_main_queue(), ^{
            [self showTransientStreamMessage:ok ? [NSString stringWithFormat:@"Saved %@", displayName] : @"Save failed"];
        });
    });
}

- (void)savePressed:(id)sender {
    (void)sender;
    if (!self.streaming) {
        NSBeep();
        [self showTransientStreamMessage:@"No frame yet"];
        return;
    }
    // macOS 14.4+ copies the renderer's displayed pixel directly. Older supported systems use a
    // one-shot fallback decoder and therefore need a self-contained IDR for this capture only.
    if ([self.videoView prepareScreenshotCapture]) [self requestIDR];
    [self attemptSaveFrameWithDeadline:NSProcessInfo.processInfo.systemUptime + 1.5 announced:NO];
}

- (void)attemptSaveFrameWithDeadline:(NSTimeInterval)deadline announced:(BOOL)announced {
    CGImageRef image = [self.videoView copyCurrentFrameImage];
    if (image) {
        [self writeScreenshotImage:image];
        return;
    }
    if (!self.streaming || NSProcessInfo.processInfo.systemUptime >= deadline) {
        [self.videoView cancelScreenshotCapture];
        NSBeep();
        [self showTransientStreamMessage:@"No frame to save yet"];
        return;
    }
    if (!announced) [self showTransientStreamMessage:@"Preparing screenshot…"];
    __weak typeof(self) weakSelf = self;
    dispatch_after(dispatch_time(DISPATCH_TIME_NOW, (int64_t)(0.2 * NSEC_PER_SEC)),
                   dispatch_get_main_queue(), ^{
        [weakSelf attemptSaveFrameWithDeadline:deadline announced:YES];
    });
}

- (void)writeScreenshotImage:(CGImageRef)image {
    NSString *filename = [NSString stringWithFormat:@"DeskStream-%@.png", [self screenshotTimestampString]];
    NSFileManager *fileManager = NSFileManager.defaultManager;
    NSURL *picturesURL = [fileManager URLForDirectory:NSPicturesDirectory inDomain:NSUserDomainMask
                                     appropriateForURL:nil create:YES error:nil];
    if (picturesURL && [fileManager isWritableFileAtPath:picturesURL.path]) {
        [self writeCGImage:image toURL:[picturesURL URLByAppendingPathComponent:filename] displayName:filename];
        return;
    }
    NSSavePanel *panel = NSSavePanel.savePanel;
    panel.nameFieldStringValue = filename;
    panel.allowedContentTypes = @[UTTypePNG];
    __weak typeof(self) weakSelf = self;
    [panel beginSheetModalForWindow:self.window completionHandler:^(NSModalResponse response) {
        if (response == NSModalResponseOK && panel.URL) {
            [weakSelf writeCGImage:image toURL:panel.URL displayName:panel.URL.lastPathComponent];
        } else {
            CGImageRelease(image);
        }
    }];
}

#pragma mark - Actions/lifecycle

- (void)capturePressed:(id)sender {
    (void)sender;
    [self.inputController setPointerCaptured:!self.inputController.pointerCaptured];
}

- (void)mutePressed:(id)sender {
    (void)sender;
    self.audioPlayer.muted = !self.audioPlayer.muted;
    self.muteButton.title = self.audioPlayer.muted ? @"Unmute" : @"Mute";
    self.muteMenuItem.title = self.audioPlayer.muted ? @"Unmute" : @"Mute";
}

- (void)statsPressed:(id)sender {
    (void)sender;
    self.statsOverlayVisible = !self.statsOverlayVisible;
    self.statsOverlayLabel.hidden = !self.statsOverlayVisible;
    self.statsButton.title = self.statsOverlayVisible ? @"Hide Stats" : @"Stats";
    self.statsMenuItem.state = self.statsOverlayVisible ? NSControlStateValueOn : NSControlStateValueOff;
}

- (void)fullscreenPressed:(id)sender { (void)sender; [self.window toggleFullScreen:nil]; }

- (void)disconnectPressed:(id)sender {
    (void)sender;
    self.wantsConnection = NO;
    self.restartRequested = NO;
    [self stopSessionAndNotifyServer:YES];
    [self.control disconnect];
    self.control = nil;
    self.connectButton.enabled = YES;
    self.connectionStatus.stringValue = @"Disconnected";
    [self showConnectUI];
    [self startDiscovery];
}

- (void)systemWillSleep:(NSNotification *)notification {
    (void)notification;
    [self stopSessionAndNotifyServer:YES];
}

- (void)systemDidWake:(NSNotification *)notification {
    (void)notification;
    if (self.wantsConnection && self.control.state == DSControlStateConnected && !self.streamRequested) {
        self.streamRequested = YES;
        [self sendControl:[self startStreamMessage]];
    }
}

@end
