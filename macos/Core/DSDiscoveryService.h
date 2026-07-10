#import <Foundation/Foundation.h>

NS_ASSUME_NONNULL_BEGIN

@interface DSDiscoveredServer : NSObject

- (instancetype)init NS_UNAVAILABLE;
- (instancetype)initWithName:(NSString *)name
                         host:(NSString *)host
                  controlPort:(uint16_t)controlPort NS_DESIGNATED_INITIALIZER;

@property(nonatomic, copy, readonly) NSString *name;
@property(nonatomic, copy, readonly) NSString *host;
@property(nonatomic, readonly) uint16_t controlPort;

@end

typedef void (^DSDiscoveryHandler)(DSDiscoveredServer *server);

/// Broadcast discovery for the exact DSPROBE1/DSREPLY v1 exchange. Manual hosts use the
/// same DSDiscoveredServer model and do not depend on broadcast being available.
@interface DSDiscoveryService : NSObject

- (instancetype)init NS_UNAVAILABLE;
- (instancetype)initWithCallbackQueue:(dispatch_queue_t)callbackQueue
                               handler:(DSDiscoveryHandler)handler NS_DESIGNATED_INITIALIZER;

- (BOOL)start:(NSError **)error;
- (void)stop;
- (void)probeNow;

+ (nullable DSDiscoveredServer *)serverFromReplyData:(NSData *)data
                                           sourceHost:(NSString *)sourceHost;
+ (nullable DSDiscoveredServer *)manualServerWithHost:(NSString *)host
                                                  port:(uint16_t)port
                                                  name:(nullable NSString *)name;

@end

NS_ASSUME_NONNULL_END
