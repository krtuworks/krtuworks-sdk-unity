#nullable enable
using System;
using System.Linq;
using System.Text;
using Krtuworks.Internal;
using NUnit.Framework;

namespace Krtuworks.Tests
{
    /// <summary>The QR encoder against a verified reference: the sha256 of the matrix for every forced mask (error correction M, byte mode).</summary>
    public class QrTests
    {
        static string Fingerprint(bool[][] modules) =>
            Ids.Sha256Hex(Encoding.UTF8.GetBytes(string.Join("\n", modules.Select(row => new string(row.Select(dark => dark ? '1' : '0').ToArray()))))).Substring(0, 16);

        static readonly (string Name, string Text, int Version, string[] Masks)[] Fixtures =
        {
            ("link", "https://id.krtuworks.com/link#c=K7QM-2RXD-9P", 4, new[] { "f9c50fe9088c3572", "b6e7a16ca11f3c84", "cea333a5780f76ca", "cb1b5935f1e3ad24", "b3c7640c41ba685a", "7ba2b1a0797877cd", "10928c3f7ab8e4df", "8f16f4825332ad7b" }),
            ("v8", new string('x', 130), 8, new[] { "675c371f2b023a5b", "b091c47086f986ef", "e1bb8bfd76849792", "3b5f24d342462edf", "84c18a91a52dc990", "b0e02eb1096e0392", "99cb799f4f5f0724", "ca1badab5b211b28" }),
            ("v10", new string('z', 213), 10, new[] { "e60b477236880f2d", "f6862dc828d614e5", "e1105e1cff3f9f00", "c962fd810ceb5d77", "c3fabc551e17ac3b", "2b872f6508c75ad4", "165da8211b0de085", "253da557eea8ceda" }),
            ("utf8", "ğüşıöç İ", 2, new[] { "abf77a489ec173a7", "fb16349eedd851c1", "fdb7072fcba77767", "70cf365734bfa440", "68d7b6d5c77eee1d", "a854c5bf0b18603d", "c10a41d4121a6f36", "41f1ec254977c3c9" }),
        };

        [Test]
        public void EveryForcedMaskMatchesTheVerifiedEncoderModuleForModule()
        {
            foreach (var fixture in Fixtures)
            {
                for (var mask = 0; mask < 8; mask++)
                {
                    var modules = QrCode.Modules(fixture.Text, mask);
                    Assert.That(modules.Length, Is.EqualTo(fixture.Version * 4 + 17), $"{fixture.Name} is a version {fixture.Version} code");
                    Assert.That(Fingerprint(modules), Is.EqualTo(fixture.Masks[mask]), $"{fixture.Name}, mask {mask}");
                }
            }
        }

        [Test]
        public void AnotherMaskOrAnotherTextIsAnotherMatrix_ControlTheFingerprintCanTellThemApart()
        {
            Assert.That(Fingerprint(QrCode.Modules(Fixtures[0].Text, 1)), Is.Not.EqualTo(Fixtures[0].Masks[0]));
            Assert.That(Fingerprint(QrCode.Modules(Fixtures[0].Text + "x", 0)), Is.Not.EqualTo(Fixtures[0].Masks[0]));
            Assert.That(Fingerprint(QrCode.Modules(Fixtures[0].Text, 0)), Is.EqualTo(Fixtures[0].Masks[0]));
        }

        [Test]
        public void TheAutomaticMaskIsOneOfTheEightForcedMatrices()
        {
            foreach (var fixture in Fixtures)
            {
                var chosen = Fingerprint(QrCode.Modules(fixture.Text));
                Assert.That(fixture.Masks, Does.Contain(chosen), fixture.Name);
            }
        }

        [Test]
        public void TooMuchDoesNotFit_ControlTheLargestThatDoes()
        {
            var tooLong = new string('x', 214);
            Assert.Throws<ArgumentOutOfRangeException>(() => QrCode.Modules(tooLong));
            Assert.That(QrCode.Modules(new string('z', 213)).Length, Is.EqualTo(57), "213 bytes is a version 10 code");
            Assert.That(QrCode.Modules("").Length, Is.EqualTo(21), "the smallest is version 1");
        }

        [Test]
        public void ARequestedVersionIsKeptWhenTheTextFitsIt()
        {
            Assert.That(QrCode.Modules("hi", null, 3).Length, Is.EqualTo(29));
            Assert.That(QrCode.Modules("hi").Length, Is.EqualTo(21), "control: without the request the smallest fitting version");
        }
    }
}
