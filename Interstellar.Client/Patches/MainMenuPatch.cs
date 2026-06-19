using HarmonyLib;
using UnityEngine;

namespace VoiceChatPlugin;

/// <summary>
/// Adds a "Buy Me a Coffee" button on the main menu,
/// styled like TOR's Discord button, positioned directly above it.
/// </summary>
[HarmonyPatch(typeof(MainMenuManager), nameof(MainMenuManager.Awake))]
public static class MainMenuCoffeeButtonPatch
{
    private static Sprite? _coffeeSprite;

    public static void Postfix(MainMenuManager __instance)
    {
        if (_coffeeSprite == null)
            _coffeeSprite = LoadCoffeeSprite();
        if (_coffeeSprite == null) return;

        // ── Coffee button (above TOR Discord: same X, higher Y) ──
        // TOR Discord is at RightBottom (0.34, 1.0, -6)
        // Coffee sits directly above: RightBottom (0.34, 1.6, -6)
        var go = new GameObject("CoffeeButton");
        go.transform.SetParent(__instance.transform, false);
        go.transform.localPosition = Vector3.zero;
        go.layer = LayerMask.NameToLayer("UI");

        var sr = go.AddComponent<SpriteRenderer>();
        sr.sprite = _coffeeSprite;
        sr.sortingOrder = 32767;

        // AspectPosition like TOR
        var aspect = go.AddComponent<AspectPosition>();
        aspect.Alignment = AspectPosition.EdgeAlignments.RightBottom;
        aspect.parentCam = HudManager.InstanceExists ? HudManager.Instance.UICamera : Camera.main;
        aspect.DistanceFromEdge = new Vector3(0.34f, 1.6f, -6f);
        aspect.AdjustPosition();

        // PassiveButton with hover feedback (green tint, like TOR Copy/Paste)
        var button = go.AddComponent<PassiveButton>();
        button.OnClick = new UnityEngine.UI.Button.ButtonClickedEvent();
        button.OnClick.AddListener((System.Action)(() =>
            Application.OpenURL("https://ko-fi.com/YOUR_USERNAME")));
        button.OnMouseOver = new UnityEngine.Events.UnityEvent();
        button.OnMouseOver.AddListener((System.Action)(() => sr.color = Color.green));
        button.OnMouseOut = new UnityEngine.Events.UnityEvent();
        button.OnMouseOut.AddListener((System.Action)(() => sr.color = Color.white));

        // Collider for click detection
        var col = go.AddComponent<CircleCollider2D>();
        col.radius = 0.25f;
    }

    private static Sprite? LoadCoffeeSprite()
    {
        try
        {
            var stream = System.Reflection.Assembly.GetExecutingAssembly()
                .GetManifestResourceStream("VoiceChatPlugin.Resources.CoffeeButton.png");
            if (stream == null) return null;

            var tex = new Texture2D(0, 0, TextureFormat.RGBA32, false) { wrapMode = TextureWrapMode.Clamp };
            using var ms = new System.IO.MemoryStream();
            stream.CopyTo(ms);
            tex.LoadImage(ms.ToArray(), false);

            var spr = Sprite.Create(tex, new Rect(0, 0, tex.width, tex.height),
                new Vector2(0.5f, 0.5f), 100f);
            spr.hideFlags |= HideFlags.HideAndDontSave | HideFlags.DontSaveInEditor;
            return spr;
        }
        catch
        {
            InterstellarPlugin.Logger?.LogError("[VC] Failed to load coffee button sprite.");
            return null;
        }
    }
}
