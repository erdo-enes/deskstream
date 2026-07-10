#import "DSDiscoveryService.h"

#import "DSUDPSocket.h"

#import <arpa/inet.h>
#import <ifaddrs.h>
#import <net/if.h>

static const uint16_t DSDiscoveryPort = 47800;

@implementation DSDiscoveredServer

- (instancetype)initWithName:(NSString *)name host:(NSString *)host controlPort:(uint16_t)controlPort {
    self = [super init];
    if (self) {
        _name = [name copy];
        _host = [host copy];
        _controlPort = controlPort;
    }
    return self;
}

- (NSString *)description {
    return [NSString stringWithFormat:@"<DSDiscoveredServer %@ %@:%u>", _name, _host, _controlPort];
}

@end

@implementation DSDiscoveryService {
    dispatch_queue_t _queue;
    dispatch_queue_t _callbackQueue;
    DSDiscoveryHandler _handler;
    DSUDPSocket *_socket;
    dispatch_source_t _probeTimer;
    NSMutableDictionary<NSString *, DSDiscoveredServer *> *_servers;
}

- (instancetype)initWithCallbackQueue:(dispatch_queue_t)callbackQueue
                               handler:(DSDiscoveryHandler)handler {
    NSParameterAssert(callbackQueue != nil);
    NSParameterAssert(handler != nil);
    self = [super init];
    if (self) {
        _queue = dispatch_queue_create("com.deskstream.macos.discovery", DISPATCH_QUEUE_SERIAL);
        _callbackQueue = callbackQueue;
        _handler = [handler copy];
        _servers = [NSMutableDictionary dictionary];
    }
    return self;
}

- (BOOL)start:(NSError **)error {
    if (_socket != nil) return YES;

    __weak typeof(self) weakSelf = self;
    DSUDPSocket *socket = [[DSUDPSocket alloc] initWithExpectedHost:nil
                                                      expectedPort:0
                                                             queue:_queue
                                                           handler:^(NSData *data, NSString *sourceHost, uint16_t sourcePort) {
        (void)sourcePort;
        [weakSelf receivedReply:data sourceHost:sourceHost];
    }
                                                             error:error];
    if (socket == nil ||
        ![socket bindToPort:0 receiveBufferSize:64 * 1024 error:error] ||
        ![socket setBroadcastEnabled:YES error:error] ||
        ![socket start:error]) {
        [socket stop];
        return NO;
    }
    _socket = socket;

    _probeTimer = dispatch_source_create(DISPATCH_SOURCE_TYPE_TIMER, 0, 0, _queue);
    dispatch_source_set_timer(_probeTimer,
                              dispatch_time(DISPATCH_TIME_NOW, 0),
                              NSEC_PER_SEC,
                              100 * NSEC_PER_MSEC);
    dispatch_source_set_event_handler(_probeTimer, ^{
        [weakSelf sendProbe];
    });
    dispatch_resume(_probeTimer);
    return YES;
}

- (void)stop {
    if (_probeTimer != nil) {
        dispatch_source_cancel(_probeTimer);
        _probeTimer = nil;
    }
    [_socket stop];
    _socket = nil;
}

- (void)probeNow {
    dispatch_async(_queue, ^{ [self sendProbe]; });
}

- (void)sendProbe {
    DSUDPSocket *socket = _socket;
    if (socket == nil) return;
    NSData *probe = [@"DSPROBE1" dataUsingEncoding:NSASCIIStringEncoding];

    NSMutableSet<NSString *> *targets = [NSMutableSet setWithObject:@"255.255.255.255"];
    struct ifaddrs *interfaces = NULL;
    if (getifaddrs(&interfaces) == 0) {
        for (struct ifaddrs *cursor = interfaces; cursor != NULL; cursor = cursor->ifa_next) {
            if (cursor->ifa_addr == NULL || cursor->ifa_addr->sa_family != AF_INET) continue;
            if ((cursor->ifa_flags & IFF_UP) == 0 ||
                (cursor->ifa_flags & IFF_LOOPBACK) != 0 ||
                (cursor->ifa_flags & IFF_BROADCAST) == 0 ||
                cursor->ifa_broadaddr == NULL) continue;

            const struct sockaddr_in *broadcast = (const struct sockaddr_in *)cursor->ifa_broadaddr;
            char address[INET_ADDRSTRLEN] = {0};
            if (inet_ntop(AF_INET, &broadcast->sin_addr, address, sizeof(address)) != NULL) {
                [targets addObject:[NSString stringWithUTF8String:address]];
            }
        }
        freeifaddrs(interfaces);
    }

    for (NSString *target in targets) {
        [socket sendData:probe toHost:target port:DSDiscoveryPort error:NULL];
    }
}

- (void)receivedReply:(NSData *)data sourceHost:(NSString *)sourceHost {
    DSDiscoveredServer *server = [DSDiscoveryService serverFromReplyData:data sourceHost:sourceHost];
    if (server == nil) return;
    NSString *key = [NSString stringWithFormat:@"%@:%u", server.host, server.controlPort];
    DSDiscoveredServer *old = _servers[key];
    if (old != nil && [old.name isEqualToString:server.name]) return;
    _servers[key] = server;
    dispatch_async(_callbackQueue, ^{ self->_handler(server); });
}

+ (DSDiscoveredServer *)serverFromReplyData:(NSData *)data sourceHost:(NSString *)sourceHost {
    if (data.length == 0 || sourceHost.length == 0) return nil;
    id object = [NSJSONSerialization JSONObjectWithData:data options:0 error:NULL];
    if (![object isKindOfClass:NSDictionary.class]) return nil;
    NSDictionary *json = object;
    NSNumber *version = [json[@"ver"] isKindOfClass:NSNumber.class] ? json[@"ver"] : nil;
    NSNumber *portNumber = [json[@"controlPort"] isKindOfClass:NSNumber.class]
        ? json[@"controlPort"] : nil;
    if (![json[@"type"] isEqual:@"DSREPLY"] || version.integerValue != 1 || portNumber == nil) return nil;
    NSInteger port = portNumber.integerValue;
    if (port < 1 || port > UINT16_MAX) return nil;
    NSString *name = [json[@"name"] isKindOfClass:NSString.class] ? json[@"name"] : nil;
    if (name.length == 0) name = sourceHost;
    return [[DSDiscoveredServer alloc] initWithName:name
                                               host:sourceHost
                                        controlPort:(uint16_t)port];
}

+ (DSDiscoveredServer *)manualServerWithHost:(NSString *)host
                                         port:(uint16_t)port
                                         name:(NSString *)name {
    NSString *trimmed = [host stringByTrimmingCharactersInSet:NSCharacterSet.whitespaceAndNewlineCharacterSet];
    if (trimmed.length == 0 || port == 0) return nil;
    NSString *displayName = name.length > 0 ? name : trimmed;
    return [[DSDiscoveredServer alloc] initWithName:displayName host:trimmed controlPort:port];
}

- (void)dealloc {
    [self stop];
}

@end
