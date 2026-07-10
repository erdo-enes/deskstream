#import "DSControlClient.h"

#import "DSProtocol.h"
#import "DSUDPSocket.h"

#import <errno.h>
#import <fcntl.h>
#import <netdb.h>
#import <netinet/tcp.h>
#import <poll.h>
#import <sys/socket.h>
#import <time.h>
#import <unistd.h>

static const NSUInteger DSMaximumControlFrame = 65536;
static const NSTimeInterval DSConnectTimeoutSeconds = 5.0;
static const uint64_t DSPingIntervalNanoseconds = 2ull * NSEC_PER_SEC;
static const uint64_t DSSilenceTimeoutNanoseconds = 6ull * NSEC_PER_SEC;

static uint64_t DSMonotonicNanoseconds(void) {
    struct timespec time = {0};
    clock_gettime(CLOCK_MONOTONIC, &time);
    return (uint64_t)time.tv_sec * NSEC_PER_SEC + (uint64_t)time.tv_nsec;
}

static int socketConnect(int descriptor, const struct sockaddr *address, socklen_t length);

static NSError *DSControlPOSIXError(NSString *operation, int code) {
    NSString *description = [NSString stringWithFormat:@"%@: %s", operation, strerror(code)];
    return [NSError errorWithDomain:NSPOSIXErrorDomain
                               code:code
                           userInfo:@{NSLocalizedDescriptionKey: description}];
}

@implementation DSControlClient {
    dispatch_queue_t _ioQueue;
    dispatch_queue_t _callbackQueue;
    DSControlMessageHandler _messageHandler;
    DSControlStateHandler _stateHandler;
    DSControlErrorHandler _errorHandler;
    dispatch_source_t _readSource;
    dispatch_source_t _keepaliveTimer;
    NSMutableData *_receiveBuffer;
    int _fileDescriptor;
    NSUInteger _generation;
    BOOL _explicitlyDisconnected;
    NSTimeInterval _nextBackoff;
    NSUInteger _reconnectAttempt;
    uint64_t _lastReceivedNanoseconds;
    uint64_t _lastPingNanoseconds;
    DSControlState _state;
}

- (instancetype)initWithHost:(NSString *)host
                         port:(uint16_t)port
                callbackQueue:(dispatch_queue_t)callbackQueue
               messageHandler:(DSControlMessageHandler)messageHandler
                 stateHandler:(DSControlStateHandler)stateHandler
                 errorHandler:(DSControlErrorHandler)errorHandler {
    NSParameterAssert(host.length > 0);
    NSParameterAssert(port > 0);
    NSParameterAssert(callbackQueue != nil);
    NSParameterAssert(messageHandler != nil);
    NSParameterAssert(stateHandler != nil);
    NSParameterAssert(errorHandler != nil);
    self = [super init];
    if (self) {
        _host = [host copy];
        _port = port;
        _callbackQueue = callbackQueue;
        _messageHandler = [messageHandler copy];
        _stateHandler = [stateHandler copy];
        _errorHandler = [errorHandler copy];
        _ioQueue = dispatch_queue_create("com.deskstream.macos.control", DISPATCH_QUEUE_SERIAL);
        _receiveBuffer = [NSMutableData data];
        _fileDescriptor = -1;
        _state = DSControlStateDisconnected;
        _automaticallyReconnects = YES;
        _nextBackoff = 0.5;
    }
    return self;
}

- (DSControlState)state {
    @synchronized (self) { return _state; }
}

- (void)connect {
    dispatch_async(_ioQueue, ^{
        self->_explicitlyDisconnected = NO;
        self->_nextBackoff = 0.5;
        self->_reconnectAttempt = 0;
        [self closeSocketLocked];
        [self transitionLocked:DSControlStateConnecting];
        [self openSocketLocked];
    });
}

- (void)disconnect {
    dispatch_async(_ioQueue, ^{
        self->_explicitlyDisconnected = YES;
        [self closeSocketLocked];
        [self transitionLocked:DSControlStateDisconnected];
    });
}

- (BOOL)sendMessage:(NSDictionary<NSString *,id> *)message error:(NSError **)error {
    if (![NSJSONSerialization isValidJSONObject:message]) {
        if (error != NULL) {
            *error = [NSError errorWithDomain:DSProtocolErrorDomain
                                         code:DSProtocolErrorInvalidArgument
                                     userInfo:@{NSLocalizedDescriptionKey: @"Control message is not valid JSON"}];
        }
        return NO;
    }
    NSData *json = [NSJSONSerialization dataWithJSONObject:message options:0 error:error];
    if (json == nil) return NO;
    if (json.length == 0 || json.length > DSMaximumControlFrame) {
        if (error != NULL) {
            *error = [NSError errorWithDomain:DSProtocolErrorDomain
                                         code:DSProtocolErrorInvalidArgument
                                     userInfo:@{NSLocalizedDescriptionKey: @"Control message exceeds the 65,536-byte limit"}];
        }
        return NO;
    }

    NSMutableData *frame = [NSMutableData dataWithLength:4 + json.length];
    DSWriteUInt32BigEndian(frame.mutableBytes, (uint32_t)json.length);
    memcpy((uint8_t *)frame.mutableBytes + 4, json.bytes, json.length);
    dispatch_async(_ioQueue, ^{
        if (self->_fileDescriptor >= 0) [self sendFrameLocked:frame];
    });
    return YES;
}

- (void)openSocketLocked {
    struct addrinfo hints = {0};
    hints.ai_family = AF_INET;
    hints.ai_socktype = SOCK_STREAM;
    hints.ai_protocol = IPPROTO_TCP;
    struct addrinfo *addresses = NULL;
    NSString *service = [NSString stringWithFormat:@"%u", _port];
    int resolutionStatus = getaddrinfo(_host.UTF8String, service.UTF8String, &hints, &addresses);
    if (resolutionStatus != 0 || addresses == NULL) {
        NSString *description = [NSString stringWithFormat:@"Could not resolve %@: %s",
                                 _host, gai_strerror(resolutionStatus)];
        NSError *error = [NSError errorWithDomain:DSNetworkErrorDomain
                                             code:DSNetworkErrorInvalidAddress
                                         userInfo:@{NSLocalizedDescriptionKey: description}];
        if (addresses != NULL) freeaddrinfo(addresses);
        [self connectionFailedLocked:error];
        return;
    }

    int connectedDescriptor = -1;
    int lastError = ECONNREFUSED;
    for (struct addrinfo *address = addresses; address != NULL; address = address->ai_next) {
        int descriptor = socket(address->ai_family, address->ai_socktype, address->ai_protocol);
        if (descriptor < 0) {
            lastError = errno;
            continue;
        }
        int enabled = 1;
        (void)setsockopt(descriptor, IPPROTO_TCP, TCP_NODELAY, &enabled, sizeof(enabled));
        (void)setsockopt(descriptor, SOL_SOCKET, SO_NOSIGPIPE, &enabled, sizeof(enabled));

        int originalFlags = fcntl(descriptor, F_GETFL, 0);
        if (originalFlags >= 0) (void)fcntl(descriptor, F_SETFL, originalFlags | O_NONBLOCK);
        int result = socketConnect(descriptor, address->ai_addr, address->ai_addrlen);
        if (result != 0 && errno == EINPROGRESS) {
            struct pollfd pollDescriptor = {.fd = descriptor, .events = POLLOUT};
            result = poll(&pollDescriptor, 1, (int)(DSConnectTimeoutSeconds * 1000));
            if (result > 0) {
                socklen_t errorLength = sizeof(lastError);
                if (getsockopt(descriptor, SOL_SOCKET, SO_ERROR, &lastError, &errorLength) != 0) {
                    lastError = errno;
                }
                result = lastError == 0 ? 0 : -1;
            } else {
                lastError = result == 0 ? ETIMEDOUT : errno;
                result = -1;
            }
        } else if (result != 0) {
            lastError = errno;
        }

        if (result == 0) {
            if (originalFlags >= 0) (void)fcntl(descriptor, F_SETFL, originalFlags & ~O_NONBLOCK);
            struct timeval sendTimeout = {.tv_sec = 2, .tv_usec = 0};
            (void)setsockopt(descriptor, SOL_SOCKET, SO_SNDTIMEO, &sendTimeout, sizeof(sendTimeout));
            connectedDescriptor = descriptor;
            break;
        }
        close(descriptor);
    }
    freeaddrinfo(addresses);

    if (connectedDescriptor < 0) {
        [self connectionFailedLocked:DSControlPOSIXError(@"connect", lastError)];
        return;
    }

    _fileDescriptor = connectedDescriptor;
    _generation++;
    [_receiveBuffer setLength:0];
    _lastReceivedNanoseconds = DSMonotonicNanoseconds();
    _lastPingNanoseconds = _lastReceivedNanoseconds;
    _nextBackoff = 0.5;
    _reconnectAttempt = 0;
    [self installSourcesLockedForGeneration:_generation];
    [self transitionLocked:DSControlStateConnected];
}

// Kept as a function-like method boundary so tests and static analysis can distinguish the
// system connect call from the public -connect selector.
static int socketConnect(int descriptor, const struct sockaddr *address, socklen_t length) {
    return connect(descriptor, address, length);
}

- (void)installSourcesLockedForGeneration:(NSUInteger)generation {
    int descriptor = _fileDescriptor;
    __weak typeof(self) weakSelf = self;
    _readSource = dispatch_source_create(DISPATCH_SOURCE_TYPE_READ,
                                         (uintptr_t)descriptor,
                                         0,
                                         _ioQueue);
    dispatch_source_set_event_handler(_readSource, ^{
        typeof(self) strongSelf = weakSelf;
        if (strongSelf == nil || generation != strongSelf->_generation) return;
        [strongSelf readAvailableLocked];
    });
    dispatch_resume(_readSource);

    _keepaliveTimer = dispatch_source_create(DISPATCH_SOURCE_TYPE_TIMER, 0, 0, _ioQueue);
    dispatch_source_set_timer(_keepaliveTimer,
                              dispatch_time(DISPATCH_TIME_NOW, NSEC_PER_SEC),
                              NSEC_PER_SEC,
                              50 * NSEC_PER_MSEC);
    dispatch_source_set_event_handler(_keepaliveTimer, ^{
        typeof(self) strongSelf = weakSelf;
        if (strongSelf == nil || generation != strongSelf->_generation) return;
        [strongSelf keepaliveTickLocked];
    });
    dispatch_resume(_keepaliveTimer);
}

- (void)readAvailableLocked {
    uint8_t bytes[16384];
    while (_fileDescriptor >= 0) {
        ssize_t count = recv(_fileDescriptor, bytes, sizeof(bytes), MSG_DONTWAIT);
        if (count > 0) {
            [_receiveBuffer appendBytes:bytes length:(NSUInteger)count];
            _lastReceivedNanoseconds = DSMonotonicNanoseconds();
            if (![self parseFramesLocked]) return;
            continue;
        }
        if (count == 0) {
            [self connectionFailedLocked:DSControlPOSIXError(@"control connection closed", ECONNRESET)];
            return;
        }
        if (errno == EINTR) continue;
        if (errno == EAGAIN || errno == EWOULDBLOCK) return;
        [self connectionFailedLocked:DSControlPOSIXError(@"recv", errno)];
        return;
    }
}

- (BOOL)parseFramesLocked {
    while (_receiveBuffer.length >= 4) {
        uint32_t payloadLength = DSReadUInt32BigEndian(_receiveBuffer.bytes);
        if (payloadLength == 0 || payloadLength > DSMaximumControlFrame) {
            NSError *error = [NSError errorWithDomain:DSProtocolErrorDomain
                                                 code:DSProtocolErrorMalformedPacket
                                             userInfo:@{NSLocalizedDescriptionKey: @"Malformed control frame length"}];
            [self connectionFailedLocked:error];
            return NO;
        }
        NSUInteger frameLength = 4 + payloadLength;
        if (_receiveBuffer.length < frameLength) return YES;

        NSData *jsonData = [_receiveBuffer subdataWithRange:NSMakeRange(4, payloadLength)];
        [_receiveBuffer replaceBytesInRange:NSMakeRange(0, frameLength) withBytes:NULL length:0];
        id object = [NSJSONSerialization JSONObjectWithData:jsonData options:0 error:NULL];
        if (![object isKindOfClass:NSDictionary.class] ||
            ![object[@"type"] isKindOfClass:NSString.class]) {
            NSError *error = [NSError errorWithDomain:DSProtocolErrorDomain
                                                 code:DSProtocolErrorMalformedPacket
                                             userInfo:@{NSLocalizedDescriptionKey: @"Malformed control JSON"}];
            [self connectionFailedLocked:error];
            return NO;
        }
        NSDictionary *message = object;
        dispatch_async(_callbackQueue, ^{ self->_messageHandler(message); });
    }
    return YES;
}

- (void)keepaliveTickLocked {
    if (_fileDescriptor < 0) return;
    uint64_t now = DSMonotonicNanoseconds();
    if (now - _lastReceivedNanoseconds > DSSilenceTimeoutNanoseconds) {
        NSError *error = [NSError errorWithDomain:DSNetworkErrorDomain
                                             code:ETIMEDOUT
                                         userInfo:@{NSLocalizedDescriptionKey: @"Control channel was silent for six seconds"}];
        [self connectionFailedLocked:error];
        return;
    }
    if (_lastPingNanoseconds == 0 || now - _lastPingNanoseconds >= DSPingIntervalNanoseconds) {
        _lastPingNanoseconds = now;
        uint64_t microseconds = now / 1000;
        NSDictionary *ping = @{ @"type": @"PING", @"t0Us": @(microseconds) };
        NSData *json = [NSJSONSerialization dataWithJSONObject:ping options:0 error:NULL];
        NSMutableData *frame = [NSMutableData dataWithLength:4 + json.length];
        DSWriteUInt32BigEndian(frame.mutableBytes, (uint32_t)json.length);
        memcpy((uint8_t *)frame.mutableBytes + 4, json.bytes, json.length);
        [self sendFrameLocked:frame];
    }
}

- (void)sendFrameLocked:(NSData *)frame {
    const uint8_t *bytes = frame.bytes;
    NSUInteger sentTotal = 0;
    while (_fileDescriptor >= 0 && sentTotal < frame.length) {
        ssize_t count = send(_fileDescriptor, bytes + sentTotal, frame.length - sentTotal, 0);
        if (count > 0) {
            sentTotal += (NSUInteger)count;
            continue;
        }
        if (count < 0 && errno == EINTR) continue;
        int code = count == 0 ? ECONNRESET : errno;
        [self connectionFailedLocked:DSControlPOSIXError(@"send", code)];
        return;
    }
}

- (void)connectionFailedLocked:(NSError *)error {
    [self closeSocketLocked];
    dispatch_async(_callbackQueue, ^{ self->_errorHandler(error); });
    if (!_explicitlyDisconnected && _automaticallyReconnects) {
        [self scheduleReconnectLocked];
    } else {
        [self transitionLocked:DSControlStateDisconnected];
    }
}

- (void)scheduleReconnectLocked {
    NSTimeInterval delay = _nextBackoff;
    _nextBackoff = MIN(5.0, _nextBackoff * 2.0);
    NSUInteger attempt = ++_reconnectAttempt;
    NSUInteger generation = _generation;
    [self transitionLocked:DSControlStateReconnecting];
    DSReconnectHandler handler = _reconnectHandler;
    if (handler != nil) {
        dispatch_async(_callbackQueue, ^{ handler(attempt, delay); });
    }
    dispatch_after(dispatch_time(DISPATCH_TIME_NOW, (int64_t)(delay * NSEC_PER_SEC)), _ioQueue, ^{
        if (self->_explicitlyDisconnected || generation != self->_generation || self->_fileDescriptor >= 0) return;
        [self openSocketLocked];
    });
}

- (void)closeSocketLocked {
    _generation++;
    if (_readSource != nil) {
        dispatch_source_cancel(_readSource);
        _readSource = nil;
    }
    if (_keepaliveTimer != nil) {
        dispatch_source_cancel(_keepaliveTimer);
        _keepaliveTimer = nil;
    }
    if (_fileDescriptor >= 0) {
        shutdown(_fileDescriptor, SHUT_RDWR);
        close(_fileDescriptor);
        _fileDescriptor = -1;
    }
    [_receiveBuffer setLength:0];
}

- (void)transitionLocked:(DSControlState)newState {
    @synchronized (self) {
        if (_state == newState) return;
        _state = newState;
    }
    dispatch_async(_callbackQueue, ^{ self->_stateHandler(newState); });
}

- (void)dealloc {
    _explicitlyDisconnected = YES;
    [self closeSocketLocked];
}

@end
