#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

namespace Krtuworks
{
    /// <summary>A level, with its world and part when the game has those layers.</summary>
    public sealed class LevelRef
    {
        public string Level = "";
        public string? World;
        public string? Part;

        public LevelRef() { }

        public LevelRef(string level, string? world = null, string? part = null)
        {
            Level = level;
            World = world;
            Part = part;
        }
    }
}

namespace Krtuworks.Internal
{
    /// <summary>
    /// Levels as the progression family tells them, the rules of krtuworks-sdk-js/src/progression.ts:
    /// the game says start, complete, fail, quit, checkpoint and restart; the SDK adds the attempt number
    /// (per level and player, kept across launches), the attempt's wall and active seconds and its deaths. An
    /// attempt still open when the app goes away is told as a quit.
    /// </summary>
    internal sealed class ProgressionTracker
    {
        sealed class Attempt
        {
            internal LevelRef Level = new LevelRef();
            internal int Number;
            internal Stopwatch Since = Stopwatch.StartNew();
            internal long ActiveAtStart;
            internal int Deaths;
        }

        readonly IKrtuStore store;
        readonly string clientId;
        readonly Func<string> owner;
        readonly Func<long> activeSeconds;
        readonly Action<JObject> emit;
        Attempt? current;

        internal ProgressionTracker(IKrtuStore store, string clientId, Func<string> owner, Func<long> activeSeconds, Action<JObject> emit)
        {
            this.store = store;
            this.clientId = clientId;
            this.owner = owner;
            this.activeSeconds = activeSeconds;
            this.emit = emit;
        }

        string Key => $"krtu:{clientId}:levels:{owner()}";
        static string RefOf(LevelRef level) => string.Join("\u0000", level.World ?? "", level.Level, level.Part ?? "");

        static JObject Place(LevelRef level)
        {
            var place = new JObject { ["level"] = level.Level };
            if (!string.IsNullOrEmpty(level.World)) place["world"] = level.World;
            if (!string.IsNullOrEmpty(level.Part)) place["part"] = level.Part;
            return place;
        }

        JObject Times(Attempt a, JObject into)
        {
            into["attempt"] = a.Number;
            into["seconds"] = Math.Round(a.Since.Elapsed.TotalSeconds, 1, MidpointRounding.AwayFromZero);
            into["activeSeconds"] = Math.Max(0, activeSeconds() - a.ActiveAtStart);
            into["deaths"] = a.Deaths;
            return into;
        }

        static JObject With(JObject data, string action)
        {
            data["action"] = action;
            return data;
        }

        internal async Task StartAsync(LevelRef level)
        {
            if (current != null) Finish("quit", null);
            var counts = await Json.Get<Dictionary<string, int>>(store, Key) ?? new Dictionary<string, int>();
            var reference = RefOf(level);
            counts[reference] = (counts.TryGetValue(reference, out var n) ? n : 0) + 1;
            await Json.Put(store, Key, counts);
            current = new Attempt { Level = level, Number = counts[reference], ActiveAtStart = activeSeconds() };
            var data = With(Place(level), "start");
            data["attempt"] = current.Number;
            emit(data);
        }

        /// <summary>Ends the attempt in play: complete, fail or quit.</summary>
        internal void Finish(string action, double? score)
        {
            var a = current;
            if (a == null) return;
            current = null;
            var data = Times(a, With(Place(a.Level), action));
            if (score.HasValue) data["score"] = score.Value;
            emit(data);
        }

        internal void Checkpoint(string name)
        {
            var a = current;
            if (a == null) return;
            var data = Times(a, With(Place(a.Level), "checkpoint"));
            data["checkpoint"] = name;
            emit(data);
        }

        /// <summary>The level starts over: the attempt in play ends as a restart and a new one begins.</summary>
        internal async Task RestartAsync()
        {
            var a = current;
            if (a == null) return;
            current = null;
            emit(Times(a, With(Place(a.Level), "restart")));
            await StartAsync(a.Level);
        }

        /// <summary>A death counts toward the attempt in play; returns where it happened.</summary>
        internal LevelRef? NoteDeath()
        {
            if (current == null) return null;
            current.Deaths++;
            return current.Level;
        }

        internal LevelRef? InPlay => current?.Level;
    }
}
