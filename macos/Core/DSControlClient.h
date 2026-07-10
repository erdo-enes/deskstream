#import <Foundation/Foundation.h>

NS_ASSUME_NONNULL_BEGIN

typedef NS_ENUM(NSInteger, DSControlState) {
    DSControlStateDisconnected,
    DSControlStateConnecting,
    DSControlStateConnected,
    DSControlStateReconnecting,
};

typedef void (^DSControlMessageHandler)(NSDictionary<NSString *, id> *message);
typedef void (^DSControlStateHandler)(DSControlState state);
typedef void (^DSControlErrorHandler)(NSError *error);
typedef void (^DSReconnectHandler)(NSUInteger attempt, NSTimeInterval delay);

/// DeskStream's TCP control transport: UInt32-BE framed UTF-8 JSON, TCP_NODELAY, FIFO
/// writes, two-second PING, six-second silence watchdog, and bounded reconnect backoff.
@interface DSControlClient : NSObject

- (instancetype)init NS_UNAVAILABLE;
- (instancetype)initWithHost:(NSString *)host
                         port:(uint16_t)port
                callbackQueue:(dispatch_queue_t)callbackQueue
               messageHandler:(DSControlMessageHandler)messageHandler
                 stateHandler:(DSControlStateHandler)stateHandler
                 errorHandler:(DSControlErrorHandler)errorHandler NS_DESIGNATED_INITIALIZER;

- (void)connect;
- (void)disconnect;

/// JSON serialization happens before enqueueing. All successfully enqueued messages are
/// written in call order on the private control queue.
- (BOOL)sendMessage:(NSDictionary<NSString *, id> *)message error:(NSError **)error;

@property(nonatomic, readonly) DSControlState state;
@property(nonatomic) BOOL automaticallyReconnects;
@property(nonatomic, copy, nullable) DSReconnectHandler reconnectHandler;
@property(nonatomic, copy, readonly) NSString *host;
@property(nonatomic, readonly) uint16_t port;

@end

NS_ASSUME_NONNULL_END
