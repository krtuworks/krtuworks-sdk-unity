#nullable enable
using System;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace Krtuworks
{
    /// <summary>
    /// Reporting another player's profile picture (POST /v1/players/:subject/avatar-report): a reason
    /// from a fixed list and an optional note. Krtuworks reviews the picture; the player who reports is
    /// never told an outcome. One report per player and picture, at most 10 a day.
    /// </summary>
    public static class AvatarReportReasons
    {
        public const string Sexual = "SEXUAL";
        public const string Hate = "HATE";
        public const string Violence = "VIOLENCE";
        public const string Impersonation = "IMPERSONATION";
        public const string Spam = "SPAM";
        public const string Other = "OTHER";
        public static readonly string[] All = { Sexual, Hate, Violence, Impersonation, Spam, Other };
        public const int NoteMax = 300;

        internal static (string Path, JObject Json) Body(string subject, string reason, string? note)
        {
            if (string.IsNullOrEmpty(subject) || subject.Length > 200) throw new KrtuworksException("invalid_request", "Name the player by the subject your app knows them by.", 400);
            if (!All.Contains(reason)) throw new KrtuworksException("invalid_request", $"reason is one of {string.Join(", ", All)}.", 400);
            var trimmed = (note ?? "").Trim();
            if (trimmed.Length > NoteMax) throw new KrtuworksException("invalid_request", $"A note is at most {NoteMax} characters.", 400);
            var json = new JObject { ["reason"] = reason };
            if (trimmed.Length > 0) json["note"] = trimmed;
            return ($"/v1/players/{Uri.EscapeDataString(subject)}/avatar-report", json);
        }
    }
}
