#nullable enable
using System;
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
    }
}
