#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using UnityEngine;

namespace Krtuworks.Unity
{
    /// <summary>
    /// Where the secrets (tokens, the device key, the device id) live on each platform: sealed files whose key the platform keeps (Windows DPAPI, macOS Keychain, Android Keystore
    /// in the app's no-backup folder; Linux and Steam Deck a key bound to the machine and user, files 0600),
    /// the iOS Keychain itself (this device only: not in iCloud backups, not moved to a new iPhone, kept
    /// across a reinstall on the same one), the browser's storage on WebGL.
    /// </summary>
    public static class SecretStores
    {
        public static ISecretStore For(string root, string appId)
        {
            switch (Application.platform)
            {
                case RuntimePlatform.WindowsPlayer:
                case RuntimePlatform.WindowsEditor:
                    return new SealedFileStore(Path.Combine(root, "secrets"), new DpapiMasterKey(Path.Combine(root, "secrets.key"), appId));
                case RuntimePlatform.OSXPlayer:
                case RuntimePlatform.OSXEditor:
                    return new SealedFileStore(Path.Combine(root, "secrets"), new MacKeychainMasterKey(appId));
                case RuntimePlatform.IPhonePlayer:
                    return new IosKeychainStore(appId);
                case RuntimePlatform.Android:
                    var noBackup = Path.Combine(AndroidNoBackupDir(), "krtuworks");
                    // On the main thread: the Keystore is reached through JNI, which other threads must attach to first.
                    return new SealedFileStore(Path.Combine(noBackup, "secrets"), new AndroidKeystoreMasterKey(Path.Combine(noBackup, "secrets.key")), offThread: false);
                case RuntimePlatform.WebGLPlayer:
                    return new BrowserSecretStore(Path.Combine(root, "secrets"));
                default:
                    // Linux and Steam Deck: one way for desktop and game mode alike (the owner's choice).
                    return new SealedFileStore(Path.Combine(root, "secrets"), new MachineBoundMasterKey(appId, Path.Combine(root, "secrets.key")));
            }
        }

        /// <summary>Context.getNoBackupFilesDir(): never in Auto Backup or a device-to-device transfer.</summary>
        static string AndroidNoBackupDir()
        {
            using var player = new AndroidJavaClass("com.unity3d.player.UnityPlayer");
            using var activity = player.GetStatic<AndroidJavaObject>("currentActivity");
            using var dir = activity.Call<AndroidJavaObject>("getNoBackupFilesDir");
            return dir.Call<string>("getAbsolutePath");
        }
    }

    /// <summary>WebGL: the page's own storage (there is no keystore on the web; the JS SDK keeps a non-extractable key instead).</summary>
    public sealed class BrowserSecretStore : FileStore, ISecretStore
    {
        public BrowserSecretStore(string directory) : base(directory, offThread: false) { }
    }

    /// <summary>
    /// Android: the sealing key encrypted by an AES key that never leaves the Android Keystore
    /// (Plugins/Android/KrtuworksKeystore.java), kept in the no-backup folder. Restored onto another phone
    /// it cannot be opened (the Keystore key is not there), so the SDK starts as a new device. Where sealing
    /// fails the key is kept as `plain:` text and sealed on a later launch (<see cref="KeystoreKeyFile"/>).
    /// </summary>
    public sealed class AndroidKeystoreMasterKey : IMasterKey
    {
        readonly string path;

        public AndroidKeystoreMasterKey(string path)
        {
            this.path = path;
        }

        public byte[]? GetOrCreate()
        {
            using var keystore = new AndroidJavaClass("com.krtuworks.sdk.KrtuworksKeystore");
            return KeystoreKeyFile.Resolve(path, text => keystore.CallStatic<string>("open", text), secret => keystore.CallStatic<string>("seal", secret));
        }
    }

    /// <summary>iOS: each secret a Keychain item, accessible after the first unlock, this device only (Plugins/iOS/KrtuworksKeychain.mm).</summary>
    public sealed class IosKeychainStore : ISecretStore
    {
        readonly string service;

        public IosKeychainStore(string appId)
        {
            service = $"com.krtuworks.sdk.{appId}";
        }

#if UNITY_IOS && !UNITY_EDITOR
        [DllImport("__Internal")] static extern string KrtuKeychainGet(string service, string key);
        [DllImport("__Internal")] static extern int KrtuKeychainSet(string service, string key, string value);
        [DllImport("__Internal")] static extern int KrtuKeychainDelete(string service, string key);
        [DllImport("__Internal")] static extern string KrtuKeychainKeys(string service);
#else
        static string KrtuKeychainGet(string service, string key) => throw new PlatformNotSupportedException();
        static int KrtuKeychainSet(string service, string key, string value) => throw new PlatformNotSupportedException();
        static int KrtuKeychainDelete(string service, string key) => throw new PlatformNotSupportedException();
        static string KrtuKeychainKeys(string service) => throw new PlatformNotSupportedException();
#endif

        public Task<string?> GetAsync(string key) => Task.FromResult<string?>(KrtuKeychainGet(service, key));

        public Task SetAsync(string key, string value)
        {
            var status = KrtuKeychainSet(service, key, value);
            if (status != 0) throw new KrtuworksException("secret_storage_unavailable", $"The Keychain refused the item ({status}).");
            return Task.CompletedTask;
        }

        public Task DeleteAsync(string key)
        {
            KrtuKeychainDelete(service, key);
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<string>> KeysAsync(string prefix) =>
            Task.FromResult<IReadOnlyList<string>>((KrtuKeychainKeys(service) ?? "").Split('\n').Where(k => k.Length > 0 && k.StartsWith(prefix, StringComparison.Ordinal)).ToList());
    }
}
