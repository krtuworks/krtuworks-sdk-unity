#nullable enable
using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.Scripting;

namespace Krtuworks.Unity
{
    /// <summary>
    /// The hidden object that tells the SDK what happens to the app: going to the background and back (the
    /// session's active time, writing what waits), the network coming back, quitting (the level in play ends
    /// as a quit, the session ends; quitting waits up to two seconds for it), and the iOS sign-in session's
    /// answer (the native plugin sends it here by name). It also times the SDK's waits, frame by frame
    /// (<see cref="Delay"/>): WebGL has no threads, so <c>Task.Delay</c>'s timer would never fire there.
    /// </summary>
    [Preserve]
    public sealed class KrtuworksRunner : MonoBehaviour
    {
        internal const string ObjectName = "KrtuworksRunner";
        const float QuitWaitSeconds = 2f;

        KrtuworksClient? client;
        NetworkReachability reachability;
        float nextReachabilityCheck;
        bool shutDown;
        float quitAsked = -1;

        static KrtuworksRunner? instance;

        sealed class Wait
        {
            internal long DueMs;
            internal TaskCompletionSource<bool> Done = new TaskCompletionSource<bool>();
            internal CancellationTokenRegistration Registration;
        }

        static readonly Stopwatch Clock = Stopwatch.StartNew();
        static readonly List<Wait> Waits = new List<Wait>();

        /// <summary>The SDK's wait: completes on the first frame after `span` has passed (or when `cancel` fires).</summary>
        internal static Task Delay(TimeSpan span, CancellationToken cancel)
        {
            if (cancel.IsCancellationRequested) return Task.FromCanceled(cancel);
            var wait = new Wait { DueMs = Clock.ElapsedMilliseconds + (long)span.TotalMilliseconds };
            if (cancel.CanBeCanceled) wait.Registration = cancel.Register(() => wait.Done.TrySetCanceled(cancel));
            lock (Waits) Waits.Add(wait);
            return wait.Done.Task;
        }

        static void FinishDueWaits()
        {
            var now = Clock.ElapsedMilliseconds;
            List<Wait> due;
            lock (Waits)
            {
                due = Waits.FindAll(w => w.DueMs <= now || w.Done.Task.IsCompleted);
                Waits.RemoveAll(w => w.DueMs <= now || w.Done.Task.IsCompleted);
            }
            foreach (var wait in due)
            {
                wait.Registration.Dispose();
                wait.Done.TrySetResult(true);
            }
        }

        internal static KrtuworksRunner Create()
        {
            if (instance != null) return instance;
            var host = new GameObject(ObjectName) { hideFlags = HideFlags.HideInHierarchy };
            DontDestroyOnLoad(host);
            return instance = host.AddComponent<KrtuworksRunner>();
        }

        internal void Attach(KrtuworksClient attached)
        {
            client = attached;
            reachability = Application.internetReachability;
            Application.wantsToQuit += WantsToQuit;
            Application.deepLinkActivated += MobileSignIn.DeepLink;
        }

        void OnDestroy()
        {
            Application.wantsToQuit -= WantsToQuit;
            Application.deepLinkActivated -= MobileSignIn.DeepLink;
        }

        void OnApplicationPause(bool paused)
        {
            client?.SetForeground(!paused);
            if (!paused) MobileSignIn.Resumed();
        }

        void Update()
        {
            FinishDueWaits();
            if (client == null) return;
            if (quitAsked >= 0 && (shutDown || Time.realtimeSinceStartup - quitAsked > QuitWaitSeconds))
            {
                quitAsked = -1;
                shutDown = true;
#if UNITY_EDITOR
                // Leaving Play mode: Application.Quit does nothing in the Editor.
                UnityEditor.EditorApplication.isPlaying = false;
#else
                Application.Quit();
#endif
                return;
            }
            if (Time.realtimeSinceStartup < nextReachabilityCheck) return;
            nextReachabilityCheck = Time.realtimeSinceStartup + 2f;
            var now = Application.internetReachability;
            if (reachability == NetworkReachability.NotReachable && now != NetworkReachability.NotReachable) client.Online();
            reachability = now;
        }

        /// <summary>The frame being shown, as a JPEG: taken at the frame's end, once everything is drawn.</summary>
        internal Task<byte[]> CaptureFrame(int quality)
        {
            var done = new TaskCompletionSource<byte[]>();
            StartCoroutine(Capture(done, quality));
            return done.Task;
        }

        static IEnumerator Capture(TaskCompletionSource<byte[]> done, int quality)
        {
            yield return new WaitForEndOfFrame();
            Texture2D? frame = null;
            try
            {
                frame = ScreenCapture.CaptureScreenshotAsTexture();
                done.TrySetResult(frame.EncodeToJPG(quality));
            }
            catch (Exception e)
            {
                done.TrySetException(e);
            }
            finally
            {
                if (frame != null) Destroy(frame);
            }
        }

        /// <summary>The first quit request starts the shutdown and waits for it (at most two seconds); the next one goes through.</summary>
        bool WantsToQuit()
        {
            if (shutDown || client == null) return true;
            if (quitAsked < 0)
            {
                quitAsked = Time.realtimeSinceStartup;
                _ = Shutdown();
            }
            return false;
        }

        async Task Shutdown()
        {
            try { await client!.ShutdownAsync(); }
            catch (Exception) { /* quitting anyway */ }
            shutDown = true;
        }

        /// <summary>Called by the iOS plugin (UnitySendMessage) with the address the sign-in came back to, or `error:<reason>`.</summary>
        [Preserve]
        public void OnAuthSession(string result) => MobileSignIn.Finish(result);
    }
}
