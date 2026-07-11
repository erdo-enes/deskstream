#import <Foundation/Foundation.h>

#import "DSControlClient.h"
#import "DSCredentialStore.h"
#import "DSDiscoveryService.h"
#import "DSFrameAssembler.h"
#import "DSProtocol.h"
#import "DSUDPSocket.h"

#import <arpa/inet.h>
#import <errno.h>
#import <sys/socket.h>
#import <unistd.h>

static int DSFailures = 0;

#define DSAssert(condition, format, ...) do { \
    if (!(condition)) { \
        DSFailures++; \
        fprintf(stderr, "FAIL %s:%d: %s\n", __FILE__, __LINE__, \
                [[NSString stringWithFormat:(format), ##__VA_ARGS__] UTF8String]); \
    } \
} while (0)

static NSData *DSMediaDatagram(uint32_t frameID,
                               uint16_t packetIndex,
                               uint16_t packetCount,
                               BOOL fec,
                               BOOL keyframe,
                               NSData *payload) {
    uint16_t fecCount = (uint16_t)((packetCount + 7) / 8);
    NSMutableData *datagram = [NSMutableData dataWithLength:DSMediaHeaderSize + payload.length];
    uint8_t *bytes = datagram.mutableBytes;
    bytes[0] = DSProtocolVersion;
    bytes[1] = (keyframe ? DSMediaFlagKeyframe : 0) | (fec ? DSMediaFlagFEC : 0);
    DSWriteUInt16BigEndian(bytes + 2, (uint16_t)payload.length);
    DSWriteUInt32BigEndian(bytes + 4, frameID);
    DSWriteUInt16BigEndian(bytes + 8, packetIndex);
    DSWriteUInt16BigEndian(bytes + 10, packetCount);
    DSWriteUInt16BigEndian(bytes + 12, fecCount);
    DSWriteUInt32BigEndian(bytes + 14, 1234);
    DSWriteUInt16BigEndian(bytes + 18, 7);
    memcpy(bytes + DSMediaHeaderSize, payload.bytes, payload.length);
    return datagram;
}

static NSData *DSFilledData(NSUInteger length, uint8_t seed) {
    NSMutableData *data = [NSMutableData dataWithLength:length];
    uint8_t *bytes = data.mutableBytes;
    for (NSUInteger index = 0; index < length; index++) {
        bytes[index] = (uint8_t)(seed + index * 17);
    }
    return data;
}

static NSData *DSParity(NSArray<NSData *> *chunks) {
    NSUInteger maximum = 0;
    for (NSData *chunk in chunks) maximum = MAX(maximum, chunk.length);
    NSMutableData *parity = [NSMutableData dataWithLength:maximum];
    uint8_t *result = parity.mutableBytes;
    for (NSData *chunk in chunks) {
        const uint8_t *bytes = chunk.bytes;
        for (NSUInteger index = 0; index < chunk.length; index++) result[index] ^= bytes[index];
    }
    return parity;
}

static void TestEndianAndInputPackets(void) {
    uint8_t bytes[8] = {0};
    DSWriteUInt16BigEndian(bytes, 0xA1B2);
    DSWriteUInt32BigEndian(bytes + 2, 0xC3D4E5F6);
    DSAssert(bytes[0] == 0xA1 && bytes[1] == 0xB2, @"UInt16 wire order");
    DSAssert(DSReadUInt16BigEndian(bytes) == 0xA1B2, @"UInt16 round trip");
    DSAssert(DSReadUInt32BigEndian(bytes + 2) == 0xC3D4E5F6, @"UInt32 round trip");
    DSAssert(DSUInt32IsNewer(0, UINT32_MAX), @"UInt32 wrap comparison");
    DSAssert(!DSUInt32IsNewer(UINT32_MAX, 0), @"UInt32 stale wrap comparison");

    NSError *error = nil;
    NSData *mouse = DSMakeMousePacket(0x01020304,
                                      DSMouseModeRelative,
                                      -11,
                                      22,
                                      -120,
                                      240,
                                      &error);
    DSAssert(mouse.length == DSMousePacketSize && error == nil, @"Mouse packet creation");
    const uint8_t *mouseBytes = mouse.bytes;
    DSAssert(memcmp(mouseBytes, "DSMI", 4) == 0, @"Mouse magic");
    DSAssert(mouseBytes[4] == 1 && mouseBytes[5] == 0 && mouseBytes[6] == 0 && mouseBytes[7] == 0,
             @"Mouse fixed fields");
    DSAssert(DSReadUInt32BigEndian(mouseBytes + 8) == 0x01020304, @"Mouse sequence");
    DSAssert(DSReadInt32BigEndian(mouseBytes + 12) == -11, @"Mouse x");
    DSAssert(DSReadInt32BigEndian(mouseBytes + 16) == 22, @"Mouse y");
    DSAssert(DSReadInt32BigEndian(mouseBytes + 20) == -120, @"Mouse horizontal wheel");
    DSAssert(DSReadInt32BigEndian(mouseBytes + 24) == 240, @"Mouse vertical wheel");

    error = nil;
    NSData *invalidAbsolute = DSMakeMousePacket(1, DSMouseModeAbsolute, -1, 0, 0, 0, &error);
    DSAssert(invalidAbsolute == nil && error != nil, @"Reject invalid absolute cursor");

    DSGamepadState state = {
        .controllerID = 2,
        .buttons = 0xA55A,
        .leftTrigger = 3,
        .rightTrigger = 250,
        .leftX = INT16_MIN,
        .leftY = INT16_MAX,
        .rightX = -1234,
        .rightY = 5678,
        .sequence = 0xF0E0D0C0,
    };
    error = nil;
    NSData *gamepad = DSMakeGamepadPacket(state, &error);
    const uint8_t *gamepadBytes = gamepad.bytes;
    DSAssert(gamepad.length == DSGamepadPacketSize && error == nil, @"Gamepad packet creation");
    DSAssert(memcmp(gamepadBytes, "DSGP", 4) == 0, @"Gamepad magic");
    DSAssert(gamepadBytes[5] == 2 && DSReadUInt16BigEndian(gamepadBytes + 6) == 0xA55A,
             @"Gamepad identity/buttons");
    DSAssert(DSReadInt16BigEndian(gamepadBytes + 10) == INT16_MIN, @"Gamepad signed left x");
    DSAssert(DSReadInt16BigEndian(gamepadBytes + 16) == 5678, @"Gamepad signed right y");
    DSAssert(DSReadUInt32BigEndian(gamepadBytes + 18) == 0xF0E0D0C0, @"Gamepad sequence");
    DSAssert(gamepadBytes[22] == 0 && gamepadBytes[23] == 0, @"Gamepad reserved bytes");
}

static void TestMediaHeaderAndAssembly(void) {
    NSData *first = DSFilledData(DSMediaMaximumPayload, 9);
    NSData *last = DSFilledData(37, 91);
    NSData *firstPacket = DSMediaDatagram(7, 0, 2, NO, YES, first);
    DSMediaHeader header;
    const uint8_t *payload = NULL;
    DSAssert(DSParseMediaDatagram(firstPacket.bytes, firstPacket.length, &header, &payload),
             @"Parse valid media packet");
    DSAssert(header.frameID == 7 && header.packetCount == 2 && header.packetIndex == 0,
             @"Parsed media fields");
    DSAssert(payload[0] == ((const uint8_t *)first.bytes)[0], @"Parsed media payload pointer");
    NSMutableData *trailing = [firstPacket mutableCopy];
    uint8_t extra = 0;
    [trailing appendBytes:&extra length:1];
    DSAssert(!DSParseMediaDatagram(trailing.bytes, trailing.length, NULL, NULL),
             @"Reject trailing datagram bytes");

    __block NSData *assembled = nil;
    __block NSUInteger outputCount = 0;
    __block NSUInteger dropCount = 0;
    __block uint32_t outputFrameID = 0;
    __block BOOL outputKeyframe = NO;
    DSFrameAssembler *assembler = [[DSFrameAssembler alloc]
        initWithOutputHandler:^(NSData *accessUnit, BOOL keyframe, uint32_t frameID,
                                uint32_t pts, uint16_t delay) {
            (void)pts; (void)delay;
            assembled = accessUnit;
            outputCount++;
            outputFrameID = frameID;
            outputKeyframe = keyframe;
        }
        dropHandler:^{ dropCount++; }];
    [assembler consumeDatagram:firstPacket];
    [assembler consumeDatagram:DSMediaDatagram(7, 1, 2, NO, YES, last)];
    NSMutableData *expected = [first mutableCopy];
    [expected appendData:last];
    DSAssert(outputCount == 1 && outputFrameID == 7 && outputKeyframe &&
             [assembled isEqualToData:expected],
             @"Two-packet assembly");
    DSAssert(dropCount == 0 && assembler.incompleteFrameCount == 0, @"Clean assembly state");

    // Recover packet zero from packet one plus XOR parity.
    assembled = nil;
    outputCount = 0;
    [assembler reset];
    NSData *parity = DSParity(@[first, last]);
    [assembler consumeDatagram:DSMediaDatagram(8, 1, 2, NO, YES, last)];
    [assembler consumeDatagram:DSMediaDatagram(8, 0, 2, YES, YES, parity)];
    DSAssert(outputCount == 1 && outputFrameID == 8 && outputKeyframe &&
             [assembled isEqualToData:expected],
             @"Single-loss XOR recovery");

    // Reordering, duplicates, and one loss in each of two independent FEC groups.
    [assembler reset];
    assembled = nil;
    outputCount = 0;
    NSMutableArray<NSData *> *chunks = [NSMutableArray array];
    NSMutableData *largeExpected = [NSMutableData data];
    for (NSUInteger index = 0; index < 10; index++) {
        NSData *chunk = DSFilledData(index == 9 ? 73 : DSMediaMaximumPayload,
                                    (uint8_t)(index * 19));
        [chunks addObject:chunk];
        [largeExpected appendData:chunk];
    }
    NSData *parity0 = DSParity([chunks subarrayWithRange:NSMakeRange(0, 8)]);
    NSData *parity1 = DSParity([chunks subarrayWithRange:NSMakeRange(8, 2)]);
    [assembler consumeDatagram:DSMediaDatagram(30, 0, 10, YES, YES, parity0)];
    for (NSInteger index = 9; index >= 0; index--) {
        if (index == 3 || index == 8) continue;
        NSData *packet = DSMediaDatagram(30, (uint16_t)index, 10, NO, YES,
                                         chunks[(NSUInteger)index]);
        [assembler consumeDatagram:packet];
        if (index == 5) [assembler consumeDatagram:packet];
    }
    [assembler consumeDatagram:DSMediaDatagram(30, 1, 10, YES, YES, parity1)];
    DSAssert(outputCount == 1 && outputFrameID == 30 &&
             [assembled isEqualToData:largeExpected], @"Multi-group reordered FEC recovery");

    // Two losses in one group cannot recover until one missing real packet arrives.
    [assembler reset];
    assembled = nil;
    outputCount = 0;
    NSData *middle = DSFilledData(DSMediaMaximumPayload, 44);
    NSData *threeParity = DSParity(@[first, middle, last]);
    [assembler consumeDatagram:DSMediaDatagram(31, 2, 3, NO, YES, last)];
    [assembler consumeDatagram:DSMediaDatagram(31, 0, 3, YES, YES, threeParity)];
    DSAssert(outputCount == 0, @"Two losses in one XOR group remain incomplete");
    [assembler consumeDatagram:DSMediaDatagram(31, 0, 3, NO, YES, first)];
    NSMutableData *threeExpected = [first mutableCopy];
    [threeExpected appendData:middle];
    [threeExpected appendData:last];
    DSAssert(outputCount == 1 && [assembled isEqualToData:threeExpected],
             @"Later real packet enables XOR recovery");

    // Four incomplete frames are allowed for Wi-Fi reordering; a fifth non-keyframe drops the
    // oldest and enters
    // discard mode. A complete keyframe then removes the older remainder and recovers.
    assembled = nil;
    outputCount = 0;
    dropCount = 0;
    [assembler reset];
    [assembler consumeDatagram:DSMediaDatagram(10, 0, 2, NO, NO, first)];
    [assembler consumeDatagram:DSMediaDatagram(11, 0, 2, NO, NO, first)];
    [assembler consumeDatagram:DSMediaDatagram(12, 0, 2, NO, NO, first)];
    [assembler consumeDatagram:DSMediaDatagram(13, 0, 2, NO, NO, first)];
    [assembler consumeDatagram:DSMediaDatagram(14, 0, 2, NO, NO, first)];
    DSAssert(dropCount == 1 && assembler.discardingUntilKeyframe,
             @"Fifth incomplete frame triggers IDR/discard");
    [assembler consumeDatagram:DSMediaDatagram(15, 0, 1, NO, NO, last)];
    DSAssert(outputCount == 0, @"Non-keyframe suppressed after reference loss");
    [assembler consumeDatagram:DSMediaDatagram(16, 0, 1, NO, YES, last)];
    DSAssert(outputCount == 1 && outputFrameID == 16 && [assembled isEqualToData:last],
             @"Complete keyframe resumes output");
    DSAssert(dropCount == 2 && !assembler.discardingUntilKeyframe,
             @"Older incomplete frame dropped before keyframe output");

    // A completely lost frame creates no in-flight object, so verify frame-ID gap detection.
    outputCount = 0;
    dropCount = 0;
    [assembler reset];
    [assembler consumeDatagram:DSMediaDatagram(20, 0, 1, NO, YES, last)];
    [assembler consumeDatagram:DSMediaDatagram(21, 0, 1, NO, NO, last)];
    DSAssert(outputCount == 2 && outputFrameID == 21 && !outputKeyframe,
             @"Contiguous inter-frame output");
    [assembler consumeDatagram:DSMediaDatagram(23, 0, 1, NO, NO, last)];
    DSAssert(outputCount == 2 && dropCount == 1 && assembler.discardingUntilKeyframe,
             @"Completely missing frame detected from frame ID gap");
    [assembler consumeDatagram:DSMediaDatagram(24, 0, 1, NO, YES, last)];
    DSAssert(outputCount == 3 && outputFrameID == 24 && outputKeyframe &&
             !assembler.discardingUntilKeyframe, @"Keyframe heals complete-frame loss");
}

static void TestDiscoveryModels(void) {
    NSData *reply = [@"{\"type\":\"DSREPLY\",\"ver\":1,\"name\":\"PC\",\"controlPort\":47801}"
        dataUsingEncoding:NSUTF8StringEncoding];
    DSDiscoveredServer *server = [DSDiscoveryService serverFromReplyData:reply sourceHost:@"192.168.1.9"];
    DSAssert([server.name isEqual:@"PC"] && [server.host isEqual:@"192.168.1.9"] &&
             server.controlPort == 47801, @"Parse discovery reply");
    NSData *wrongVersion = [@"{\"type\":\"DSREPLY\",\"ver\":2,\"controlPort\":47801}"
        dataUsingEncoding:NSUTF8StringEncoding];
    DSAssert([DSDiscoveryService serverFromReplyData:wrongVersion sourceHost:@"127.0.0.1"] == nil,
             @"Reject discovery version mismatch");
    DSDiscoveredServer *manual = [DSDiscoveryService manualServerWithHost:@" 10.0.0.2 "
                                                                      port:47801
                                                                      name:nil];
    DSAssert([manual.host isEqual:@"10.0.0.2"] && [manual.name isEqual:@"10.0.0.2"],
             @"Manual server uses discovery-compatible model");
}

static void TestUDPSocket(void) {
    dispatch_queue_t queue = dispatch_queue_create("com.deskstream.tests.udp", DISPATCH_QUEUE_SERIAL);
    dispatch_semaphore_t received = dispatch_semaphore_create(0);
    __block NSData *receivedData = nil;
    NSError *error = nil;

    DSUDPSocket *sender = [[DSUDPSocket alloc] initWithExpectedHost:nil
                                                      expectedPort:0
                                                             queue:queue
                                                           handler:^(__unused NSData *data,
                                                                     __unused NSString *host,
                                                                     __unused uint16_t port) {}
                                                             error:&error];
    if (![sender bindToPort:0 receiveBufferSize:65536 error:&error]) {
        if ([error.domain isEqual:NSPOSIXErrorDomain] && error.code == EPERM) {
            printf("SKIP UDP loopback test (sandbox denied bind).\n");
            return;
        }
        DSAssert(NO, @"Bind UDP sender: %@", error);
        return;
    }

    DSUDPSocket *receiver = [[DSUDPSocket alloc] initWithExpectedHost:@"127.0.0.1"
                                                        expectedPort:sender.localPort
                                                               queue:queue
                                                             handler:^(NSData *data,
                                                                       __unused NSString *host,
                                                                       __unused uint16_t port) {
        receivedData = data;
        dispatch_semaphore_signal(received);
    }
                                                               error:&error];
    DSAssert([receiver bindToPort:0 receiveBufferSize:65536 error:&error], @"Bind UDP receiver: %@", error);
    DSAssert([receiver start:&error], @"Start UDP receiver: %@", error);

    DSUDPSocket *wrongSender = [[DSUDPSocket alloc] initWithExpectedHost:nil
                                                            expectedPort:0
                                                                   queue:queue
                                                                 handler:^(__unused NSData *data,
                                                                           __unused NSString *host,
                                                                           __unused uint16_t port) {}
                                                                   error:&error];
    [wrongSender bindToPort:0 receiveBufferSize:65536 error:&error];
    [wrongSender sendData:[@"wrong" dataUsingEncoding:NSUTF8StringEncoding]
                   toHost:@"127.0.0.1" port:receiver.localPort error:&error];
    long early = dispatch_semaphore_wait(received,
                                         dispatch_time(DISPATCH_TIME_NOW, 100 * NSEC_PER_MSEC));
    DSAssert(early != 0, @"Reject UDP packet from unexpected source port");

    NSData *expected = [@"deskstream" dataUsingEncoding:NSUTF8StringEncoding];
    DSAssert([sender sendData:expected toHost:@"127.0.0.1" port:receiver.localPort error:&error],
             @"Send validated UDP packet: %@", error);
    long wait = dispatch_semaphore_wait(received,
                                        dispatch_time(DISPATCH_TIME_NOW, 2 * NSEC_PER_SEC));
    DSAssert(wait == 0 && [receivedData isEqualToData:expected], @"Receive validated UDP packet");
    [receiver stop];

    dispatch_semaphore_t rawReceived = dispatch_semaphore_create(0);
    __block NSData *rawReceivedData = nil;
    DSUDPSocket *rawReceiver = [[DSUDPSocket alloc]
        initWithExpectedHost:@"127.0.0.1"
        expectedPort:sender.localPort
        queue:queue
        rawHandler:^(const void *bytes, size_t length) {
            // The production media path parses synchronously. Copy here only so the test can
            // compare after the callback returns and prove the ephemeral-byte contract works.
            rawReceivedData = [NSData dataWithBytes:bytes length:length];
            dispatch_semaphore_signal(rawReceived);
        }
        error:&error];
    DSAssert([rawReceiver bindToPort:0 receiveBufferSize:65536 error:&error],
             @"Bind raw UDP receiver: %@", error);
    DSAssert([rawReceiver start:&error], @"Start raw UDP receiver: %@", error);
    DSAssert([sender sendData:expected toHost:@"127.0.0.1" port:rawReceiver.localPort error:&error],
             @"Send raw UDP packet: %@", error);
    wait = dispatch_semaphore_wait(rawReceived,
                                   dispatch_time(DISPATCH_TIME_NOW, 2 * NSEC_PER_SEC));
    DSAssert(wait == 0 && [rawReceivedData isEqualToData:expected], @"Receive raw UDP packet");

    [rawReceiver stop];
    [wrongSender stop];
    [sender stop];
}

static BOOL DSReadExact(int descriptor, void *buffer, size_t length) {
    uint8_t *bytes = buffer;
    size_t total = 0;
    while (total < length) {
        ssize_t count = recv(descriptor, bytes + total, length - total, 0);
        if (count <= 0) return NO;
        total += (size_t)count;
    }
    return YES;
}

static NSDictionary *DSReadControlFrame(int descriptor) {
    uint8_t header[4];
    if (!DSReadExact(descriptor, header, sizeof(header))) return nil;
    uint32_t length = DSReadUInt32BigEndian(header);
    if (length == 0 || length > 65536) return nil;
    NSMutableData *payload = [NSMutableData dataWithLength:length];
    if (!DSReadExact(descriptor, payload.mutableBytes, length)) return nil;
    id object = [NSJSONSerialization JSONObjectWithData:payload options:0 error:NULL];
    return [object isKindOfClass:NSDictionary.class] ? object : nil;
}

static BOOL DSSendControlFrame(int descriptor, NSDictionary *message) {
    NSData *json = [NSJSONSerialization dataWithJSONObject:message options:0 error:NULL];
    NSMutableData *frame = [NSMutableData dataWithLength:4 + json.length];
    DSWriteUInt32BigEndian(frame.mutableBytes, (uint32_t)json.length);
    memcpy((uint8_t *)frame.mutableBytes + 4, json.bytes, json.length);
    const uint8_t *bytes = frame.bytes;
    NSUInteger total = 0;
    while (total < frame.length) {
        ssize_t count = send(descriptor, bytes + total, frame.length - total, 0);
        if (count <= 0) return NO;
        total += (NSUInteger)count;
    }
    return YES;
}

static void TestControlClient(void) {
    int listener = socket(AF_INET, SOCK_STREAM, IPPROTO_TCP);
    DSAssert(listener >= 0, @"Create TCP listener");
    int enabled = 1;
    setsockopt(listener, SOL_SOCKET, SO_REUSEADDR, &enabled, sizeof(enabled));
    struct sockaddr_in address = {0};
    address.sin_len = sizeof(address);
    address.sin_family = AF_INET;
    address.sin_addr.s_addr = htonl(INADDR_LOOPBACK);
    if (bind(listener, (const struct sockaddr *)&address, sizeof(address)) != 0) {
        if (errno == EPERM) {
            printf("SKIP TCP loopback test (sandbox denied bind).\n");
            close(listener);
            return;
        }
        DSAssert(NO, @"Bind TCP listener: %s", strerror(errno));
        close(listener);
        return;
    }
    DSAssert(listen(listener, 1) == 0, @"Listen TCP");
    socklen_t addressLength = sizeof(address);
    getsockname(listener, (struct sockaddr *)&address, &addressLength);
    uint16_t port = ntohs(address.sin_port);

    dispatch_semaphore_t serverFinished = dispatch_semaphore_create(0);
    __block BOOL ordered = NO;
    dispatch_async(dispatch_get_global_queue(QOS_CLASS_USER_INITIATED, 0), ^{
        int peer = accept(listener, NULL, NULL);
        NSDictionary *first = peer >= 0 ? DSReadControlFrame(peer) : nil;
        NSDictionary *second = peer >= 0 ? DSReadControlFrame(peer) : nil;
        ordered = [first[@"type"] isEqual:@"FIRST"] && [second[@"type"] isEqual:@"SECOND"];
        if (peer >= 0) {
            DSSendControlFrame(peer, @{ @"type": @"TEST_REPLY", @"value": @42 });
            usleep(200000);
            close(peer);
        }
        close(listener);
        dispatch_semaphore_signal(serverFinished);
    });

    dispatch_queue_t callbacks = dispatch_queue_create("com.deskstream.tests.control", DISPATCH_QUEUE_SERIAL);
    dispatch_semaphore_t replyReceived = dispatch_semaphore_create(0);
    dispatch_semaphore_t disconnected = dispatch_semaphore_create(0);
    __block DSControlClient *client = nil;
    __block __weak DSControlClient *weakClient = nil;
    client = [[DSControlClient alloc] initWithHost:@"127.0.0.1"
                                              port:port
                                     callbackQueue:callbacks
                                    messageHandler:^(NSDictionary<NSString *,id> *message) {
        if ([message[@"type"] isEqual:@"TEST_REPLY"] && [message[@"value"] integerValue] == 42) {
            dispatch_semaphore_signal(replyReceived);
        }
    }
                                      stateHandler:^(DSControlState state) {
        if (state == DSControlStateConnected) {
            [weakClient sendMessage:@{@"type": @"FIRST", @"n": @1} error:NULL];
            [weakClient sendMessage:@{@"type": @"SECOND", @"n": @2} error:NULL];
        } else if (state == DSControlStateDisconnected) {
            dispatch_semaphore_signal(disconnected);
        }
    }
                                      errorHandler:^(__unused NSError *controlError) {}];
    weakClient = client;
    client.automaticallyReconnects = NO;
    [client connect];

    long replyWait = dispatch_semaphore_wait(replyReceived,
                                              dispatch_time(DISPATCH_TIME_NOW, 3 * NSEC_PER_SEC));
    long serverWait = dispatch_semaphore_wait(serverFinished,
                                               dispatch_time(DISPATCH_TIME_NOW, 3 * NSEC_PER_SEC));
    DSAssert(replyWait == 0, @"Receive framed TCP JSON");
    DSAssert(serverWait == 0 && ordered, @"TCP writes preserve call order");
    [client disconnect];
    dispatch_semaphore_wait(disconnected, dispatch_time(DISPATCH_TIME_NOW, NSEC_PER_SEC));
}

static void TestCredentialStoreConstruction(void) {
    DSCredentialStore *store = [[DSCredentialStore alloc] initWithService:@"com.deskstream.tests"];
    DSAssert([store.service isEqual:@"com.deskstream.tests"], @"Credential store service isolation");
}

int main(void) {
    @autoreleasepool {
        TestEndianAndInputPackets();
        TestMediaHeaderAndAssembly();
        TestDiscoveryModels();
        TestUDPSocket();
        TestControlClient();
        TestCredentialStoreConstruction();

        if (DSFailures == 0) {
            printf("DeskStream macOS core tests passed.\n");
            return 0;
        }
        fprintf(stderr, "%d DeskStream macOS core test(s) failed.\n", DSFailures);
        return 1;
    }
}
