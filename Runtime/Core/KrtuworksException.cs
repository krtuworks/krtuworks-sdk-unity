#nullable enable
using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;

namespace Krtuworks
{
    /// <summary>
    /// Every failure the SDK reports. <see cref="Code"/> is the server's code (the server's `error.code`,
    /// Krtuworks ID's OAuth `error`) or one of the SDK's own: `network_error` (no answer), `context_changed`
    /// (the player changed while the call ran; its answer was dropped), `signed_out`, `birth_date_required`.
    /// </summary>
    public sealed class KrtuworksException : Exception
    {
        /// <summary>HTTP status; 0 when nothing came back.</summary>
        public int Status { get; }
        public string Code { get; }
        /// <summary>Seconds to wait before trying again, when the server said so.</summary>
        public double? RetryAfter { get; }
        /// <summary>The rest of the server's answer (`mergedInto`, `current`, `endsAt`, `terms`, `description` …).</summary>
        public JObject Details { get; }

        public KrtuworksException(string code, string message, int status = 0, double? retryAfter = null, JObject? details = null) : base(message)
        {
            Code = code;
            Status = status;
            RetryAfter = retryAfter;
            Details = details ?? new JObject();
        }

        /// <summary>True when the same call can succeed later (no answer, a busy or briefly closed server).</summary>
        public bool Retryable => Status == 0 || Status == 429 || Status >= 500 || Code == "account_merging";

        /// <summary>
        /// Refusals that can lift (a frozen app, a merge or a ban being settled, a module switched off for now): what is queued
        /// for the player waits on this device and is sent when they are gone.
        /// </summary>
        internal static readonly HashSet<string> WaitingCodes = new HashSet<string>
        {
            "app_suspended", "account_merged", "account_merging", "sanctioned", "account_deletion_pending", "switched_off", "module_disabled",
        };

        /// <summary>True when the refusal can lift (<see cref="WaitingCodes"/>): the queued item stays.</summary>
        internal bool Waiting => WaitingCodes.Contains(Code);

        /// <summary>
        /// Raised while the player's credentials were got or refreshed, before the endpoint answered: a refusal of the
        /// token, not of the queued body. Nothing queued is dropped for it.
        /// </summary>
        internal bool AuthLayer { get; set; }

        /// <summary>True when a queue must keep the item this error came for (it says nothing about the body itself).</summary>
        internal bool KeepsQueued => AuthLayer || Waiting;
    }

    /// <summary>How long a queue waits after a failed send.</summary>
    internal static class Retry
    {
        internal const long WaitingMs = 15 * 60_000;

        /// <summary>
        /// The wait before the next try: the server's `Retry-After` when it gave one, else a long wait for a refusal that can
        /// lift, else the queue's own growing backoff (5 s, doubling, at most 5 min; <paramref name="backoffMs"/> keeps it).
        /// </summary>
        internal static long DelayMs(KrtuworksException err, ref long backoffMs)
        {
            backoffMs = Math.Min(300_000, Math.Max(5_000, backoffMs * 2));
            if (err.RetryAfter.HasValue) return (long)(err.RetryAfter.Value * 1000);
            return err.Waiting ? WaitingMs : backoffMs;
        }
    }
}
