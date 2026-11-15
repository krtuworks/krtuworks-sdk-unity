using System;
using System.Threading.Tasks;
using NUnit.Framework;

namespace Krtuworks.Tests
{
    /// <summary>
    /// Assert.ThrowsAsync without blocking. NUnit's own waits for the task on the calling thread; inside the Unity editor that
    /// is the main thread, where the SDK's awaits resume (no ConfigureAwait(false), so game code may touch the engine after an
    /// await), and the test deadlocks. This one awaits. Like NUnit's, the exception must be exactly T.
    /// </summary>
    internal static class AsyncAssert
    {
        public static async Task<T> ThrowsAsync<T>(Func<Task> code, string? message = null) where T : Exception
        {
            try
            {
                await code();
            }
            catch (Exception e) when (e.GetType() == typeof(T))
            {
                return (T)e;
            }
            catch (Exception e)
            {
                Assert.Fail($"Expected {typeof(T).Name} but got {e.GetType().Name}: {e.Message}{(message == null ? "" : " (" + message + ")")}");
            }
            Assert.Fail($"Expected {typeof(T).Name} but nothing was thrown{(message == null ? "" : " (" + message + ")")}");
            throw new InvalidOperationException("unreachable");
        }
    }
}
