#import "DSUDPSocket.h"

#import <arpa/inet.h>
#import <errno.h>
#import <fcntl.h>
#import <netdb.h>
#import <sys/socket.h>
#import <unistd.h>

NSString * const DSNetworkErrorDomain = @"com.deskstream.macos.network";

static NSError *DSErrnoError(NSString *operation) {
    int code = errno;
    NSString *message = [NSString stringWithFormat:@"%@: %s", operation, strerror(code)];
    return [NSError errorWithDomain:NSPOSIXErrorDomain
                               code:code
                           userInfo:@{NSLocalizedDescriptionKey: message}];
}

static BOOL DSResolveIPv4(NSString *host, uint16_t port, struct sockaddr_in *result, NSError **error) {
    if (host.length == 0 || result == NULL || port == 0) {
        if (error != NULL) {
            *error = [NSError errorWithDomain:DSNetworkErrorDomain
                                         code:DSNetworkErrorInvalidAddress
                                     userInfo:@{NSLocalizedDescriptionKey: @"A valid IPv4 host and port are required"}];
        }
        return NO;
    }

    struct addrinfo hints = {0};
    hints.ai_family = AF_INET;
    hints.ai_socktype = SOCK_DGRAM;
    hints.ai_protocol = IPPROTO_UDP;
    struct addrinfo *addresses = NULL;
    NSString *service = [NSString stringWithFormat:@"%u", port];
    int status = getaddrinfo(host.UTF8String, service.UTF8String, &hints, &addresses);
    if (status != 0 || addresses == NULL) {
        if (error != NULL) {
            NSString *description = [NSString stringWithFormat:@"Could not resolve %@: %s",
                                     host, gai_strerror(status)];
            *error = [NSError errorWithDomain:DSNetworkErrorDomain
                                         code:DSNetworkErrorInvalidAddress
                                     userInfo:@{NSLocalizedDescriptionKey: description}];
        }
        if (addresses != NULL) freeaddrinfo(addresses);
        return NO;
    }
    memcpy(result, addresses->ai_addr, sizeof(*result));
    freeaddrinfo(addresses);
    return YES;
}

@implementation DSUDPSocket {
    dispatch_queue_t _queue;
    DSDatagramHandler _handler;
    dispatch_source_t _readSource;
    int _fileDescriptor;
    struct sockaddr_in _expectedAddress;
    BOOL _validatesHost;
    uint16_t _expectedPort;
    uint16_t _localPort;
    BOOL _running;
}

- (instancetype)initWithExpectedHost:(NSString *)expectedHost
                         expectedPort:(uint16_t)expectedPort
                                queue:(dispatch_queue_t)queue
                              handler:(DSDatagramHandler)handler
                                error:(NSError **)error {
    NSParameterAssert(queue != nil);
    NSParameterAssert(handler != nil);
    self = [super init];
    if (self) {
        _queue = queue;
        _handler = [handler copy];
        _fileDescriptor = -1;
        _expectedPort = expectedPort;
        if (expectedHost.length > 0) {
            uint16_t resolutionPort = expectedPort == 0 ? 1 : expectedPort;
            if (!DSResolveIPv4(expectedHost, resolutionPort, &_expectedAddress, error)) {
                return nil;
            }
            _validatesHost = YES;
        }
    }
    return self;
}

- (uint16_t)localPort { return _localPort; }
- (BOOL)isRunning { return _running; }

- (BOOL)bindToPort:(uint16_t)port receiveBufferSize:(int)receiveBufferSize error:(NSError **)error {
    @synchronized (self) {
        if (_fileDescriptor >= 0) {
            if (error != NULL) {
                *error = [NSError errorWithDomain:DSNetworkErrorDomain
                                             code:DSNetworkErrorSocketFailure
                                         userInfo:@{NSLocalizedDescriptionKey: @"UDP socket is already bound"}];
            }
            return NO;
        }

        int descriptor = socket(AF_INET, SOCK_DGRAM, IPPROTO_UDP);
        if (descriptor < 0) {
            if (error != NULL) *error = DSErrnoError(@"socket");
            return NO;
        }
        int enabled = 1;
        (void)setsockopt(descriptor, SOL_SOCKET, SO_REUSEADDR, &enabled, sizeof(enabled));
        if (receiveBufferSize > 0) {
            (void)setsockopt(descriptor, SOL_SOCKET, SO_RCVBUF,
                             &receiveBufferSize, sizeof(receiveBufferSize));
        }
        int flags = fcntl(descriptor, F_GETFL, 0);
        if (flags >= 0) (void)fcntl(descriptor, F_SETFL, flags | O_NONBLOCK);

        struct sockaddr_in address = {0};
        address.sin_len = sizeof(address);
        address.sin_family = AF_INET;
        address.sin_port = htons(port);
        address.sin_addr.s_addr = htonl(INADDR_ANY);
        if (bind(descriptor, (const struct sockaddr *)&address, sizeof(address)) != 0) {
            if (error != NULL) *error = DSErrnoError(@"bind");
            close(descriptor);
            return NO;
        }

        socklen_t addressLength = sizeof(address);
        if (getsockname(descriptor, (struct sockaddr *)&address, &addressLength) != 0) {
            if (error != NULL) *error = DSErrnoError(@"getsockname");
            close(descriptor);
            return NO;
        }
        _fileDescriptor = descriptor;
        _localPort = ntohs(address.sin_port);
        return YES;
    }
}

- (BOOL)setBroadcastEnabled:(BOOL)enabled error:(NSError **)error {
    @synchronized (self) {
        if (_fileDescriptor < 0) {
            if (error != NULL) {
                *error = [NSError errorWithDomain:DSNetworkErrorDomain
                                             code:DSNetworkErrorNotBound
                                         userInfo:@{NSLocalizedDescriptionKey: @"UDP socket is not bound"}];
            }
            return NO;
        }
        int value = enabled ? 1 : 0;
        if (setsockopt(_fileDescriptor, SOL_SOCKET, SO_BROADCAST, &value, sizeof(value)) != 0) {
            if (error != NULL) *error = DSErrnoError(@"setsockopt(SO_BROADCAST)");
            return NO;
        }
        return YES;
    }
}

- (BOOL)start:(NSError **)error {
    @synchronized (self) {
        if (_running) {
            if (error != NULL) {
                *error = [NSError errorWithDomain:DSNetworkErrorDomain
                                             code:DSNetworkErrorAlreadyStarted
                                         userInfo:@{NSLocalizedDescriptionKey: @"UDP receiver is already running"}];
            }
            return NO;
        }
        if (_fileDescriptor < 0) {
            if (error != NULL) {
                *error = [NSError errorWithDomain:DSNetworkErrorDomain
                                             code:DSNetworkErrorNotBound
                                         userInfo:@{NSLocalizedDescriptionKey: @"UDP socket is not bound"}];
            }
            return NO;
        }

        _running = YES;
        int descriptor = _fileDescriptor;
        _readSource = dispatch_source_create(DISPATCH_SOURCE_TYPE_READ,
                                             (uintptr_t)descriptor,
                                             0,
                                             _queue);
        __weak typeof(self) weakSelf = self;
        dispatch_source_set_event_handler(_readSource, ^{
            [weakSelf receiveAvailableDatagrams];
        });
        dispatch_resume(_readSource);
        return YES;
    }
}

- (void)receiveAvailableDatagrams {
    uint8_t bytes[65535];
    while (_running) {
        struct sockaddr_in source = {0};
        socklen_t sourceLength = sizeof(source);
        ssize_t count = recvfrom(_fileDescriptor,
                                 bytes,
                                 sizeof(bytes),
                                 MSG_DONTWAIT,
                                 (struct sockaddr *)&source,
                                 &sourceLength);
        if (count < 0) {
            if (errno == EAGAIN || errno == EWOULDBLOCK || errno == EINTR) return;
            return;
        }
        if (source.sin_family != AF_INET) continue;
        uint16_t sourcePort = ntohs(source.sin_port);
        if (_validatesHost && source.sin_addr.s_addr != _expectedAddress.sin_addr.s_addr) continue;
        if (_expectedPort != 0 && sourcePort != _expectedPort) continue;

        char hostBuffer[INET_ADDRSTRLEN] = {0};
        if (inet_ntop(AF_INET, &source.sin_addr, hostBuffer, sizeof(hostBuffer)) == NULL) continue;
        NSData *data = [NSData dataWithBytes:bytes length:(NSUInteger)count];
        _handler(data, [NSString stringWithUTF8String:hostBuffer], sourcePort);
    }
}

- (BOOL)sendData:(NSData *)data
           toHost:(NSString *)host
             port:(uint16_t)port
            error:(NSError **)error {
    struct sockaddr_in destination;
    if (!DSResolveIPv4(host, port, &destination, error)) return NO;

    int descriptor;
    @synchronized (self) {
        descriptor = _fileDescriptor;
    }
    if (descriptor < 0) {
        if (error != NULL) {
            *error = [NSError errorWithDomain:DSNetworkErrorDomain
                                         code:DSNetworkErrorNotBound
                                     userInfo:@{NSLocalizedDescriptionKey: @"UDP socket is not bound"}];
        }
        return NO;
    }

    ssize_t sent = sendto(descriptor,
                          data.bytes,
                          data.length,
                          0,
                          (const struct sockaddr *)&destination,
                          sizeof(destination));
    if (sent < 0 || (NSUInteger)sent != data.length) {
        if (error != NULL) *error = DSErrnoError(@"sendto");
        return NO;
    }
    return YES;
}

- (void)stop {
    @synchronized (self) {
        if (!_running && _fileDescriptor < 0) return;
        _running = NO;
        if (_readSource != nil) {
            dispatch_source_cancel(_readSource);
            _readSource = nil;
        }
        if (_fileDescriptor >= 0) {
            close(_fileDescriptor);
            _fileDescriptor = -1;
        }
        _localPort = 0;
    }
}

- (void)dealloc {
    [self stop];
}

@end
