// The SDK's secrets on iOS (Runtime/Unity/SecretStores.cs IosKeychainStore): one generic password per key
// under the app's service, accessible after the first unlock and on this device only (not in iCloud
// backups, not moved to a new iPhone). Strings come back strdup'ed: the runtime frees them.
#import <Foundation/Foundation.h>
#import <Security/Security.h>

static NSMutableDictionary *KrtuQuery(const char *service, const char *key) {
    NSMutableDictionary *query = [@{
        (__bridge id)kSecClass: (__bridge id)kSecClassGenericPassword,
        (__bridge id)kSecAttrService: [NSString stringWithUTF8String:service],
    } mutableCopy];
    if (key != NULL) query[(__bridge id)kSecAttrAccount] = [NSString stringWithUTF8String:key];
    return query;
}

static char *KrtuCopy(NSString *text) {
    return text == nil ? NULL : strdup(text.UTF8String);
}

extern "C" char *KrtuKeychainGet(const char *service, const char *key) {
    NSMutableDictionary *query = KrtuQuery(service, key);
    query[(__bridge id)kSecReturnData] = @YES;
    query[(__bridge id)kSecMatchLimit] = (__bridge id)kSecMatchLimitOne;
    CFTypeRef result = NULL;
    if (SecItemCopyMatching((__bridge CFDictionaryRef)query, &result) != errSecSuccess) return NULL;
    NSData *data = (__bridge_transfer NSData *)result;
    return KrtuCopy([[NSString alloc] initWithData:data encoding:NSUTF8StringEncoding]);
}

extern "C" int KrtuKeychainSet(const char *service, const char *key, const char *value) {
    NSData *data = [[NSString stringWithUTF8String:value] dataUsingEncoding:NSUTF8StringEncoding];
    NSMutableDictionary *query = KrtuQuery(service, key);
    NSDictionary *fields = @{
        (__bridge id)kSecValueData: data,
        (__bridge id)kSecAttrAccessible: (__bridge id)kSecAttrAccessibleAfterFirstUnlockThisDeviceOnly,
    };
    OSStatus status = SecItemUpdate((__bridge CFDictionaryRef)query, (__bridge CFDictionaryRef)fields);
    if (status == errSecItemNotFound) {
        [query addEntriesFromDictionary:fields];
        status = SecItemAdd((__bridge CFDictionaryRef)query, NULL);
    }
    return (int)status;
}

extern "C" int KrtuKeychainDelete(const char *service, const char *key) {
    OSStatus status = SecItemDelete((__bridge CFDictionaryRef)KrtuQuery(service, key));
    return status == errSecItemNotFound ? 0 : (int)status;
}

extern "C" char *KrtuKeychainKeys(const char *service) {
    NSMutableDictionary *query = KrtuQuery(service, NULL);
    query[(__bridge id)kSecReturnAttributes] = @YES;
    query[(__bridge id)kSecMatchLimit] = (__bridge id)kSecMatchLimitAll;
    CFTypeRef result = NULL;
    if (SecItemCopyMatching((__bridge CFDictionaryRef)query, &result) != errSecSuccess) return KrtuCopy(@"");
    NSArray *items = (__bridge_transfer NSArray *)result;
    NSMutableArray *names = [NSMutableArray array];
    for (NSDictionary *item in items) {
        NSString *account = item[(__bridge id)kSecAttrAccount];
        if (account != nil) [names addObject:account];
    }
    return KrtuCopy([names componentsJoinedByString:@"\n"]);
}
