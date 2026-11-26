#nullable enable
using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

namespace Krtuworks.Unity
{
    /// <summary>
    /// The phone's half of signing in: the sign-in page comes back to the app on its own
    /// scheme. iOS: ASWebAuthenticationSession (Plugins/iOS/KrtuworksAuthSession.mm) waits for the scheme
    /// itself and shares Safari's sign-in. Android: the browser opens the page and the scheme brings the
    /// address back as a deep link (the build step adds the scheme to the app's manifest).
    /// </summary>
    internal static class MobileSignIn
    {
        /// <summary>Android: back in the app with no address this long after it came back = the player closed the browser.</summary>
        const int NoLinkAfterResumeMs = 2000;

        static TaskCompletionSource<string>? pending;
        static string? waitingScheme;
        /// <summary>The `state` of the sign-in waiting: a late return of an earlier sign-in carries another one.</summary>
        static string? waitingState;

#if UNITY_IOS && !UNITY_EDITOR
        [DllImport("__Internal")]
        static extern void KrtuAuthSessionStart(string url, string scheme, string receiver);

        [DllImport("__Internal")]
        static extern void KrtuAuthSessionCancel();
#endif

        internal static Task<string> AuthenticateAsync(string url, string scheme, CancellationToken cancel)
        {
            pending?.TrySetCanceled();
            var started = new TaskCompletionSource<string>();
            pending = started;
            waitingScheme = scheme;
            waitingState = StateOf(url);
            cancel.Register(() =>
            {
                if (pending != started) return;
                pending = null;
#if UNITY_IOS && !UNITY_EDITOR
                KrtuAuthSessionCancel();
#endif
                started.TrySetCanceled();
            });
#if UNITY_IOS && !UNITY_EDITOR
            KrtuAuthSessionStart(url, scheme, KrtuworksRunner.ObjectName);
#else
            Application.OpenURL(url);
#endif
            return started.Task;
        }

        /// <summary>A deep link while the app runs (Android): ours when it is on the scheme a sign-in waits for.</summary>
        internal static void DeepLink(string url)
        {
            if (waitingScheme != null && url.StartsWith(waitingScheme + ":", StringComparison.Ordinal)) Finish(url);
        }

        /// <summary>The address the sign-in came back to, or `error:cancelled` / `error:<reason>`.</summary>
        internal static void Finish(string result)
        {
            if (!result.StartsWith("error:", StringComparison.Ordinal) && !BelongsToWaiting(result)) return;
            var finished = pending;
            pending = null;
            waitingScheme = null;
            waitingState = null;
            if (finished == null) return;
            if (result == "error:cancelled") finished.TrySetCanceled();
            else if (result.StartsWith("error:", StringComparison.Ordinal)) finished.TrySetException(new KrtuworksException("sign_in_failed", $"The sign-in did not finish: {result.Substring(6)}"));
            else finished.TrySetResult(result);
        }

        /// <summary>A return address is the waiting sign-in's unless it carries another sign-in's `state`.</summary>
        internal static bool BelongsToWaiting(string address)
        {
            var state = StateOf(address);
            return waitingState == null || state == null || state == waitingState;
        }

        /// <summary>The `state` in an address's query, or null.</summary>
        internal static string? StateOf(string address)
        {
            var start = address.IndexOf('?');
            if (start < 0) return null;
            var end = address.IndexOf('#', start);
            var query = end < 0 ? address.Substring(start + 1) : address.Substring(start + 1, end - start - 1);
            foreach (var part in query.Split('&'))
            {
                if (part.StartsWith("state=", StringComparison.Ordinal)) return Uri.UnescapeDataString(part.Substring(6).Replace('+', ' '));
            }
            return null;
        }

        /// <summary>The app came back to the foreground: on Android, no address soon after means the browser was closed.</summary>
        internal static void Resumed()
        {
            if (Application.platform != RuntimePlatform.Android || pending == null) return;
            _ = CancelWithoutLink(pending);
        }

        static async Task CancelWithoutLink(TaskCompletionSource<string> waiting)
        {
            await KrtuworksRunner.Delay(TimeSpan.FromMilliseconds(NoLinkAfterResumeMs), CancellationToken.None);
            if (pending == waiting) Finish("error:cancelled");
        }
    }
}
