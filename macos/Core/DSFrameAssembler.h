#import <Foundation/Foundation.h>

NS_ASSUME_NONNULL_BEGIN

typedef void (^DSFrameOutputHandler)(NSData *accessUnit,
                                     BOOL keyframe,
                                     uint32_t frameID,
                                     uint32_t presentationTimeMilliseconds,
                                     uint16_t pipelineDelayMilliseconds);
typedef void (^DSFrameDropHandler)(void);

/// DeskStream v1 frame reassembly and XOR-FEC recovery.
///
/// The assembler intentionally holds no more than two incomplete frames. Any reference gap
/// invokes `dropHandler` (normally REQUEST_IDR) and suppresses output until a complete
/// keyframe arrives. Calls are synchronized so decoder-error callbacks may safely request
/// discard from a queue other than the media receive queue.
@interface DSFrameAssembler : NSObject

- (instancetype)init NS_UNAVAILABLE;
- (instancetype)initWithOutputHandler:(DSFrameOutputHandler)outputHandler
                           dropHandler:(DSFrameDropHandler)dropHandler NS_DESIGNATED_INITIALIZER;

- (void)consumeDatagram:(NSData *)datagram;
- (void)consumeBytes:(const void *)bytes length:(size_t)length;

/// Used when a downstream decoder drops a reference frame. It does not invoke dropHandler;
/// the caller already knows it must request an IDR.
- (void)requestDiscardUntilKeyframe;
- (void)reset;

@property(nonatomic, readonly) NSUInteger incompleteFrameCount;
@property(nonatomic, readonly) BOOL discardingUntilKeyframe;

@end

NS_ASSUME_NONNULL_END
