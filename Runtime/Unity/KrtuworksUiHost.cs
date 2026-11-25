#nullable enable
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using UnityEngine.UIElements;

namespace Krtuworks.Unity
{
    /// <summary>
    /// The ready-made screens drawn with UI Toolkit on a panel of their own (see <see cref="KrtuworksUiLayer"/>): one modal at a
    /// time, every control reachable with a gamepad, a keyboard or a pointer. Cancel / Escape / the gamepad's B gives the screen's
    /// back answer (Keep playing, Cancel, Play offline); screens with no back action ignore it.
    /// </summary>
    public sealed class KrtuworksUiHost : IUiHost
    {
        readonly UiTexts texts;
        readonly bool hints;
        readonly Action<string>? openUrl;
        readonly IReadOnlyList<StyleSheet>? styles;
        readonly float sortingOrder;

        /// <param name="hints">Draws the controller hints row ("A Select  B Back").</param>
        /// <param name="openUrl">Opens an address in the system browser (the age screen's links); null where there is none.</param>
        /// <param name="styles">The game's style sheets, added after the package's.</param>
        public KrtuworksUiHost(UiTexts texts, bool hints, Action<string>? openUrl, IReadOnlyList<StyleSheet>? styles, float sortingOrder)
        {
            this.texts = texts;
            this.hints = hints;
            this.openUrl = openUrl;
            this.styles = styles;
            this.sortingOrder = sortingOrder;
        }

        VisualElement Root() => KrtuworksUiLayer.Root(sortingOrder, styles);

        public IUiDialog Show(UiScreen screen) => new Dialog(Root(), texts, hints, openUrl, screen);

        public IUiReminder Remind() => new Reminder(Root(), texts);

        sealed class Dialog : IUiDialog
        {
            readonly UiAnswerQueue answers = new UiAnswerQueue();
            readonly VisualElement backdrop = new VisualElement();
            readonly DrawContext context;
            readonly bool hints;
            DrawnScreen? drawn;

            internal Dialog(VisualElement root, UiTexts texts, bool hints, Action<string>? openUrl, UiScreen screen)
            {
                this.hints = hints;
                backdrop.AddToClassList("kw-root");
                backdrop.AddToClassList("kw-backdrop");
                // The answers go out on the next tick: a click handler must not be taken apart by the code its answer wakes.
                context = new DrawContext { Texts = texts, OpenUrl = openUrl, Answer = answer => backdrop.schedule.Execute(() => answers.Post(answer)) };
                backdrop.RegisterCallback<NavigationCancelEvent>(e =>
                {
                    if (drawn?.Back == null) return;
                    context.Answer(drawn.Back);
                    e.StopPropagation();
                });
                root.Add(backdrop);
                Draw(screen);
            }

            void Draw(UiScreen screen)
            {
                Release();
                backdrop.Clear();
                drawn = KrtuworksUiScreens.Draw(screen, context);
                backdrop.Add(drawn.Card);
                if (hints) drawn.Card.Add(KrtuworksUiScreens.Hints(context, drawn.Back != null));
                var first = drawn.Focus;
                if (first != null) backdrop.schedule.Execute(() => first.Focus());
            }

            void Release()
            {
                drawn?.Ticker?.Pause();
                if (drawn == null) return;
                foreach (var texture in drawn.Textures) UnityEngine.Object.Destroy(texture);
                drawn = null;
            }

            public Task<UiAnswer> NextAnswerAsync() => answers.NextAsync();

            public void Update(UiScreen screen) => Draw(screen);

            public void Close()
            {
                Release();
                backdrop.RemoveFromHierarchy();
                answers.Close();
            }
        }

        /// <summary>A corner prompt that never takes input from the game: only its own buttons are hit by the pointer.</summary>
        sealed class Reminder : IUiReminder
        {
            readonly TaskCompletionSource<UiReminderAnswer> answer = new TaskCompletionSource<UiReminderAnswer>();
            readonly VisualElement layer = new VisualElement();

            internal Reminder(VisualElement root, UiTexts texts)
            {
                layer.AddToClassList("kw-root");
                layer.AddToClassList("kw-corner");
                layer.pickingMode = PickingMode.Ignore;
                var toast = new VisualElement();
                toast.AddToClassList("kw-toast");
                var text = new VisualElement();
                text.AddToClassList("kw-toast-text");
                text.Add(KrtuworksUiScreens.Text(texts.Get("reminder.title"), "kw-toast-title"));
                text.Add(KrtuworksUiScreens.Text(texts.Get("reminder.body"), "kw-toast-body"));
                toast.Add(text);
                toast.Add(KrtuworksUiScreens.MakeButton(texts.Get("reminder.later"), () => Answered(UiReminderAnswer.Later), ghost: true));
                toast.Add(KrtuworksUiScreens.MakeButton(texts.Get("reminder.link"), () => Answered(UiReminderAnswer.Link)));
                layer.Add(toast);
                root.Add(layer);
            }

            void Answered(UiReminderAnswer given) => layer.schedule.Execute(() => answer.TrySetResult(given));

            public Task<UiReminderAnswer> Answer => answer.Task;

            public void Close()
            {
                layer.RemoveFromHierarchy();
                answer.TrySetResult(UiReminderAnswer.Later);
            }
        }
    }
}
