#import <Foundation/Foundation.h>

NS_ASSUME_NONNULL_BEGIN

/// Bounded low-latency PCM16 player for DeskStream's 5 ms audio datagrams.
@interface DSAudioPlayer : NSObject

@property (atomic, getter=isMuted) BOOL muted;
@property (atomic, readonly) NSUInteger outputDrops;
@property (atomic, readonly) uint64_t packetsLost;

- (BOOL)startWithSampleRate:(double)sampleRate
                   channels:(NSUInteger)channels
              packetSamples:(NSUInteger)packetSamples
                      error:(NSError **)error;
- (void)consumeAudioDatagram:(NSData *)datagram;
- (void)stop;

@end

NS_ASSUME_NONNULL_END
