#nullable enable
using System;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.UIElements;

namespace Krtuworks.Unity
{
    /// <summary>
    /// The "Sign in with Krtuworks ID" button to place in a UXML document or add in code. It follows the current ready-made
    /// screens (<see cref="KrtuworksUnity.Ui"/>) and is hidden where there is no sign-in (a WebGL build).
    /// </summary>
#if UNITY_2023_2_OR_NEWER
    [UxmlElement]
#endif
    public partial class KrtuworksSignInButton : VisualElement
    {
#if !UNITY_2023_2_OR_NEWER
        public new class UxmlFactory : UxmlFactory<KrtuworksSignInButton> { }
#endif

        public KrtuworksSignInButton()
        {
            AddToClassList("kw-root");
            var styles = KrtuworksUiLayer.Styles;
            if (styles != null) styleSheets.Add(styles);
            RegisterCallback<AttachToPanelEvent>(_ =>
            {
                KrtuworksUnity.UiChanged += Bind;
                Bind(KrtuworksUnity.Ui);
            });
            RegisterCallback<DetachFromPanelEvent>(_ => KrtuworksUnity.UiChanged -= Bind);
        }

        void Bind(KrtuworksUi? ui)
        {
            Clear();
            if (ui == null || !ui.SignInOffered)
            {
                style.display = DisplayStyle.None;
                return;
            }
            style.display = DisplayStyle.Flex;
            var button = new Button();
            button.AddToClassList("kw-button");
            button.AddToClassList("kw-signin");
            button.Add(KrtuworksUiScreens.Text("K", "kw-mark"));
            button.Add(KrtuworksUiScreens.Text(ui.T("signIn.button"), "kw-signin-text"));
            button.clicked += () => _ = KrtuworksUiScreens.Quietly(ui.SignInAsync());
            Add(button);
        }
    }

    /// <summary>
    /// The account chip: the player's picture (or the first letter of the name), the name and "Krtuworks ID", and a menu: manage the
    /// account and sign out, or for a guest link a Krtuworks ID. It follows who plays and hides while nobody does.
    /// </summary>
#if UNITY_2023_2_OR_NEWER
    [UxmlElement]
#endif
    public partial class KrtuworksAccountChip : VisualElement
    {
#if !UNITY_2023_2_OR_NEWER
        public new class UxmlFactory : UxmlFactory<KrtuworksAccountChip> { }
#endif

        KrtuworksUi? ui;
        VisualElement? menu;

        public KrtuworksAccountChip()
        {
            AddToClassList("kw-root");
            var styles = KrtuworksUiLayer.Styles;
            if (styles != null) styleSheets.Add(styles);
            RegisterCallback<AttachToPanelEvent>(_ =>
            {
                KrtuworksUnity.UiChanged += Bind;
                Bind(KrtuworksUnity.Ui);
            });
            RegisterCallback<DetachFromPanelEvent>(_ =>
            {
                KrtuworksUnity.UiChanged -= Bind;
                Follow(null);
            });
        }

        void Bind(KrtuworksUi? current)
        {
            Follow(current);
            Rebuild();
        }

        void Follow(KrtuworksUi? current)
        {
            if (ui != null) ui.Client.IdentityChanged -= OnIdentityChanged;
            ui = current;
            if (ui != null) ui.Client.IdentityChanged += OnIdentityChanged;
        }

        void OnIdentityChanged(IdentityChange change) => Rebuild();

        void Rebuild()
        {
            Clear();
            menu = null;
            var player = ui?.Client.Player;
            if (ui == null || player == null)
            {
                style.display = DisplayStyle.None;
                return;
            }
            style.display = DisplayStyle.Flex;
            var current = ui;
            var guest = player.Kind == PlayerKind.Guest;
            var name = guest ? current.T("account.guest") : player.Username ?? current.T("account.label");
            var wrap = new VisualElement();
            wrap.AddToClassList("kw-wrap");
            var chip = new Button();
            chip.AddToClassList("kw-chip");
            var avatar = new VisualElement();
            avatar.AddToClassList("kw-avatar");
            avatar.Add(KrtuworksUiScreens.Text(name.Length > 0 ? name.Substring(0, 1).ToUpperInvariant() : "?", "kw-avatar-letter"));
            chip.Add(avatar);
            var who = new VisualElement();
            who.AddToClassList("kw-who");
            who.Add(KrtuworksUiScreens.Text(name, "kw-who-name"));
            who.Add(KrtuworksUiScreens.Text(current.T("account.label"), "kw-who-small"));
            chip.Add(who);
            wrap.Add(chip);
            Add(wrap);
            if (!string.IsNullOrEmpty(player.Picture)) _ = ShowPicture(avatar, player);
            chip.clicked += () => ToggleMenu(wrap, current, guest);
            wrap.RegisterCallback<NavigationCancelEvent>(e =>
            {
                if (menu == null) return;
                CloseMenu();
                e.StopPropagation();
            });
        }

        /// <summary>The picture replaces the letter when it loads; when it does not, the letter stays.</summary>
        async Task ShowPicture(VisualElement avatar, Player player)
        {
            var picture = await KrtuworksAvatars.LoadAsync(player.Picture!);
            if (picture == null || avatar.panel == null || ui?.Client.Player?.Picture != player.Picture) return;
            avatar.Clear();
            avatar.style.backgroundImage = Background.FromTexture2D(picture);
        }

        void CloseMenu()
        {
            menu?.RemoveFromHierarchy();
            menu = null;
        }

        void ToggleMenu(VisualElement wrap, KrtuworksUi current, bool guest)
        {
            if (menu != null)
            {
                CloseMenu();
                return;
            }
            var items = new System.Collections.Generic.List<Button>();
            if (guest)
            {
                if (current.SignInOffered) items.Add(KrtuworksUiScreens.MakeButton(current.T("account.link"), () => _ = KrtuworksUiScreens.Quietly(current.SignInAsync())));
            }
            else
            {
                if (current.CanOpenUrl) items.Add(KrtuworksUiScreens.MakeButton(current.T("account.manage"), current.OpenAccountPage));
                items.Add(KrtuworksUiScreens.MakeButton(current.T("account.signOut"), () => _ = KrtuworksUiScreens.Quietly(current.SignOutAsync())));
            }
            if (items.Count == 0) return;
            menu = new VisualElement();
            menu.AddToClassList("kw-menu");
            foreach (var item in items)
            {
                item.clicked += CloseMenu;
                menu.Add(item);
            }
            wrap.Add(menu);
            var first = items[0];
            menu.schedule.Execute(() => first.Focus());
        }
    }
}
