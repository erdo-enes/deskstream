#import <Foundation/Foundation.h>

NS_ASSUME_NONNULL_BEGIN

typedef void (^DSGamepadDatagramBlock)(NSData *datagram);
typedef void (^DSGamepadControlBlock)(NSDictionary<NSString *, id> *message);

/// Maps up to four GameController.framework devices to DeskStream/XInput snapshots.
@interface DSGamepadManager : NSObject

@property (atomic, getter=isEnabled) BOOL enabled;
@property (atomic, readonly) NSUInteger controllerCount;
@property (nonatomic, copy, nullable) void (^inventoryChangedHandler)(NSArray<NSString *> *names);

- (instancetype)initWithDatagramSender:(DSGamepadDatagramBlock)sendDatagram
                          controlSender:(DSGamepadControlBlock)sendControl;
- (void)refreshControllers;
- (void)handleRumbleForController:(NSUInteger)controllerID
                       largeMotor:(uint8_t)largeMotor
                       smallMotor:(uint8_t)smallMotor;
- (void)stop;

@end

NS_ASSUME_NONNULL_END
