#nullable enable
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UIElements;

namespace Krtuworks.Unity
{
    /// <summary>
    /// The panel the ready-made screens are drawn on: a runtime PanelSettings and a UIDocument on a hidden object that
    /// survives scene loads, above the game's own UI. Everything on it is built in code.
    /// </summary>
    internal static class KrtuworksUiLayer
    {
        const string ObjectName = "KrtuworksUi";

        static UIDocument? document;
        static PanelSettings? settings;

        /// <summary>The package's style sheet: the <c>--kw-*</c> properties and every screen's look.</summary>
        internal static StyleSheet? Styles => Resources.Load<StyleSheet>("Krtuworks/KrtuworksUi");

        /// <summary>The panel's root, made on first use; the sort order and the game's style sheets (added after ours) are applied each time.</summary>
        internal static VisualElement Root(float sortingOrder, IReadOnlyList<StyleSheet>? extraStyles)
        {
            if (document == null)
            {
                settings = ScriptableObject.CreateInstance<PanelSettings>();
                settings.themeStyleSheet = Resources.Load<ThemeStyleSheet>("Krtuworks/KrtuworksTheme") ?? ScriptableObject.CreateInstance<ThemeStyleSheet>();
                settings.scaleMode = PanelScaleMode.ScaleWithScreenSize;
                settings.referenceResolution = new Vector2Int(1280, 720);
                settings.screenMatchMode = PanelScreenMatchMode.MatchWidthOrHeight;
                settings.match = 0.5f;
                var host = new GameObject(ObjectName) { hideFlags = HideFlags.HideInHierarchy };
                host.SetActive(false);
                Object.DontDestroyOnLoad(host);
                document = host.AddComponent<UIDocument>();
                document.panelSettings = settings;
                host.SetActive(true);
            }
            settings!.sortingOrder = sortingOrder;
            var root = document.rootVisualElement;
            // The root fills the screen: with no screen open the game's own UI under it must still get every click.
            root.pickingMode = PickingMode.Ignore;
            root.styleSheets.Clear();
            var main = Styles;
            if (main != null) root.styleSheets.Add(main);
            if (extraStyles != null)
            {
                foreach (var sheet in extraStyles) root.styleSheets.Add(sheet);
            }
            return root;
        }
    }
}
