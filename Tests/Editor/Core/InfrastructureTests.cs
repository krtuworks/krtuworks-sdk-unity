#nullable enable
using System.Threading.Tasks;
using Krtuworks.Internal;
using NUnit.Framework;

namespace Krtuworks.Tests
{
    public class InfrastructureTests
    {
        [Test]
        public async Task ARunThatFinishedAtOnceIsNotKept_ControlARunningOneIsShared()
        {
            var flight = new SingleFlight<int>();
            var runs = 0;
            Assert.That(await flight.Run(() => Task.FromResult(++runs)), Is.EqualTo(1));
            Assert.That(await flight.Run(() => Task.FromResult(++runs)), Is.EqualTo(2), "a run that finished before it was handed back is not reused");
            var gate = new TaskCompletionSource<int>();
            var a = flight.Run(() =>
            {
                runs++;
                return gate.Task;
            });
            var b = flight.Run(() =>
            {
                runs++;
                return gate.Task;
            });
            Assert.That(b, Is.SameAs(a), "control: a caller during a run joins it");
            gate.SetResult(9);
            Assert.That(await a, Is.EqualTo(9));
            Assert.That(runs, Is.EqualTo(3));
            Assert.That(await flight.Run(() => Task.FromResult(++runs)), Is.EqualTo(4), "after it ends the next caller starts again");
        }
    }
}
