using HarmonyLib;
using UnityEngine;
using VoiceChatPlugin.VoiceChat;

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
            _coffeeSprite = InterstellarHudState.LoadSpriteFromResources("VoiceChatPlugin.Resources.CoffeeButton.png", 100f);
        if (_coffeeSprite == null) return;

        var go = new GameObject("CoffeeButton");
        go.transform.SetParent(__instance.transform, false);
        go.transform.localPosition = Vector3.zero;
        go.layer = LayerMask.NameToLayer("UI");

        var sr = go.AddComponent<SpriteRenderer>();
        sr.sprite = _coffeeSprite;
        sr.sortingOrder = 32767;

        var aspect = go.AddComponent<AspectPosition>();
        aspect.Alignment = AspectPosition.EdgeAlignments.RightBottom;
        aspect.parentCam = HudManager.InstanceExists ? HudManager.Instance.UICamera : Camera.main;
        aspect.DistanceFromEdge = new Vector3(0.34f, 1.6f, -6f);
        aspect.AdjustPosition();

        var button = go.AddComponent<PassiveButton>();
        button.OnClick = new UnityEngine.UI.Button.ButtonClickedEvent();
        button.OnClick.AddListener((System.Action)(() =>
            Application.OpenURL("https://ko-fi.com/fangkuaiya")));
        button.OnMouseOver = new UnityEngine.Events.UnityEvent();
        button.OnMouseOver.AddListener((System.Action)(() => sr.color = Color.green));
        button.OnMouseOut = new UnityEngine.Events.UnityEvent();
        button.OnMouseOut.AddListener((System.Action)(() => sr.color = Color.white));

        // Collider for click detection
        var col = go.AddComponent<CircleCollider2D>();
        col.radius = 0.25f;
    }
}
