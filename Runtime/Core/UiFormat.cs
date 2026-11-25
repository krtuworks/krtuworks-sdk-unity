#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.RegularExpressions;

namespace Krtuworks
{
    /// <summary>The order people in a locale write a date in.</summary>
    public enum DateOrder { Dmy, Mdy, Ymd }

    public enum BirthCheck { Ok, Invalid, Underage }

    /// <summary>What the ready-made screens show about dates, ages, sizes and times.</summary>
    public static class UiFormat
    {
        /// <summary>Krtuworks ID is for people aged 13 or older; the server refuses younger guests the same way.</summary>
        public const int MinAge = 13;
        const int OldestYears = 120;

        static readonly HashSet<string> MdyRegions = new HashSet<string> { "US", "PH", "FM", "MH", "PW", "AS", "GU", "MP", "PR", "VI", "UM" };
        static readonly HashSet<string> YmdLanguages = new HashSet<string> { "zh", "ja", "ko", "hu", "lt", "mn" };
        static readonly HashSet<string> YmdRegions = new HashSet<string> { "CN", "TW", "JP", "KR", "KP", "HU", "LT", "MN" };
        static readonly Regex Region = new Regex("^[A-Za-z]{2}$");

        /// <summary>The order people in <paramref name="locale"/> write a date in (`en-US` month first, `ja` year first, most day first).</summary>
        public static DateOrder DateOrderFor(string locale)
        {
            var parts = locale.Replace('_', '-').Split('-');
            var language = parts[0].ToLowerInvariant();
            var region = "";
            for (var i = 1; i < parts.Length; i++)
            {
                if (!Region.IsMatch(parts[i])) continue;
                region = parts[i].ToUpperInvariant();
                break;
            }
            if (MdyRegions.Contains(region) || (language == "en" && region == "")) return DateOrder.Mdy;
            if (YmdRegions.Contains(region) || YmdLanguages.Contains(language)) return DateOrder.Ymd;
            return DateOrder.Dmy;
        }

        public static int DaysInMonth(int year, int month) => DateTime.DaysInMonth(year, month);

        public static string IsoDate(int year, int month, int day) => $"{year:D4}-{month:D2}-{day:D2}";

        static int[] Parts(string birthDate)
        {
            var parts = birthDate.Split('-');
            return new[] { int.Parse(parts[0], CultureInfo.InvariantCulture), int.Parse(parts[1], CultureInfo.InvariantCulture), int.Parse(parts[2], CultureInfo.InvariantCulture) };
        }

        /// <summary>Whole years between <paramref name="birthDate"/> (`yyyy-MM-dd`) and <paramref name="today"/> (UTC), the way the server counts them.</summary>
        public static int AgeOn(string birthDate, DateTimeOffset today)
        {
            var born = Parts(birthDate);
            var now = today.UtcDateTime;
            return now.Year - born[0] - (now.Month < born[1] || (now.Month == born[1] && now.Day < born[2]) ? 1 : 0);
        }

        /// <summary>The age screen's answer: a real date, no later than today and no more than 120 years back, 13 or older.</summary>
        public static BirthCheck CheckBirth(int year, int month, int day, DateTimeOffset today)
        {
            if (year < 1 || year > 9999 || month < 1 || month > 12 || day < 1 || day > DaysInMonth(year, month)) return BirthCheck.Invalid;
            var age = AgeOn(IsoDate(year, month, day), today);
            if (age < 0 || age > OldestYears) return BirthCheck.Invalid;
            return age < MinAge ? BirthCheck.Underage : BirthCheck.Ok;
        }

        /// <summary>The years the age screen offers: this year back to 120 years ago, newest first.</summary>
        public static IReadOnlyList<int> BirthYears(DateTimeOffset today)
        {
            var now = today.UtcDateTime.Year;
            var years = new List<int>();
            for (var i = 0; i <= OldestYears; i++) years.Add(now - i);
            return years;
        }

        /// <summary>The first day someone born on <paramref name="birthDate"/> is 13 (a 29 February birthday counts from 1 March).</summary>
        public static string TurnsThirteen(string birthDate)
        {
            var born = Parts(birthDate);
            var year = born[0] + MinAge;
            return born[2] > DaysInMonth(year, born[1]) ? IsoDate(year, born[1] + 1, 1) : IsoDate(year, born[1], born[2]);
        }

        static readonly Regex WebpPicture = new Regex("^(https://[^/?#]+/avatars/[0-9a-f]{64}(?:-128)?)\\.webp$");

        /// <summary>A Krtuworks ID profile picture's address as PNG (the engine cannot read webp); any other address as it is.</summary>
        public static string EngineImage(string url)
        {
            var match = WebpPicture.Match(url);
            return match.Success ? match.Groups[1].Value + ".png" : url;
        }

        /// <summary>`12 KB`, `1.4 MB`.</summary>
        public static string FileSize(long bytes)
        {
            if (bytes < 1024) return $"{bytes} B";
            if (bytes < 1024 * 1024) return $"{(long)Math.Floor(bytes / 1024.0 + 0.5)} KB";
            return $"{(bytes / (1024.0 * 1024.0)).ToString("0.0", CultureInfo.InvariantCulture)} MB";
        }

        /// <summary>The time left on a code as `m:ss`, rounded up to the second.</summary>
        public static string Countdown(long remainingMs)
        {
            var seconds = remainingMs <= 0 ? 0 : (remainingMs + 999) / 1000;
            return $"{seconds / 60}:{seconds % 60:D2}";
        }

        static CultureInfo CultureOf(string locale)
        {
            try { return CultureInfo.GetCultureInfo(locale); }
            catch (CultureNotFoundException) { return CultureInfo.InvariantCulture; }
        }

        /// <summary>A date in words, in the locale's order: `1 July 2026`.</summary>
        public static string LongDate(DateTimeOffset at, string locale)
        {
            var pattern = DateOrderFor(locale) == DateOrder.Ymd ? "yyyy MMMM d" : DateOrderFor(locale) == DateOrder.Mdy ? "MMMM d, yyyy" : "d MMMM yyyy";
            return at.ToString(pattern, CultureOf(locale));
        }

        /// <summary>When a save was made: `Today, 21:40`, `Yesterday, 23:15`, else the date and time in <paramref name="locale"/>. Days are the ones of <paramref name="now"/>'s offset.</summary>
        public static string SavedWhen(string iso, DateTimeOffset now, string locale, UiTexts texts)
        {
            if (!DateTimeOffset.TryParse(iso, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed)) return "";
            var at = parsed.ToOffset(now.Offset);
            var culture = CultureOf(locale);
            var time = at.ToString("t", culture);
            var daysAgo = (now.Date - at.Date).Days;
            if (daysAgo == 0) return texts.Get("time.today", ("time", time));
            if (daysAgo == 1) return texts.Get("time.yesterday", ("time", time));
            return at.ToString("g", culture);
        }
    }
}
