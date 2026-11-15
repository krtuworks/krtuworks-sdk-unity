#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;

namespace Krtuworks
{
    /// <summary>
    /// The store as files: one file per key in `directory` (the name is the key, base64url), written to a
    /// temporary file first and moved into place, so a crash leaves the old value or the new one, never half.
    /// </summary>
    public class FileStore : IKrtuStore
    {
        readonly string directory;
        readonly bool offThread;

        /// <param name="offThread">Reads and writes on the thread pool (false where there are no threads: WebGL).</param>
        public FileStore(string directory, bool offThread = true)
        {
            this.directory = directory;
            this.offThread = offThread;
            Directory.CreateDirectory(directory);
            Internal.Native.ChmodIfUnix(directory, 0x1C0);
        }

        protected string PathOf(string key) => Path.Combine(directory, Internal.Ids.Base64Url(key) + ".v");

        Task<T> Run<T>(Func<T> work) => offThread ? Task.Run(work) : Task.FromResult(work());

        Task Run(Action work)
        {
            if (offThread) return Task.Run(work);
            work();
            return Task.CompletedTask;
        }

        public Task<string?> GetAsync(string key) => Run(() => Read(key));

        public Task SetAsync(string key, string value) => Run(() => Write(key, value));

        public Task DeleteAsync(string key) => Run(() => File.Delete(PathOf(key)));

        public Task<IReadOnlyList<string>> KeysAsync(string prefix) => Run<IReadOnlyList<string>>(() =>
            Directory.GetFiles(directory, "*.v")
                .Select(f => Path.GetFileNameWithoutExtension(f))
                .Select(name =>
                {
                    try { return Encoding.UTF8.GetString(Internal.Ids.FromBase64Url(name)); }
                    catch (FormatException) { return null; }
                })
                .Where(k => k != null && k.StartsWith(prefix, StringComparison.Ordinal))
                .Select(k => k!)
                .ToList());

        /// <summary>The stored text of `key`, or null.</summary>
        protected virtual string? Read(string key)
        {
            var path = PathOf(key);
            return File.Exists(path) ? File.ReadAllText(path, Encoding.UTF8) : null;
        }

        protected virtual void Write(string key, string value) => WriteBytes(PathOf(key), Encoding.UTF8.GetBytes(value));

        /// <summary>The value's file, written whole (this user's only on Unix).</summary>
        protected static void WriteBytes(string path, byte[] bytes) => Internal.AtomicFile.Write(path, bytes, 0x180);
    }

    /// <summary>Where the 32-byte key that seals the secret files lives (the platform's own secret storage).</summary>
    public interface IMasterKey
    {
        /// <summary>The key, made and kept on the first call; null when the platform's storage is not available.</summary>
        byte[]? GetOrCreate();
    }

    /// <summary>
    /// The secrets (tokens, the device key) as sealed files: AES-256-CBC, then HMAC-SHA256 over the key name,
    /// the IV and the ciphertext (a file moved to another key or another device does not open). The sealing
    /// key comes from the platform (<see cref="IMasterKey"/>). A file sealed with another key (another
    /// device's, restored from a backup without its key) is deleted and reads as missing: the SDK starts
    /// over, as a new device. When the platform does not give the key right now, nothing is
    /// deleted: reads are empty and writes refused until it does.
    /// </summary>
    public sealed class SealedFileStore : FileStore, ISecretStore
    {
        readonly IMasterKey master;
        byte[]? encKey;
        byte[]? macKey;

        public SealedFileStore(string directory, IMasterKey master, bool offThread = true) : base(directory, offThread)
        {
            this.master = master;
        }

        bool Keys()
        {
            if (encKey != null) return true;
            var key = master.GetOrCreate();
            if (key == null || key.Length != 32) return false;
            using var hmac = new HMACSHA256(key);
            encKey = hmac.ComputeHash(Encoding.ASCII.GetBytes("krtuworks-sealed-file:enc"));
            macKey = hmac.ComputeHash(Encoding.ASCII.GetBytes("krtuworks-sealed-file:mac"));
            return true;
        }

        byte[] Tag(string key, byte[] iv, byte[] ciphertext)
        {
            using var hmac = new HMACSHA256(macKey!);
            var name = Encoding.UTF8.GetBytes(key);
            var input = new byte[4 + name.Length + iv.Length + ciphertext.Length];
            BitConverter.GetBytes(name.Length).CopyTo(input, 0);
            name.CopyTo(input, 4);
            iv.CopyTo(input, 4 + name.Length);
            ciphertext.CopyTo(input, 4 + name.Length + iv.Length);
            return hmac.ComputeHash(input);
        }

        protected override void Write(string key, string value)
        {
            if (!Keys()) throw new KrtuworksException("secret_storage_unavailable", "The platform's secret storage is not available.");
            using var aes = Aes.Create();
            aes.Key = encKey!;
            aes.GenerateIV();
            aes.Mode = CipherMode.CBC;
            aes.Padding = PaddingMode.PKCS7;
            byte[] ciphertext;
            using (var encryptor = aes.CreateEncryptor())
            {
                var plain = Encoding.UTF8.GetBytes(value);
                ciphertext = encryptor.TransformFinalBlock(plain, 0, plain.Length);
            }
            var tag = Tag(key, aes.IV, ciphertext);
            WriteBytes(PathOf(key), aes.IV.Concat(ciphertext).Concat(tag).ToArray());
        }

        protected override string? Read(string key)
        {
            var path = PathOf(key);
            if (!File.Exists(path)) return null;
            // The platform's storage not answering now (a locked Keychain) is not a reason to lose the secret.
            if (!Keys()) return null;
            var sealedBytes = File.ReadAllBytes(path);
            if (sealedBytes.Length < 16 + 16 + 32)
            {
                File.Delete(path);
                return null;
            }
            var iv = sealedBytes.Take(16).ToArray();
            var ciphertext = sealedBytes.Skip(16).Take(sealedBytes.Length - 48).ToArray();
            var tag = sealedBytes.Skip(sealedBytes.Length - 32).ToArray();
            if (!Internal.Ids.FixedTimeEquals(Tag(key, iv, ciphertext), tag))
            {
                File.Delete(path);
                return null;
            }
            using var aes = Aes.Create();
            aes.Key = encKey!;
            aes.IV = iv;
            aes.Mode = CipherMode.CBC;
            aes.Padding = PaddingMode.PKCS7;
            using var decryptor = aes.CreateDecryptor();
            return Encoding.UTF8.GetString(decryptor.TransformFinalBlock(ciphertext, 0, ciphertext.Length));
        }
    }
}
