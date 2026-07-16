#import <Foundation/Foundation.h>

#import "DSProtocol.h"

NS_ASSUME_NONNULL_BEGIN

/// Correlates the optional server DSTR sidecar with the macOS frame lifecycle. All recording is
/// queued away from the media and renderer hot paths, and completed records are written in batches
/// to ~/Library/Logs/DeskStream/frame-trace.jsonl.
@interface DSFrameTraceCollector : NSObject

- (void)recordServerTrace:(DSFrameTrace)trace;
- (void)recordReceiveForFrameID:(uint32_t)frameID atMicroseconds:(uint64_t)timestamp;
- (void)recordAssembleForFrameID:(uint32_t)frameID atMicroseconds:(uint64_t)timestamp;

/// AVSampleBufferDisplayLayer does not expose decode-complete or scanout callbacks. These two
/// values are therefore explicitly recorded as decoder submission and renderer enqueue, rather
/// than being mislabeled as exact Decode/Present completion.
- (void)recordRendererSubmissionForFrameID:(uint32_t)frameID
                       decodeSubmitMicroseconds:(uint64_t)decodeSubmit
                      presentEnqueueMicroseconds:(uint64_t)presentEnqueue;

/// `offset` is positive when the server monotonic clock is ahead of this Mac's clock.
- (void)updateClockOffsetMicroseconds:(int64_t)offset roundTripMicroseconds:(uint64_t)roundTrip;
- (void)reset;

@end

NS_ASSUME_NONNULL_END
