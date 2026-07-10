#import <Foundation/Foundation.h>

NS_ASSUME_NONNULL_BEGIN

FOUNDATION_EXPORT NSString * const DSNetworkErrorDomain;

typedef NS_ERROR_ENUM(DSNetworkErrorDomain, DSNetworkError) {
    DSNetworkErrorInvalidAddress = 1,
    DSNetworkErrorSocketFailure = 2,
    DSNetworkErrorNotBound = 3,
    DSNetworkErrorAlreadyStarted = 4,
};

typedef void (^DSDatagramHandler)(NSData *data, NSString *sourceHost, uint16_t sourcePort);

/// A one-shot bound IPv4 UDP socket with optional source endpoint validation.
/// Receive callbacks run on the queue supplied at initialization.
@interface DSUDPSocket : NSObject

- (instancetype)init NS_UNAVAILABLE;
- (nullable instancetype)initWithExpectedHost:(nullable NSString *)expectedHost
                                  expectedPort:(uint16_t)expectedPort
                                         queue:(dispatch_queue_t)queue
                                       handler:(DSDatagramHandler)handler
                                         error:(NSError **)error NS_DESIGNATED_INITIALIZER;

- (BOOL)bindToPort:(uint16_t)port receiveBufferSize:(int)receiveBufferSize error:(NSError **)error;
- (BOOL)setBroadcastEnabled:(BOOL)enabled error:(NSError **)error;
- (BOOL)start:(NSError **)error;
- (void)stop;

- (BOOL)sendData:(NSData *)data
           toHost:(NSString *)host
             port:(uint16_t)port
            error:(NSError **)error;

@property(nonatomic, readonly) uint16_t localPort;
@property(nonatomic, readonly, getter=isRunning) BOOL running;

@end

NS_ASSUME_NONNULL_END
