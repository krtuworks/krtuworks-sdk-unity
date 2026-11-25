#nullable enable
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.UIElements;

namespace Krtuworks.Unity
{
    /// <summary>One screen drawn: its card, what to focus first, what Cancel / Back answers, and what to free when it goes.</summary>
    internal sealed class DrawnScreen
    {
        internal VisualElement Card = new VisualElement();
        internal VisualElement? Focus;
        internal UiAnswer? Back;
        internal readonly List<Texture2D> Textures = new List<Texture2D>();
        internal IVisualElementScheduledItem? Ticker;
    }

    /// <summary>What every screen needs from its host.</summary>
    internal sealed class DrawContext
    {
        internal UiTexts Texts = new UiTexts();
        internal Action<UiAnswer> Answer = _ => { };
        /// <summary>Opens an address in the system browser; null where there is none.</summary>
        internal Action<string>? OpenUrl;
        internal Func<long> NowMs = () => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
    }

    /// <summary>The screens as UI Toolkit elements, styled by the package's style sheet (classes <c>kw-*</c>).</summary>
    internal static class KrtuworksUiScreens
    {
        static readonly string[] Orders = { "dmy", "mdy", "ymd" };

        /// <summary>A click's work that ends on its own: a failure goes to the Console rather than nowhere.</summary>
        internal static async Task Quietly(Task work)
        {
            try { await work; }
            catch (Exception e) { Debug.LogException(e); }
        }

        internal static Label Text(string text, params string[] classNames)
        {
            var label = new Label(text);
            foreach (var className in classNames) label.AddToClassList(className);
            return label;
        }

        internal static Button MakeButton(string text, Action click, bool ghost = false)
        {
            var button = new Button { text = text };
            button.AddToClassList("kw-button");
            if (ghost) button.AddToClassList("kw-button--ghost");
            button.clicked += click;
            return button;
        }

        static VisualElement Box(string className)
        {
            var box = new VisualElement();
            box.AddToClassList(className);
            return box;
        }

        static VisualElement Card(bool wide)
        {
            var card = Box("kw-card");
            if (wide) card.AddToClassList("kw-card--wide");
            return card;
        }

        /// <summary>The sentence with its marks drawn as links (or plain words where nothing can be opened).</summary>
        static VisualElement Sentence(DrawContext c, string key, Dictionary<string, (string Url, string Text)> links)
        {
            var row = Box("kw-sentence");
            foreach (var part in c.Texts.Segments(key))
            {
                if (part.Mark == null)
                {
                    row.Add(Text(part.Text!, "kw-sentence-text"));
                    continue;
                }
                if (!links.TryGetValue(part.Mark, out var link))
                {
                    row.Add(Text("{" + part.Mark + "}", "kw-sentence-text"));
                    continue;
                }
                if (c.OpenUrl == null)
                {
                    row.Add(Text(link.Text, "kw-sentence-text"));
                    continue;
                }
                var open = c.OpenUrl;
                var button = new Button { text = link.Text };
                button.AddToClassList("kw-link");
                button.clicked += () => open(link.Url);
                row.Add(button);
            }
            return row;
        }

        /// <summary>Left and Right change the chosen entry (a gamepad's way through a list); Up and Down move on.</summary>
        static void Steps(DropdownField field)
        {
            field.RegisterCallback<NavigationMoveEvent>(e =>
            {
                var step = e.direction == NavigationMoveEvent.Direction.Right ? 1 : e.direction == NavigationMoveEvent.Direction.Left ? -1 : 0;
                if (step == 0) return;
                field.index = Math.Max(0, Math.Min(field.choices.Count - 1, field.index + step));
                e.StopPropagation();
            });
        }

        internal static DrawnScreen Draw(UiScreen screen, DrawContext c)
        {
            switch (screen)
            {
                case AgeScreen age: return Age(age, c);
                case TermsScreen terms: return Terms(terms, c);
                case UnderageScreen _: return Underage(c);
                case ConflictScreen conflict: return Conflict(conflict, c);
                case ErrorScreen error: return Error(error, c);
                case WaitingScreen waiting: return Waiting(waiting, c);
                case DeviceScreen device: return Device(device, c);
                default: throw new ArgumentException($"no drawing for {screen.GetType().Name}", nameof(screen));
            }
        }

        static Dictionary<string, (string Url, string Text)> LegalLinks(DrawContext c, string termsUrl, string privacyUrl) => new Dictionary<string, (string, string)>
        {
            ["terms"] = (termsUrl, c.Texts.Get("age.termsLink")),
            ["privacy"] = (privacyUrl, c.Texts.Get("age.privacyLink")),
        };

        static DrawnScreen Age(AgeScreen screen, DrawContext c)
        {
            var drawn = new DrawnScreen { Card = Card(false) };
            drawn.Card.Add(Text(c.Texts.Get("age.title"), "kw-title"));
            if (screen.Problem != AgeProblem.None) drawn.Card.Add(Text(c.Texts.Get(screen.Problem == AgeProblem.Invalid ? "age.invalid" : "age.acceptNeeded"), "kw-problem"));
            var days = new List<string> { "" };
            for (var d = 1; d <= 31; d++) days.Add(d.ToString());
            var months = new List<string> { "" };
            for (var m = 1; m <= 12; m++) months.Add(c.Texts.Get($"month.{m}"));
            var years = new List<string> { "" };
            foreach (var y in screen.Years) years.Add(y.ToString());
            var day = new DropdownField(days, 0);
            var month = new DropdownField(months, 0);
            var year = new DropdownField(years, 0);
            foreach (var field in new[] { day, month, year })
            {
                field.AddToClassList("kw-dropdown");
                Steps(field);
            }
            void TrimDays()
            {
                var max = year.index > 0 && month.index > 0 ? UiFormat.DaysInMonth(screen.Years[year.index - 1], month.index) : 31;
                if (day.index > max) day.index = 0;
            }
            year.RegisterValueChangedCallback(_ => TrimDays());
            month.RegisterValueChangedCallback(_ => TrimDays());
            var fields = new Dictionary<char, VisualElement>();
            foreach (var pair in new[] { ('d', "age.day", day), ('m', "age.month", month), ('y', "age.year", year) })
            {
                var box = Box("kw-field");
                box.Add(Text(c.Texts.Get(pair.Item2), "kw-caption"));
                box.Add(pair.Item3);
                fields[pair.Item1] = box;
            }
            var row = Box("kw-row");
            var order = Orders[(int)screen.Order];
            for (var i = 0; i < order.Length; i++)
            {
                var box = fields[order[i]];
                if (i == order.Length - 1) box.AddToClassList("kw-field--last");
                row.Add(box);
            }
            drawn.Card.Add(row);
            var accept = new Toggle();
            accept.AddToClassList("kw-check");
            var agree = Box("kw-agree");
            agree.Add(accept);
            agree.Add(Sentence(c, "age.accept", LegalLinks(c, screen.TermsUrl, screen.PrivacyUrl)));
            drawn.Card.Add(agree);
            drawn.Card.Add(MakeButton(c.Texts.Get("age.continue"), () =>
                c.Answer(UiAnswer.Birth(year.index > 0 ? screen.Years[year.index - 1] : 0, month.index, day.index, accept.value))));
            if (screen.SignIn) drawn.Card.Add(MakeButton(c.Texts.Get("age.signIn"), () => c.Answer(UiAnswer.SignIn), ghost: true));
            drawn.Focus = order[0] == 'd' ? day : order[0] == 'm' ? month : year;
            return drawn;
        }

        static DrawnScreen Terms(TermsScreen screen, DrawContext c)
        {
            var drawn = new DrawnScreen { Card = Card(false) };
            drawn.Card.Add(Text(c.Texts.Get("terms.title"), "kw-title"));
            var body = Sentence(c, "terms.body", LegalLinks(c, screen.TermsUrl, screen.PrivacyUrl));
            body.style.marginBottom = 16;
            drawn.Card.Add(body);
            var accept = MakeButton(c.Texts.Get("terms.accept"), () => c.Answer(UiAnswer.Accept));
            drawn.Card.Add(accept);
            drawn.Focus = accept;
            return drawn;
        }

        static DrawnScreen Underage(DrawContext c)
        {
            var drawn = new DrawnScreen { Card = Card(false), Back = UiAnswer.Offline };
            drawn.Card.Add(Text(c.Texts.Get("underage.title"), "kw-title"));
            drawn.Card.Add(Text(c.Texts.Get("underage.body"), "kw-body"));
            var play = MakeButton(c.Texts.Get("underage.play"), () => c.Answer(UiAnswer.Offline));
            drawn.Card.Add(play);
            drawn.Focus = play;
            return drawn;
        }

        static Texture2D? Picture(byte[]? bytes)
        {
            if (bytes == null) return null;
            var texture = new Texture2D(2, 2);
            if (texture.LoadImage(bytes)) return texture;
            UnityEngine.Object.Destroy(texture);
            return null;
        }

        static Button SaveButton(DrawnScreen drawn, SaveCard card, DrawContext c)
        {
            var button = new Button();
            button.AddToClassList("kw-save");
            var picture = Picture(card.Summary?.Image);
            if (picture != null)
            {
                drawn.Textures.Add(picture);
                var image = new Image { image = picture, scaleMode = ScaleMode.ScaleAndCrop };
                image.AddToClassList("kw-save-image");
                button.Add(image);
            }
            var name = Box("kw-save-name");
            name.Add(Text(card.Label, "kw-save-label"));
            if (card.Newer) name.Add(Text(c.Texts.Get("conflict.newer"), "kw-tag"));
            button.Add(name);
            var lines = new List<string>();
            if (!string.IsNullOrEmpty(card.Summary?.Title)) lines.Add(card.Summary!.Title!);
            if (card.Summary != null) lines.AddRange(card.Summary.Lines);
            if (card.When.Length > 0) lines.Add(card.When);
            lines.Add(card.Size);
            button.Add(Text(string.Join("\n", lines), "kw-meta"));
            return button;
        }

        static DrawnScreen Conflict(ConflictScreen screen, DrawContext c)
        {
            var drawn = new DrawnScreen { Card = Card(true) };
            drawn.Card.Add(Text(c.Texts.Get("conflict.title", ("slot", screen.Slot)), "kw-title"));
            drawn.Card.Add(Text(c.Texts.Get("conflict.body"), "kw-body"));
            var picked = screen.Cards[0].Newer ? 0 : 1;
            var cards = new List<Button>();
            var saves = Box("kw-saves");
            void Mark()
            {
                for (var i = 0; i < cards.Count; i++) cards[i].EnableInClassList("kw-save--picked", i == picked);
            }
            for (var i = 0; i < screen.Cards.Length; i++)
            {
                var index = i;
                var button = SaveButton(drawn, screen.Cards[i], c);
                if (i == screen.Cards.Length - 1) button.AddToClassList("kw-save--last");
                button.clicked += () =>
                {
                    picked = index;
                    Mark();
                };
                cards.Add(button);
                saves.Add(button);
            }
            Mark();
            drawn.Card.Add(saves);
            drawn.Card.Add(MakeButton(c.Texts.Get("conflict.keep"), () => c.Answer(UiAnswer.Keep(picked))));
            drawn.Focus = cards[picked];
            return drawn;
        }

        static DrawnScreen Error(ErrorScreen screen, DrawContext c)
        {
            var drawn = new DrawnScreen { Card = Card(false), Back = UiAnswer.Continue };
            drawn.Card.Add(Text(c.Texts.Get("error.title"), "kw-title"));
            drawn.Card.Add(Text(screen.Message, "kw-problem"));
            if (screen.Retry)
            {
                var retry = MakeButton(c.Texts.Get("error.retry"), () => c.Answer(UiAnswer.Retry));
                drawn.Card.Add(retry);
                drawn.Focus = retry;
            }
            var keepPlaying = MakeButton(c.Texts.Get("error.continue"), () => c.Answer(UiAnswer.Continue), ghost: screen.Retry);
            drawn.Card.Add(keepPlaying);
            drawn.Focus ??= keepPlaying;
            return drawn;
        }

        static DrawnScreen Waiting(WaitingScreen screen, DrawContext c)
        {
            var drawn = new DrawnScreen { Card = Card(false), Back = UiAnswer.Cancel };
            drawn.Card.Add(Text(c.Texts.Get("waiting.title"), "kw-title"));
            drawn.Card.Add(Text(c.Texts.Get("waiting.body"), "kw-body"));
            var reopen = MakeButton(c.Texts.Get("waiting.reopen"), () => c.Answer(UiAnswer.Reopen));
            drawn.Card.Add(reopen);
            drawn.Card.Add(MakeButton(c.Texts.Get("waiting.cancel"), () => c.Answer(UiAnswer.Cancel), ghost: true));
            drawn.Focus = reopen;
            return drawn;
        }

        /// <summary>The QR as a point-filtered texture with its quiet zone: every module is whole pixels, so it stays crisp at any size.</summary>
        static Texture2D QrTexture(bool[][] modules)
        {
            const int quiet = 4;
            var side = modules.Length + 2 * quiet;
            var pixels = new Color32[side * side];
            var light = new Color32(255, 255, 255, 255);
            var dark = new Color32(0, 0, 0, 255);
            for (var i = 0; i < pixels.Length; i++) pixels[i] = light;
            for (var y = 0; y < modules.Length; y++)
            {
                for (var x = 0; x < modules[y].Length; x++)
                {
                    // Texture rows run bottom to top.
                    if (modules[y][x]) pixels[(side - 1 - (y + quiet)) * side + x + quiet] = dark;
                }
            }
            var texture = new Texture2D(side, side, TextureFormat.RGBA32, false) { filterMode = FilterMode.Point, wrapMode = TextureWrapMode.Clamp };
            texture.SetPixels32(pixels);
            texture.Apply();
            return texture;
        }

        static DrawnScreen Device(DeviceScreen screen, DrawContext c)
        {
            var drawn = new DrawnScreen { Card = Card(false), Back = UiAnswer.Cancel };
            drawn.Card.Add(Text(c.Texts.Get("device.title"), "kw-title"));
            if (screen.Expired)
            {
                drawn.Card.Add(Text(c.Texts.Get("device.expired"), "kw-problem"));
                var fresh = MakeButton(c.Texts.Get("device.newCode"), () => c.Answer(UiAnswer.NewCode));
                drawn.Card.Add(fresh);
                drawn.Card.Add(MakeButton(c.Texts.Get("device.cancel"), () => c.Answer(UiAnswer.Cancel), ghost: true));
                drawn.Focus = fresh;
                return drawn;
            }
            drawn.Card.Add(Text(c.Texts.Get("device.go", ("uri", screen.Uri)), "kw-body"));
            var qr = QrTexture(screen.Qr);
            drawn.Textures.Add(qr);
            var scale = Math.Max(1, 220 / qr.width);
            var image = new Image { image = qr, scaleMode = ScaleMode.ScaleToFit };
            image.AddToClassList("kw-qr");
            image.style.width = qr.width * scale;
            image.style.height = qr.height * scale;
            drawn.Card.Add(image);
            drawn.Card.Add(Text(c.Texts.Get("device.enter"), "kw-caption", "kw-center"));
            drawn.Card.Add(Text(screen.Code, "kw-code"));
            var waiting = Text("", "kw-body", "kw-center");
            void Tick() => waiting.text = c.Texts.Get("device.waiting", ("time", UiFormat.Countdown(screen.ExpiresAtMs - c.NowMs())));
            Tick();
            drawn.Card.Add(waiting);
            drawn.Ticker = drawn.Card.schedule.Execute(Tick);
            drawn.Ticker.Every(500);
            var newCode = MakeButton(c.Texts.Get("device.newCode"), () => c.Answer(UiAnswer.NewCode), ghost: true);
            drawn.Card.Add(newCode);
            drawn.Card.Add(MakeButton(c.Texts.Get("device.cancel"), () => c.Answer(UiAnswer.Cancel), ghost: true));
            drawn.Focus = newCode;
            return drawn;
        }

        /// <summary>The controller hints row: the select and back glyphs, drawn only on a console (or when the game asks).</summary>
        internal static VisualElement Hints(DrawContext c, bool withBack)
        {
            var row = Box("kw-hints");
            VisualElement Hint(string glyph, string text)
            {
                var hint = Box("kw-hint");
                hint.Add(Text(c.Texts.Get(glyph), "kw-glyph"));
                hint.Add(Text(c.Texts.Get(text), "kw-hint-text"));
                return hint;
            }
            row.Add(Hint("hint.selectButton", "hint.select"));
            if (withBack) row.Add(Hint("hint.backButton", "hint.back"));
            return row;
        }
    }
}
