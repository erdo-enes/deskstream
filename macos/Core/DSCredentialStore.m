#import "DSCredentialStore.h"

#import <Security/Security.h>
#import <errno.h>

static NSString * const DSClientIdentifierAccount = @"client-id";

@implementation DSCredentialStore

- (instancetype)init {
    return [self initWithService:@"com.deskstream.client.macos"];
}

- (instancetype)initWithService:(NSString *)service {
    NSParameterAssert(service.length > 0);
    self = [super init];
    if (self) {
        _service = [service copy];
    }
    return self;
}

- (NSString *)clientIdentifier:(NSError **)error {
    if (error != NULL) *error = nil;
    NSData *existing = [self dataForAccount:DSClientIdentifierAccount error:error];
    if (existing != nil) {
        NSString *identifier = [[NSString alloc] initWithData:existing encoding:NSUTF8StringEncoding];
        if (identifier.length > 0) return identifier;
    } else if (error != NULL && *error != nil) {
        return nil;
    }

    NSString *identifier = NSUUID.UUID.UUIDString.lowercaseString;
    NSData *data = [identifier dataUsingEncoding:NSUTF8StringEncoding];
    return [self setData:data account:DSClientIdentifierAccount error:error] ? identifier : nil;
}

- (NSString *)tokenForServer:(NSString *)serverHost error:(NSError **)error {
    if (error != NULL) *error = nil;
    if (serverHost.length == 0) return nil;
    NSData *data = [self dataForAccount:[self tokenAccountForHost:serverHost] error:error];
    if (data == nil) return nil;
    return [[NSString alloc] initWithData:data encoding:NSUTF8StringEncoding];
}

- (BOOL)setToken:(NSString *)token forServer:(NSString *)serverHost error:(NSError **)error {
    if (error != NULL) *error = nil;
    if (token.length == 0 || serverHost.length == 0) {
        if (error != NULL) {
            *error = [NSError errorWithDomain:NSPOSIXErrorDomain
                                         code:EINVAL
                                     userInfo:@{NSLocalizedDescriptionKey: @"A server host and pairing token are required"}];
        }
        return NO;
    }
    NSData *data = [token dataUsingEncoding:NSUTF8StringEncoding];
    return [self setData:data account:[self tokenAccountForHost:serverHost] error:error];
}

- (BOOL)removeTokenForServer:(NSString *)serverHost error:(NSError **)error {
    if (error != NULL) *error = nil;
    if (serverHost.length == 0) return YES;
    NSDictionary *query = [self baseQueryForAccount:[self tokenAccountForHost:serverHost]];
    OSStatus status = SecItemDelete((__bridge CFDictionaryRef)query);
    if (status == errSecSuccess || status == errSecItemNotFound) return YES;
    if (error != NULL) *error = [self errorForStatus:status operation:@"Delete credential"];
    return NO;
}

- (NSString *)tokenAccountForHost:(NSString *)host {
    return [@"token:" stringByAppendingString:host.lowercaseString];
}

- (NSDictionary *)baseQueryForAccount:(NSString *)account {
    return @{
        (__bridge id)kSecClass: (__bridge id)kSecClassGenericPassword,
        (__bridge id)kSecAttrService: _service,
        (__bridge id)kSecAttrAccount: account,
    };
}

- (NSData *)dataForAccount:(NSString *)account error:(NSError **)error {
    NSMutableDictionary *query = [[self baseQueryForAccount:account] mutableCopy];
    query[(__bridge id)kSecReturnData] = @YES;
    query[(__bridge id)kSecMatchLimit] = (__bridge id)kSecMatchLimitOne;

    CFTypeRef result = NULL;
    OSStatus status = SecItemCopyMatching((__bridge CFDictionaryRef)query, &result);
    if (status == errSecItemNotFound) return nil;
    if (status != errSecSuccess) {
        if (error != NULL) *error = [self errorForStatus:status operation:@"Read credential"];
        if (result != NULL) CFRelease(result);
        return nil;
    }
    return CFBridgingRelease(result);
}

- (BOOL)setData:(NSData *)data account:(NSString *)account error:(NSError **)error {
    NSDictionary *query = [self baseQueryForAccount:account];
    NSDictionary *attributes = @{(__bridge id)kSecValueData: data};
    OSStatus status = SecItemUpdate((__bridge CFDictionaryRef)query,
                                    (__bridge CFDictionaryRef)attributes);
    if (status == errSecItemNotFound) {
        NSMutableDictionary *addition = [query mutableCopy];
        addition[(__bridge id)kSecValueData] = data;
        status = SecItemAdd((__bridge CFDictionaryRef)addition, NULL);
    }
    if (status == errSecSuccess) return YES;
    if (error != NULL) *error = [self errorForStatus:status operation:@"Store credential"];
    return NO;
}

- (NSError *)errorForStatus:(OSStatus)status operation:(NSString *)operation {
    CFStringRef statusMessage = SecCopyErrorMessageString(status, NULL);
    NSString *detail = CFBridgingRelease(statusMessage) ?: @"Unknown Keychain error";
    NSString *description = [NSString stringWithFormat:@"%@: %@", operation, detail];
    return [NSError errorWithDomain:NSOSStatusErrorDomain
                               code:status
                           userInfo:@{NSLocalizedDescriptionKey: description}];
}

@end
