#import <Foundation/Foundation.h>

NS_ASSUME_NONNULL_BEGIN

/// Stores the stable client UUID and per-server pairing tokens as generic-password Keychain
/// items. Keeping both in Keychain prevents a surviving token from being paired with a new
/// client ID after reinstall.
@interface DSCredentialStore : NSObject

- (instancetype)init;
- (instancetype)initWithService:(NSString *)service NS_DESIGNATED_INITIALIZER;

- (nullable NSString *)clientIdentifier:(NSError **)error;
- (nullable NSString *)tokenForServer:(NSString *)serverHost error:(NSError **)error;
- (BOOL)setToken:(NSString *)token forServer:(NSString *)serverHost error:(NSError **)error;
- (BOOL)removeTokenForServer:(NSString *)serverHost error:(NSError **)error;

@property(nonatomic, copy, readonly) NSString *service;

@end

NS_ASSUME_NONNULL_END
