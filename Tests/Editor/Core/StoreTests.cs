#nullable enable
using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using NUnit.Framework;

namespace Krtuworks.Tests
{
    public class StoreTests
    {
        string dir = "";

        [SetUp]
        public void MakeDir() => dir = Path.Combine(Path.GetTempPath(), $"krtu-store-{Guid.NewGuid():N}");

        [TearDown]
        public void RemoveDir()
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
        }

        sealed class FixedKey : IMasterKey
        {
            internal byte[]? Key;
            public byte[]? GetOrCreate() => Key;
        }

        static byte[] Key(byte fill) => Enumerable.Repeat(fill, 32).ToArray();

        [Test]
        public async Task AFileStoreKeepsValuesUnderAnyKeyAndListsThemByPrefix()
        {
            var store = new FileStore(dir);
            await store.SetAsync("krtu:app.test.native:events:pw_a/b", "{\"x\":1}");
            await store.SetAsync("krtu:app.test.native:upload:1", "u1");
            await store.SetAsync("krtu:app.test.native:upload:1", "u1-again");
            Assert.That(await store.GetAsync("krtu:app.test.native:events:pw_a/b"), Is.EqualTo("{\"x\":1}"), "a key with a slash is a file name, not a path");
            Assert.That(await store.GetAsync("krtu:app.test.native:upload:1"), Is.EqualTo("u1-again"));
            Assert.That(await store.KeysAsync("krtu:app.test.native:upload:"), Is.EqualTo(new[] { "krtu:app.test.native:upload:1" }));
            await store.DeleteAsync("krtu:app.test.native:upload:1");
            Assert.That(await store.GetAsync("krtu:app.test.native:upload:1"), Is.Null);
            Assert.That(Directory.GetFiles(dir).Where(f => f.EndsWith(".tmp")), Is.Empty, "no temporary file is left");
            Assert.That(await new FileStore(dir).GetAsync("krtu:app.test.native:events:pw_a/b"), Is.EqualTo("{\"x\":1}"), "a new launch reads what the last one wrote");
        }

        [Test]
        public async Task SealedFilesOpenOnlyWithTheirKeyAndUnderTheirName_ControlTheRightKeyOpensThem()
        {
            var master = new FixedKey { Key = Key(1) };
            var store = new SealedFileStore(dir, master);
            await store.SetAsync("krtu:app:tokens", "{\"refreshToken\":\"secret-value\"}");
            var files = Directory.GetFiles(dir, "*.v");
            Assert.That(files.Length, Is.EqualTo(1));
            Assert.That(File.ReadAllText(files[0]), Does.Not.Contain("secret-value"), "nothing readable on disk");
            Assert.That(await new SealedFileStore(dir, new FixedKey { Key = Key(1) }).GetAsync("krtu:app:tokens"), Is.EqualTo("{\"refreshToken\":\"secret-value\"}"), "control: the same key opens it after a relaunch");

            // Moved under another key name: the tag binds the name, so it does not open there.
            var moved = files[0].Replace(Path.GetFileName(files[0]), Krtuworks.Internal.Ids.Base64Url("krtu:app:deviceKey") + ".v");
            File.Copy(files[0], moved);
            Assert.That(await store.GetAsync("krtu:app:deviceKey"), Is.Null);
            Assert.That(File.Exists(moved), Is.False, "a file that does not open is removed: the SDK starts as a new device");

            // A locked key store right now: nothing readable, nothing lost.
            var locked = new SealedFileStore(dir, new FixedKey { Key = null });
            Assert.That(await locked.GetAsync("krtu:app:tokens"), Is.Null);
            Assert.That(File.Exists(files[0]), Is.True, "a missing key never deletes a secret");
            await AsyncAssert.ThrowsAsync<KrtuworksException>(() => locked.SetAsync("krtu:app:tokens", "x"));

            // Another key (another device, a backup restored without its key): the file goes.
            Assert.That(await new SealedFileStore(dir, new FixedKey { Key = Key(2) }).GetAsync("krtu:app:tokens"), Is.Null);
            Assert.That(File.Exists(files[0]), Is.False);
        }

        [Test]
        public async Task ATamperedSealedFileIsRefused()
        {
            var store = new SealedFileStore(dir, new FixedKey { Key = Key(3) });
            await store.SetAsync("k", "value");
            var file = Directory.GetFiles(dir, "*.v").Single();
            var bytes = File.ReadAllBytes(file);
            bytes[20] ^= 1;
            File.WriteAllBytes(file, bytes);
            Assert.That(await store.GetAsync("k"), Is.Null);
            await store.SetAsync("k", "again");
            Assert.That(await store.GetAsync("k"), Is.EqualTo("again"), "control: a new value is kept");
        }

        [Test]
        public void TheMachineBoundKeyIsTheSameForThisAppOnThisMachine_AnotherAppGetsAnother()
        {
            if (!File.Exists("/etc/machine-id") && !File.Exists("/var/lib/dbus/machine-id")) Assert.Ignore("no machine id here");
            var a = new MachineBoundMasterKey("app_aaaaaaaaaaaaaaaaaaaa", Path.Combine(dir, "k")).GetOrCreate();
            var again = new MachineBoundMasterKey("app_aaaaaaaaaaaaaaaaaaaa", Path.Combine(dir, "k")).GetOrCreate();
            var other = new MachineBoundMasterKey("app_bbbbbbbbbbbbbbbbbbbb", Path.Combine(dir, "k")).GetOrCreate();
            Assert.That(a, Has.Length.EqualTo(32));
            Assert.That(again, Is.EqualTo(a), "a relaunch (or the other Steam Deck mode) finds the same key");
            Assert.That(other, Is.Not.EqualTo(a));
            Assert.That(File.Exists(Path.Combine(dir, "k")), Is.False, "nothing written when the machine has an id");
        }

        // ── The Android key file ─────────────────────────────────────────────

        static string? FakeSeal(string secret) => $"sealed({secret})";
        static string? FakeOpen(string text) => text.StartsWith("sealed(", StringComparison.Ordinal) ? text.Substring(7, text.Length - 8) : "";

        [Test]
        public void AKeyKeptPlainBecauseSealingFailedIsSealedOnALaterLaunchAsTheSameKey_ControlAKeyAlreadySealedIsOpenedAndLeftAlone()
        {
            var path = Path.Combine(dir, "secrets.key");
            var first = KeystoreKeyFile.Resolve(path, FakeOpen, _ => null)!;
            Assert.That(File.ReadAllText(path), Does.StartWith("plain:"), "sealing failed: the key is kept as it is");

            Assert.That(KeystoreKeyFile.Resolve(path, FakeOpen, _ => null), Is.EqualTo(first), "still failing: the same plain key, nothing thrown, no new key");
            Assert.That(File.ReadAllText(path), Does.StartWith("plain:"));

            Assert.That(KeystoreKeyFile.Resolve(path, FakeOpen, FakeSeal), Is.EqualTo(first), "sealing works now: the same key");
            Assert.That(File.ReadAllText(path), Is.EqualTo($"sealed({Convert.ToBase64String(first)})"));

            var sealCalls = 0;
            Assert.That(KeystoreKeyFile.Resolve(path, FakeOpen, secret => { sealCalls++; return FakeSeal(secret); }), Is.EqualTo(first), "control: a sealed key opens as the same key");
            Assert.That(sealCalls, Is.EqualTo(0), "and is not sealed again");
            Assert.That(File.ReadAllText(path), Is.EqualTo($"sealed({Convert.ToBase64String(first)})"));
        }

        [Test]
        public void TheKeystoreFailingWhileOpeningWaitsInsteadOfMakingAKey_ControlAKeyThatCanNeverOpenHereIsReplaced()
        {
            var path = Path.Combine(dir, "secrets.key");
            var first = KeystoreKeyFile.Resolve(path, FakeOpen, FakeSeal)!;
            var kept = File.ReadAllText(path);
            Assert.That(KeystoreKeyFile.Resolve(path, _ => null, FakeSeal), Is.Null);
            Assert.That(File.ReadAllText(path), Is.EqualTo(kept), "the file is untouched");

            var other = KeystoreKeyFile.Resolve(path, _ => "", FakeSeal)!;
            Assert.That(other, Is.Not.EqualTo(first), "restored from another phone: a new key");
            Assert.That(File.ReadAllText(path), Is.Not.EqualTo(kept));
        }
    }
}
