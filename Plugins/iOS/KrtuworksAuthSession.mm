// The phone sign-in on iOS (Runtime/Unity/MobileSignIn.cs): ASWebAuthenticationSession opens the Krtuworks
// ID page, shares Safari's sign-in (not ephemeral) and hands back the address that came back on the app's
// scheme. The answer goes to the runner object by name: the address, `error:cancelled` or `error:<code>`.
#import <AuthenticationServices/AuthenticationServices.h>
#import <UIKit/UIKit.h>

extern "C" void UnitySendMessage(const char *object, const char *method, const char *message);

API_AVAILABLE(ios(13.0))
@interface KrtuAuthPresenter : NSObject <ASWebAuthenticationPresentationContextProviding>
@end

@implementation KrtuAuthPresenter
- (ASPresentationAnchor)presentationAnchorForWebAuthenticationSession:(ASWebAuthenticationSession *)session {
    for (UIScene *scene in UIApplication.sharedApplication.connectedScenes) {
        if (![scene isKindOfClass:UIWindowScene.class]) continue;
        for (UIWindow *window in ((UIWindowScene *)scene).windows) {
            if (window.isKeyWindow) return window;
        }
    }
    return UIApplication.sharedApplication.windows.firstObject;
}
@end

static ASWebAuthenticationSession *krtuSession;
static id krtuPresenter;
static NSString *krtuReceiver;

static void KrtuAnswer(NSString *result) {
    dispatch_async(dispatch_get_main_queue(), ^{
        UnitySendMessage(krtuReceiver.UTF8String, "OnAuthSession", result.UTF8String);
    });
}

extern "C" void KrtuAuthSessionStart(const char *url, const char *scheme, const char *receiver) {
    NSURL *target = [NSURL URLWithString:[NSString stringWithUTF8String:url]];
    NSString *callbackScheme = [NSString stringWithUTF8String:scheme];
    krtuReceiver = [NSString stringWithUTF8String:receiver];
    dispatch_async(dispatch_get_main_queue(), ^{
        [krtuSession cancel];
        __block __weak ASWebAuthenticationSession *started = nil;
        ASWebAuthenticationSession *session = [[ASWebAuthenticationSession alloc] initWithURL:target callbackURLScheme:callbackScheme completionHandler:^(NSURL *callback, NSError *error) {
            // A session replaced by a newer one answers late (its cancel): that answer is not the newer one's.
            if (krtuSession == nil || krtuSession != started) return;
            krtuSession = nil;
            if (callback != nil) KrtuAnswer(callback.absoluteString);
            else if (error.code == ASWebAuthenticationSessionErrorCodeCanceledLogin) KrtuAnswer(@"error:cancelled");
            else KrtuAnswer([NSString stringWithFormat:@"error:%ld", (long)error.code]);
        }];
        started = session;
        krtuSession = session;
        if (@available(iOS 13.0, *)) {
            krtuPresenter = [KrtuAuthPresenter new];
            krtuSession.presentationContextProvider = krtuPresenter;
            krtuSession.prefersEphemeralWebBrowserSession = NO;
        }
        if (![krtuSession start]) {
            krtuSession = nil;
            KrtuAnswer(@"error:start_failed");
        }
    });
}

extern "C" void KrtuAuthSessionCancel(void) {
    dispatch_async(dispatch_get_main_queue(), ^{
        [krtuSession cancel];
        krtuSession = nil;
    });
}
