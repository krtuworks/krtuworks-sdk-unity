#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Krtuworks.Internal;
using Newtonsoft.Json.Linq;
using NUnit.Framework;

namespace Krtuworks.Tests
{
    public class PositionTests
    {
        const string Terms = "TERMS:v1 PRIVACY:v1";

        [Test]
        public void ASamplerTellsOneSamplePerIntervalWithTheSecondsSinceTheOneBefore_ControlReportsInsideTheIntervalTellNothing()
        {
            var s = new PositionSampler(10);
            Assert.That(s.Due(100), Is.Null, "the first report only starts the clock");
            Assert.That(s.Due(104), Is.Null);
            Assert.That(s.Due(109.9), Is.Null);
            Assert.That(s.Due(110), Is.EqualTo(10));
            Assert.That(s.Due(111), Is.Null);
            Assert.That(s.Due(122.4), Is.EqualTo(12), "a late frame carries the whole time since the last sample");
        }

        [Test]
        public void TimeTheGameDidNotReportBelongsToNoPosition_ControlAGapWithinThreeIntervalsIsTold()
        {
            var s = new PositionSampler(10);
            s.Due(0);
            Assert.That(s.Due(31), Is.Null, "over three intervals: the clock starts again");
            Assert.That(s.Due(41), Is.EqualTo(10));
            Assert.That(s.Due(70), Is.EqualTo(29));
            s.Reset();
            Assert.That(s.Due(200), Is.Null, "after a reset the next report starts the clock");
            Assert.That(s.Due(150), Is.Null, "a clock that went back starts again");
            Assert.That(s.Due(160), Is.EqualTo(10));
        }

        [Test]
        public void TheIntervalIsWholeSecondsOneToSixHundredTenByDefault()
        {
            var server = new FakeServer();
            Assert.That(Resolved.From(server.Options()).PositionSampleSeconds, Is.EqualTo(10));
            Assert.That(Resolved.From(server.Options(extra: o => o.PositionSampleSeconds = 600)).PositionSampleSeconds, Is.EqualTo(600));
            Assert.That(Resolved.From(server.Options(extra: o => o.PositionSampleSeconds = 1)).PositionSampleSeconds, Is.EqualTo(1));
            foreach (var bad in new[] { 0, 601, -5 })
                Assert.Throws<ArgumentException>(() => Resolved.From(server.Options(extra: o => o.PositionSampleSeconds = bad)), $"{bad}");
        }

        [Test]
        public async Task AGameSendsAPositionSampleWithItsSecondsAndPlaceUnderThePositionsModule_ControlPositionsOffOrAnAppSendsNone()
        {
            var at = new EventPosition { Space = "world", Area = "Forest", X = 12.5, Y = 40, Z = 1 };
            foreach (var (name, modules, type, kept) in new (string, JObject, string?, bool)[]
            {
                ("game", new JObject { ["customEvents"] = false }, null, true),
                ("positions off", new JObject { ["positions"] = false, ["combat"] = true }, null, false),
                ("an app", new JObject(), "MOBILE_APP", false),
            })
            {
                var server = new FakeServer();
                server.GuestRoutes();
                server.ConfigRoute(new JObject { ["modules"] = modules });
                var sent = new List<JObject>();
                server.AcceptEvents(sent);
                var config = FakeServer.App;
                config.Type = type;
                var krtu = await KrtuworksClient.InitializeAsync(server.Options(extra: o =>
                {
                    o.Config = config;
                    o.PositionSampleSeconds = 1;
                }));
                await krtu.RefreshConfigAsync();
                await krtu.SignInAsGuestAsync("2000-01-01", Terms);
                await krtu.ReadyAsync();
                Assert.That(krtu.SamplePosition(at), Is.False, $"{name}: the first report starts the clock");
                Assert.That(krtu.SamplePosition(at), Is.False, $"{name}: inside the interval");
                await Task.Delay(1100);
                Assert.That(krtu.SamplePosition(at), Is.EqualTo(kept), name);
                await krtu.FlushAsync();
                var samples = sent.Where(e => (string?)e["type"] == "position_sample").ToList();
                if (kept)
                {
                    Assert.That(samples.Count, Is.EqualTo(1));
                    Assert.That(samples[0]["position"]!.ToString(Newtonsoft.Json.Formatting.None), Is.EqualTo(JObject.FromObject(new { space = "world", area = "Forest", x = 12.5, y = 40d, z = 1d }).ToString(Newtonsoft.Json.Formatting.None)));
                    Assert.That((int)samples[0]["data"]!["seconds"]!, Is.InRange(1, 2));
                    Assert.That(samples[0]["data"]!["level"], Is.Null, "no level in play");
                }
                else Assert.That(samples, Is.Empty, name);
                await krtu.ShutdownAsync();
            }
        }

        [Test]
        public async Task APositionWithoutAnAreaOrWithANumberThatIsNotFiniteIsRefused_ControlAScreenPositionIsTaken()
        {
            var server = new FakeServer();
            server.GuestRoutes();
            server.ConfigRoute(new JObject());
            var krtu = await KrtuworksClient.InitializeAsync(server.Options());
            Assert.Throws<ArgumentException>(() => krtu.SamplePosition(new EventPosition { Space = "world", Area = "", X = 1, Y = 1 }));
            Assert.Throws<ArgumentException>(() => krtu.SamplePosition(new EventPosition { Space = "world", Area = "Forest", X = double.NaN, Y = 1 }));
            Assert.Throws<ArgumentException>(() => krtu.SamplePosition(new EventPosition { Space = "world", Area = "Forest", X = 1, Y = 1, Z = double.PositiveInfinity }));
            Assert.Throws<ArgumentException>(() => krtu.SamplePosition(new EventPosition { Space = "map", Area = "Forest", X = 1, Y = 1 }));
            Assert.That(krtu.SamplePosition(new EventPosition { Space = "screen", Area = "Menu", X = 1, Y = 1 }), Is.False);
            await krtu.ShutdownAsync();
        }
    }
}
