#pragma warning disable CS8602, CS8603
using System;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
using Object = UnityEngine.Object;

namespace Interstellar.Voice;

public static class VCTextInputPopup
{
    private static GameObject? _popup;
    private static VCTextField? _field;
    private static Action<string>? _onSave;

    public static bool IsShowing => _popup != null && _popup.activeSelf;

    public static void Show(string title, string placeholder, string current, int charLimit, Action<string> onSave)
    {
        Hide();
        _onSave = onSave;

        var canvas = VCUiKit.EnsureCanvas();
        _popup = new GameObject("VC_InputPopup");
        _popup.transform.SetParent(canvas.transform, false);
        var rt = _popup.AddComponent<RectTransform>();
        rt.anchorMin = Vector2.zero;
        rt.anchorMax = Vector2.one;
        rt.offsetMin = Vector2.zero;
        rt.offsetMax = Vector2.zero;

        var dim = VCUiKit.CreateImage(_popup.transform, "Dim", Vector2.zero, Vector2.zero, VCUiKit.PixelSprite, new Color(0f, 0f, 0f, 0.45f));
        var dimRt = (RectTransform)dim.transform;
        dimRt.anchorMin = Vector2.zero;
        dimRt.anchorMax = Vector2.one;
        dimRt.offsetMin = Vector2.zero;
        dimRt.offsetMax = Vector2.zero;
        var dimBtn = dim.gameObject.AddComponent<Button>();
        dimBtn.transition = Selectable.Transition.None;
        dimBtn.onClick.AddListener((Action)(() => Hide()));

        const float pw = 720f, ph = 330f;
        var panel = VCUiKit.CreatePanel(_popup.transform, "Panel", new Vector2(pw, ph),
            new Color(0.88f, 0.94f, 1f, 1f), new Color(0.07f, 0.10f, 0.16f, 0.97f));

        VCUiKit.CreateText(panel, "Title", title, new Vector2(0f, ph * 0.5f - 46f), new Vector2(pw - 80f, 44f),
            30f, new Color(0.92f, 0.95f, 1f, 1f), FontStyles.Bold, TextAlignmentOptions.Center);

        var fieldObj = new GameObject("Input");
        fieldObj.transform.SetParent(panel, false);
        var fieldRt = fieldObj.AddComponent<RectTransform>();
        fieldRt.anchorMin = fieldRt.anchorMax = new Vector2(0.5f, 0.5f);
        fieldRt.pivot = new Vector2(0.5f, 0.5f);
        fieldRt.anchoredPosition = new Vector2(0f, 6f);
        fieldRt.sizeDelta = new Vector2(pw - 120f, 62f);

        _field = fieldObj.AddComponent<VCTextField>();
        _field.Init(fieldRt, 26f, charLimit, placeholder, current ?? "");

        VCUiKit.CreateButton(panel, TranslationHelper.Get("vc.popup.confirm", "Confirm"), new Vector2(-130f, -118f), new Vector2(200f, 58f),
            new Color(0.20f, 0.62f, 0.33f, 1f), () => Confirm(), 26f);
        VCUiKit.CreateButton(panel, TranslationHelper.Get("vc.popup.cancel", "Cancel"), new Vector2(130f, -118f), new Vector2(200f, 58f),
            new Color(0.65f, 0.25f, 0.25f, 1f), () => Hide(), 26f);

        _field.GainFocus();
    }

    public static void Confirm()
    {
        if (_field == null) { Hide(); return; }
        var save = _onSave;
        var text = _field.Text;
        Hide();
        save?.Invoke(text);
    }

    public static void Hide()
    {
        if (_popup != null) Object.Destroy(_popup);
        _popup = null;
        _field = null;
        _onSave = null;
    }
}
