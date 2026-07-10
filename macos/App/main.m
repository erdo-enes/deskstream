#import <AppKit/AppKit.h>
#import "DSAppDelegate.h"

int main(int argc, const char *argv[]) {
    (void)argc; (void)argv;
    @autoreleasepool {
        NSApplication *application = NSApplication.sharedApplication;
        DSAppDelegate *delegate = [[DSAppDelegate alloc] init];
        application.delegate = delegate;
        [application setActivationPolicy:NSApplicationActivationPolicyRegular];
        [application run];
    }
    return 0;
}
