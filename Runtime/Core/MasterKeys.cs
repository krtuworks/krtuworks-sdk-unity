#nullable enable
using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Krtuworks.Internal;

namespace Krtuworks
{
    /// <summary>Windows: the sealing key protected with DPAPI for this Windows user (and this app), kept in a file.</summary>
    public sealed class DpapiMasterKey : IMasterKey
    {
        readonly string path;
        readonly byte[] entropy;

        public DpapiMasterKey(string path, string appId)
        {
            this.path = path;
            entropy = Encoding.UTF8.GetBytes($"krtuworks-sdk:{appId}");
        }

        public byte[]? GetOrCreate()
        {
            try
            {
                if (File.Exists(path))
                {
                    var opened = Native.DpapiUnprotect(File.ReadAllBytes(path), entropy);
                    if (opened != null && opened.Length == 32) return opened;
                }
                // None yet, or sealed for another user or machine (copied here): a new key; the old files no longer open.
                var key = Ids.RandomBytes(32);
                var sealedKey = Native.DpapiProtect(key, entropy);
                if (sealedKey == null) return null;
                AtomicFile.Write(path, sealedKey);
                return key;
            }
            catch (Exception e) when (e is DllNotFoundException || e is EntryPointNotFoundException) { return null; }
        }
    }

    /// <summary>macOS: the sealing key as a generic password in the user's login Keychain (service `com.krtuworks.sdk`, account = the app).</summary>
    /// <remarks>The Keychain trusts the app that made the item by its code signature: a build signed with a Developer ID keeps that trust across updates; an unsigned build asks the player again after each update.</remarks>
    public sealed class MacKeychainMasterKey : IMasterKey
    {
        const string Service = "com.krtuworks.sdk";
        readonly string account;

        public MacKeychainMasterKey(string appId)
        {
            account = appId;
        }

        public byte[]? GetOrCreate()
        {
            try
            {
                var found = Native.KeychainFind(Service, account);
                if (found != null)
                {
                    var key = Ids.FromBase64Url(found);
                    if (key.Length == 32) return key;
                }
                var made = Ids.RandomBytes(32);
                var status = Native.KeychainAdd(Service, account, Ids.Base64Url(made));
                if (status == Native.ErrSecDuplicateItem)
                {
                    var again = Native.KeychainFind(Service, account);
                    return again == null ? null : Ids.FromBase64Url(again);
                }
                return status == 0 ? made : null;
            }
            catch (Exception e) when (e is DllNotFoundException || e is EntryPointNotFoundException) { return null; }
        }
    }

    /// <summary>
    /// Linux and Steam Deck (the owner chose one way for desktop and game mode alike): the sealing key
    /// is bound to this machine, this user and this app (`/etc/machine-id`, the uid, the app id), and the
    /// sealed files are readable by this user only (0600). Copied to another machine or user, they do not
    /// open. A machine without a machine id keeps a random key in an owner-only file instead.
    /// </summary>
    public sealed class MachineBoundMasterKey : IMasterKey
    {
        static readonly string[] MachineIdFiles = { "/etc/machine-id", "/var/lib/dbus/machine-id" };
        readonly string appId;
        readonly string fallbackPath;

        public MachineBoundMasterKey(string appId, string fallbackPath)
        {
            this.appId = appId;
            this.fallbackPath = fallbackPath;
        }

        public byte[]? GetOrCreate()
        {
            var machine = MachineIdFiles.Where(File.Exists).Select(f => File.ReadAllText(f).Trim()).FirstOrDefault(id => id.Length > 0);
            if (machine != null)
            {
                using var hmac = new HMACSHA256(Encoding.ASCII.GetBytes("krtuworks-sdk:machine-bound-key"));
                return hmac.ComputeHash(Encoding.UTF8.GetBytes($"{machine}\n{User()}\n{appId}"));
            }
            if (File.Exists(fallbackPath))
            {
                var kept = File.ReadAllBytes(fallbackPath);
                if (kept.Length == 32) return kept;
            }
            var key = Ids.RandomBytes(32);
            AtomicFile.Write(fallbackPath, key, 0x180);
            return key;
        }

        /// <summary>This user: the uid, or the user name where libc cannot be reached (the same answer every launch).</summary>
        static string User()
        {
            var uid = Native.Uid();
            return uid.HasValue ? uid.Value.ToString(System.Globalization.CultureInfo.InvariantCulture) : $"name:{Environment.UserName}";
        }
    }
}

namespace Krtuworks.Internal
{
    internal static class AtomicFile
    {
        /// <summary>
        /// A temporary file next to `path` (made with `mode` on Unix, e.g. 0600), flushed, then moved over it:
        /// a crash leaves the old content or the new, never half.
        /// </summary>
        internal static void Write(string path, byte[] bytes, uint? mode = null)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var temp = Path.Combine(Path.GetDirectoryName(path)!, $".{Guid.NewGuid():N}.tmp");
            using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write))
            {
                stream.Write(bytes, 0, bytes.Length);
                stream.Flush(true);
            }
            if (mode.HasValue) Native.ChmodIfUnix(temp, mode.Value);
            try
            {
                if (!File.Exists(path)) File.Move(temp, path);
                else Replace(temp, path);
            }
            catch (IOException) when (File.Exists(path) && File.Exists(temp))
            {
                // Made by another writer between the check and the move.
                Replace(temp, path);
            }
            finally
            {
                if (File.Exists(temp)) File.Delete(temp);
            }
        }

        static void Replace(string temp, string path)
        {
            try { File.Replace(temp, path, null); }
            catch (Exception e) when (e is NotImplementedException || e is PlatformNotSupportedException)
            {
                // A runtime without File.Replace: not atomic here, but never a half-written file.
                File.Delete(path);
                File.Move(temp, path);
            }
        }
    }

    /// <summary>The platform calls the secret stores need, each behind its own library (missing ones throw DllNotFoundException).</summary>
    internal static class Native
    {
        // ── libc: `libc.so.6` on Linux (a bare `libc` may not resolve there under IL2CPP), `libc` on macOS ──
        [DllImport("libc.so.6", EntryPoint = "chmod")]
        static extern int ChmodGlibc(string path, uint mode);

        [DllImport("libc", EntryPoint = "chmod")]
        static extern int ChmodLibc(string path, uint mode);

        [DllImport("libc.so.6", EntryPoint = "getuid")]
        static extern uint GetuidGlibc();

        [DllImport("libc", EntryPoint = "getuid")]
        static extern uint GetuidLibc();

        static bool Unix => Environment.OSVersion.Platform == PlatformID.Unix || Environment.OSVersion.Platform == PlatformID.MacOSX;

        static T? FirstLibc<T>(Func<T> glibc, Func<T> libc) where T : struct
        {
            try { return glibc(); }
            catch (Exception e) when (e is DllNotFoundException || e is EntryPointNotFoundException) { }
            try { return libc(); }
            catch (Exception e) when (e is DllNotFoundException || e is EntryPointNotFoundException) { return null; }
        }

        /// <summary>chmod on Unix; elsewhere (or without libc) the folder's own rights hold.</summary>
        internal static void ChmodIfUnix(string path, uint mode)
        {
            if (Unix) FirstLibc(() => ChmodGlibc(path, mode), () => ChmodLibc(path, mode));
        }

        /// <summary>This process's uid on Unix; null elsewhere or without libc.</summary>
        internal static uint? Uid() => Unix ? FirstLibc(GetuidGlibc, GetuidLibc) : null;

        // ── Windows DPAPI ──
        [StructLayout(LayoutKind.Sequential)]
        struct DataBlob
        {
            public int Size;
            public IntPtr Data;
        }

        const int CryptProtectUiForbidden = 0x1;

        [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        static extern bool CryptProtectData(ref DataBlob dataIn, string? description, ref DataBlob entropy, IntPtr reserved, IntPtr prompt, int flags, out DataBlob dataOut);

        [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        static extern bool CryptUnprotectData(ref DataBlob dataIn, IntPtr description, ref DataBlob entropy, IntPtr reserved, IntPtr prompt, int flags, out DataBlob dataOut);

        [DllImport("kernel32.dll")]
        static extern IntPtr LocalFree(IntPtr memory);

        static byte[]? Dpapi(byte[] input, byte[] entropy, bool protect)
        {
            var inHandle = GCHandle.Alloc(input, GCHandleType.Pinned);
            var entropyHandle = GCHandle.Alloc(entropy, GCHandleType.Pinned);
            try
            {
                var dataIn = new DataBlob { Size = input.Length, Data = inHandle.AddrOfPinnedObject() };
                var extra = new DataBlob { Size = entropy.Length, Data = entropyHandle.AddrOfPinnedObject() };
                var ok = protect
                    ? CryptProtectData(ref dataIn, "Krtuworks SDK", ref extra, IntPtr.Zero, IntPtr.Zero, CryptProtectUiForbidden, out var dataOut)
                    : CryptUnprotectData(ref dataIn, IntPtr.Zero, ref extra, IntPtr.Zero, IntPtr.Zero, CryptProtectUiForbidden, out dataOut);
                if (!ok) return null;
                try
                {
                    var output = new byte[dataOut.Size];
                    Marshal.Copy(dataOut.Data, output, 0, dataOut.Size);
                    return output;
                }
                finally { LocalFree(dataOut.Data); }
            }
            finally
            {
                inHandle.Free();
                entropyHandle.Free();
            }
        }

        internal static byte[]? DpapiProtect(byte[] data, byte[] entropy) => Dpapi(data, entropy, true);

        internal static byte[]? DpapiUnprotect(byte[] data, byte[] entropy) => Dpapi(data, entropy, false);

        // ── macOS Keychain (generic passwords) ──
        const string Security = "/System/Library/Frameworks/Security.framework/Security";
        const string CoreFoundation = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";
        internal const int ErrSecDuplicateItem = -25299;

        [DllImport(Security)]
        static extern int SecKeychainFindGenericPassword(IntPtr keychains, uint serviceLength, byte[] service, uint accountLength, byte[] account, out uint passwordLength, out IntPtr password, out IntPtr item);

        [DllImport(Security)]
        static extern int SecKeychainAddGenericPassword(IntPtr keychain, uint serviceLength, byte[] service, uint accountLength, byte[] account, uint passwordLength, byte[] password, IntPtr item);

        [DllImport(Security)]
        static extern int SecKeychainItemFreeContent(IntPtr attributes, IntPtr data);

        [DllImport(CoreFoundation)]
        static extern void CFRelease(IntPtr value);

        internal static string? KeychainFind(string service, string account)
        {
            var s = Encoding.UTF8.GetBytes(service);
            var a = Encoding.UTF8.GetBytes(account);
            var status = SecKeychainFindGenericPassword(IntPtr.Zero, (uint)s.Length, s, (uint)a.Length, a, out var length, out var data, out var item);
            if (status != 0) return null;
            try
            {
                var bytes = new byte[length];
                Marshal.Copy(data, bytes, 0, (int)length);
                return Encoding.UTF8.GetString(bytes);
            }
            finally
            {
                SecKeychainItemFreeContent(IntPtr.Zero, data);
                if (item != IntPtr.Zero) CFRelease(item);
            }
        }

        internal static int KeychainAdd(string service, string account, string password)
        {
            var s = Encoding.UTF8.GetBytes(service);
            var a = Encoding.UTF8.GetBytes(account);
            var p = Encoding.UTF8.GetBytes(password);
            return SecKeychainAddGenericPassword(IntPtr.Zero, (uint)s.Length, s, (uint)a.Length, a, (uint)p.Length, p, IntPtr.Zero);
        }
    }
}
