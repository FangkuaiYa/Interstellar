using System;
using System.Collections.Generic;
using Il2CppInterop.Runtime.Injection;
using Interstellar.Voice;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
using Object = UnityEngine.Object;

namespace Interstellar.UI;

internal sealed class VoiceUiDriver : MonoBehaviour
{
    private static bool _registered;

    public static void Register()
    {
        if (_registered) return;
        _registered = true;
        ClassInjector.RegisterTypeInIl2Cpp<VoiceUiDriver>();
    }

    public VoiceUiDriver(IntPtr ptr) : base(ptr) { }

    void Update()
    {
        VoiceUiKit.Tick();
    }
}

internal static class VoiceUiKit
{
    public static readonly Color32 Accent       = new(34, 211, 238, 255);
    public static readonly Color32 AccentSoft    = new(34, 211, 238, 90);
    public static readonly Color32 AccentFaint   = new(34, 211, 238, 28);
    public static readonly Color32 AccentGlow     = new(34, 211, 238, 64);
    public static readonly Color32 PanelOuter     = new(12, 15, 20, 235);
    public static readonly Color32 PanelInner     = new(20, 25, 33, 235);
    public static readonly Color32 PanelTop        = new(31, 39, 51, 240);
    public static readonly Color32 PanelBottom     = new(15, 19, 26, 240);
    public static readonly Color32 PanelShadow     = new(0, 0, 0, 170);
    public static readonly Color32 RailSurface    = new(9, 11, 15, 235);
    public static readonly Color32 HeaderSurface  = new(16, 20, 27, 245);
    public static readonly Color32 HeaderTop       = new(26, 33, 44, 250);
    public static readonly Color32 HeaderBottom    = new(14, 18, 25, 250);
    public static readonly Color32 TopHighlight    = new(150, 170, 195, 32);
    public static readonly Color32 Divider         = new(120, 138, 160, 26);
    public static readonly Color32 TextPrimary     = new(228, 235, 245, 255);
    public static readonly Color32 TextBright      = new(244, 249, 255, 255);
    public static readonly Color32 TextMuted       = new(140, 156, 178, 255);
    public static readonly Color32 TextFaint       = new(96, 110, 130, 255);
    public static readonly Color32 TrackBg         = new(38, 46, 60, 255);
    public static readonly Color32 ControlBg       = new(28, 34, 44, 255);
    public static readonly Color32 ControlHover    = new(44, 53, 67, 255);
    public static readonly Color32 RowHover        = new(255, 255, 255, 12);
    public static readonly Color32 KnobOff         = new(150, 162, 180, 255);
    public static readonly Color32 ToggleOffTrack  = new(46, 54, 68, 255);
    public static readonly Color32 CloseHover      = new(230, 88, 96, 255);
    public static readonly Color32 Danger          = new(230, 88, 96, 255);
    public static readonly Color32 DangerDim       = new(70, 34, 38, 255);
    public static readonly Color32 Clear           = new(0, 0, 0, 0);

    // Live level bar ramp used by the player-volume rows (green → yellow → red).
    private static readonly Color32 LevelMeterGreen  = new(77, 217, 107, 240);
    private static readonly Color32 LevelMeterYellow = new(242, 214, 64, 240);
    private static readonly Color32 LevelMeterRed    = new(245, 64, 56, 240);
    private const float LevelMeterRelease = 1.6f;

    private static readonly Dictionary<uint, Sprite> _solid = new();
    private static Sprite? _rounded;
    private static Sprite? _roundedSoft;
    private static Sprite? _glow;
    private static Sprite? _gradPanel;
    private static Sprite? _gradHeader;
    private static TMP_FontAsset? _font;
    private static GameObject? _canvasRoot;
    private static Canvas? _canvas;
    private static RectTransform? _tooltipRoot;
    private static CanvasGroup? _tooltipGroup;
    private static TextMeshProUGUI? _tooltipText;
    private static bool _tooltipRequested;

    public static readonly Color32 Backdrop = new(0, 0, 0, 150);

    public static bool AnyPanelOpen =>
        VoiceSettingsPanel.IsOpen || VCInputBlockPatch.IsAnyVoiceWindowOpen;

    private static bool _swallowActive;
    private static bool _swallowSawRelease;
    private static int _swallowFrame = -1000;
    private const int SwallowMaxFrames = 20;
    public static void SwallowClick() { _swallowActive = true; _swallowSawRelease = false; _swallowFrame = Time.frameCount; }
    private static bool SwallowBlocking => _swallowActive && Time.frameCount - _swallowFrame < SwallowMaxFrames;
    public static bool BlockGameInput => AnyPanelOpen || SwallowBlocking;

    /// <summary>True while a <see cref="RebindRow"/> owns the keyboard. Anything that
    /// reads a global hotkey stands aside so a capture only ever binds a key — it never
    /// closes a window, toggles a setting or scrolls the pane.</summary>
    internal static bool IsCapturingKey => RebindRow.IsCapturing;

    /// <summary>Extends <see cref="IsCapturingKey"/> over the frame a capture consumed an
    /// input and until the bound keys are released, so the key that was just bound cannot
    /// fire its old hotkey on the very press that set it.</summary>
    internal static bool SuppressGlobalHotkeys
        => IsCapturingKey || RebindRow.ShouldSuppressKeybinds;
    private static void UpdateSwallow()
    {
        if (!_swallowActive) return;
        if (Time.frameCount - _swallowFrame >= SwallowMaxFrames) { _swallowActive = false; return; }
        if (Input.GetMouseButton(0)) return;
        if (_swallowSawRelease) _swallowActive = false;
        else _swallowSawRelease = true;
    }

    private static int _tickFrame = -1;

    public static void Tick()
    {
        int frame = Time.frameCount;
        if (frame == _tickFrame) return;
        _tickFrame = frame;
        UpdateSwallow();
        BeginTooltipFrame();

        try { VoiceDeviceTest.Tick(); } catch (Exception e) { InterstellarPlugin.Logger?.LogWarning("[VC-UI] VoiceDeviceTest.Tick threw: " + e.Message); }
        try { VoiceSettingsPanel.Tick(); } catch (Exception e) { InterstellarPlugin.Logger?.LogWarning("[VC-UI] VoiceSettingsPanel.Tick threw: " + e.Message); }
        EndTooltipFrame();
    }

    private static void BeginTooltipFrame() => _tooltipRequested = false;

    private static void EndTooltipFrame()
    {
        if (_tooltipRoot == null || _tooltipGroup == null) return;
        if (!_tooltipRequested)
        {
            _tooltipGroup.alpha = 0f;
            _tooltipRoot.gameObject.SetActive(false);
            return;
        }

        _tooltipRoot.SetAsLastSibling();
    }

    internal static void RequestTooltip(
        string title,
        string description,
        bool plainTextDescription = false)
    {
        if (string.IsNullOrWhiteSpace(description)) return;
        EnsureTooltip();
        if (_tooltipRoot == null || _tooltipGroup == null || _tooltipText == null) return;

        _tooltipRequested = true;
        _tooltipRoot.gameObject.SetActive(true);
        _tooltipGroup.alpha = 1f;
        string body = plainTextDescription
            ? $"<noparse>{description}</noparse>"
            : description;
        _tooltipText.text = string.IsNullOrWhiteSpace(title)
            ? body
            : $"<b>{title}</b>\n<size=86%><color=#B5C2D4>{body}</color></size>";

        _tooltipRoot.sizeDelta = new Vector2(420f, 80f);
        _tooltipText.ForceMeshUpdate();
        float height = Mathf.Clamp(_tooltipText.preferredHeight + 28f, 62f, 260f);
        _tooltipRoot.sizeDelta = new Vector2(420f, height);

        if (!LocalPoint(CanvasRect, out var mouse)) return;
        var canvasRect = CanvasRect.rect;
        float halfW = _tooltipRoot.sizeDelta.x * 0.5f;
        float halfH = _tooltipRoot.sizeDelta.y * 0.5f;
        float x = mouse.x + 22f + halfW;
        if (x + halfW > canvasRect.xMax - 12f)
            x = mouse.x - 22f - halfW;
        float y = mouse.y - 18f - halfH;
        x = Mathf.Clamp(x, canvasRect.xMin + halfW + 12f, canvasRect.xMax - halfW - 12f);
        y = Mathf.Clamp(y, canvasRect.yMin + halfH + 12f, canvasRect.yMax - halfH - 12f);
        _tooltipRoot.anchoredPosition = new Vector2(x, y);
    }

    private static void EnsureTooltip()
    {
        EnsureCanvas();
        if (_tooltipRoot != null && _tooltipGroup != null && _tooltipText != null) return;

        _tooltipRoot = Rect("Interstellar_Tooltip", Canvas.transform);
        _tooltipRoot.Anchor(new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f));
        _tooltipRoot.sizeDelta = new Vector2(420f, 80f);

        var shadow = GlowImage("TooltipShadow", _tooltipRoot, new Color32(0, 0, 0, 165));
        shadow.rectTransform.Anchor(Vector2.zero, Vector2.one, new Vector2(0.5f, 0.5f));
        shadow.rectTransform.offsetMin = new Vector2(-18f, -22f);
        shadow.rectTransform.offsetMax = new Vector2(18f, 14f);

        var backgroundRt = Rect("TooltipBackground", _tooltipRoot);
        backgroundRt.Anchor(Vector2.zero, Vector2.one, new Vector2(0.5f, 0.5f));
        backgroundRt.offsetMin = Vector2.zero;
        backgroundRt.offsetMax = Vector2.zero;
        var background = backgroundRt.gameObject.AddComponent<Image>();
        background.sprite = Rounded();
        background.type = Image.Type.Sliced;
        background.color = new Color32(18, 23, 31, 252);
        background.raycastTarget = false;

        var accent = Panel("TooltipAccent", _tooltipRoot, Accent, true);
        accent.rectTransform.Anchor(new Vector2(0f, 0f), new Vector2(0f, 1f), new Vector2(0f, 0.5f));
        accent.rectTransform.sizeDelta = new Vector2(3f, -20f);
        accent.rectTransform.anchoredPosition = new Vector2(7f, 0f);

        _tooltipText = Text("TooltipText", _tooltipRoot, "", 18f,
            TextPrimary, TextAlignmentOptions.TopLeft);
        _tooltipText.enableWordWrapping = true;
        _tooltipText.overflowMode = TextOverflowModes.Overflow;
        _tooltipText.rectTransform.Anchor(Vector2.zero, Vector2.one, new Vector2(0.5f, 0.5f));
        _tooltipText.rectTransform.offsetMin = new Vector2(20f, 14f);
        _tooltipText.rectTransform.offsetMax = new Vector2(-16f, -14f);

        _tooltipGroup = _tooltipRoot.gameObject.AddComponent<CanvasGroup>();
        _tooltipGroup.interactable = false;
        _tooltipGroup.blocksRaycasts = false;
        _tooltipGroup.alpha = 0f;
        _tooltipRoot.gameObject.SetActive(false);
    }

    public static void RaiseAbove(Transform panel, Transform? extra = null)
    {
        panel.SetAsLastSibling();
        if (extra != null) extra.SetAsLastSibling();
    }

    public static Canvas Canvas
    {
        get { EnsureCanvas(); return _canvas!; }
    }

    public static RectTransform CanvasRect
    {
        get { EnsureCanvas(); return _canvas!.GetComponent<RectTransform>(); }
    }

    public static void EnsureCanvas()
    {
        if (_canvasRoot != null && _canvas != null)
        {
            EnsureDriver();
            return;
        }
        _canvas = null;
        _canvasRoot = null;

        _canvasRoot = new GameObject("Interstellar_UICanvas");
        Object.DontDestroyOnLoad(_canvasRoot);
        _canvasRoot.hideFlags |= HideFlags.DontUnloadUnusedAsset;
        _canvasRoot.layer = LayerMask.NameToLayer("UI");

        _canvas = _canvasRoot.AddComponent<Canvas>();
        _canvas.renderMode = RenderMode.ScreenSpaceOverlay;
        _canvas.sortingOrder = 1000;

        var scaler = _canvasRoot.AddComponent<CanvasScaler>();
        scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
        scaler.referenceResolution = new Vector2(1920f, 1080f);
        scaler.screenMatchMode = CanvasScaler.ScreenMatchMode.MatchWidthOrHeight;
        scaler.matchWidthOrHeight = 0.5f;

        _canvasRoot.AddComponent<GraphicRaycaster>();
        EnsureDriver();
    }

    public static void EnsureDriver()
    {
        if (_canvasRoot == null) return;
        if (!_canvasRoot.activeSelf) _canvasRoot.SetActive(true);
        VoiceUiDriver.Register();
        var driver = _canvasRoot.GetComponent<VoiceUiDriver>();
        if (driver == null)
            driver = _canvasRoot.AddComponent<VoiceUiDriver>();
        if (!driver.enabled) driver.enabled = true;
    }

    public static Sprite Solid(Color32 c)
    {
        uint key = ((uint)c.r << 24) | ((uint)c.g << 16) | ((uint)c.b << 8) | c.a;
        if (_solid.TryGetValue(key, out var cached) && cached != null) return cached;
        var tex = new Texture2D(1, 1, TextureFormat.RGBA32, false) { hideFlags = HideFlags.HideAndDontSave };
        tex.SetPixel(0, 0, c);
        tex.Apply();
        var sprite = Sprite.Create(tex, new Rect(0, 0, 1, 1), new Vector2(0.5f, 0.5f));
        sprite.hideFlags |= HideFlags.HideAndDontSave;
        _solid[key] = sprite;
        return sprite;
    }

    public static Sprite Rounded(bool soft = false)
    {
        if (soft && _roundedSoft != null) return _roundedSoft;
        if (!soft && _rounded != null) return _rounded;

        const int s = 64;
        int rad = soft ? 32 : 16;
        var tex = new Texture2D(s, s, TextureFormat.RGBA32, false) { hideFlags = HideFlags.HideAndDontSave };
        for (int y = 0; y < s; y++)
        for (int x = 0; x < s; x++)
        {
            float a = 1f;
            int cx = x < rad ? rad : (x > s - 1 - rad ? s - 1 - rad : x);
            int cy = y < rad ? rad : (y > s - 1 - rad ? s - 1 - rad : y);
            if (cx != x || cy != y)
            {
                float d = Mathf.Sqrt((x - cx) * (x - cx) + (y - cy) * (y - cy));
                a = Mathf.Clamp01(rad - d + 0.5f);
            }
            tex.SetPixel(x, y, new Color(1f, 1f, 1f, a));
        }
        tex.Apply();
        var sprite = Sprite.Create(tex, new Rect(0, 0, s, s), new Vector2(0.5f, 0.5f), s, 0,
            SpriteMeshType.FullRect, new Vector4(rad, rad, rad, rad));
        sprite.hideFlags |= HideFlags.HideAndDontSave;
        if (soft) _roundedSoft = sprite; else _rounded = sprite;
        return sprite;
    }

    public static Sprite Glow()
    {
        if (_glow != null) return _glow;
        const int s = 64;
        float half = (s - 1) * 0.5f;
        var tex = new Texture2D(s, s, TextureFormat.RGBA32, false) { hideFlags = HideFlags.HideAndDontSave };
        for (int y = 0; y < s; y++)
        for (int x = 0; x < s; x++)
        {
            float dx = Mathf.Abs(x - half) / half;
            float dy = Mathf.Abs(y - half) / half;
            float d = Mathf.Sqrt(dx * dx + dy * dy);
            float a = Mathf.Clamp01(1f - d);
            a = a * a;
            tex.SetPixel(x, y, new Color(1f, 1f, 1f, a));
        }
        tex.Apply();
        var sprite = Sprite.Create(tex, new Rect(0, 0, s, s), new Vector2(0.5f, 0.5f), s, 0,
            SpriteMeshType.FullRect, new Vector4(24, 24, 24, 24));
        sprite.hideFlags |= HideFlags.HideAndDontSave;
        _glow = sprite;
        return sprite;
    }

    private static Sprite Gradient(Color32 top, Color32 bottom, ref Sprite? cache)
    {
        if (cache != null) return cache;
        const int s = 64;
        int rad = 18;
        var tex = new Texture2D(s, s, TextureFormat.RGBA32, false) { hideFlags = HideFlags.HideAndDontSave };
        for (int y = 0; y < s; y++)
        {
            float t = y / (float)(s - 1);
            Color col = Color.Lerp(bottom, top, t);
            for (int x = 0; x < s; x++)
            {
                float a = 1f;
                int cx = x < rad ? rad : (x > s - 1 - rad ? s - 1 - rad : x);
                int cy = y < rad ? rad : (y > s - 1 - rad ? s - 1 - rad : y);
                if (cx != x || cy != y)
                {
                    float d = Mathf.Sqrt((x - cx) * (x - cx) + (y - cy) * (y - cy));
                    a = Mathf.Clamp01(rad - d + 0.5f);
                }
                var c = col; c.a *= a;
                tex.SetPixel(x, y, c);
            }
        }
        tex.Apply();
        var sprite = Sprite.Create(tex, new Rect(0, 0, s, s), new Vector2(0.5f, 0.5f), s, 0,
            SpriteMeshType.FullRect, new Vector4(rad, rad, rad, rad));
        sprite.hideFlags |= HideFlags.HideAndDontSave;
        cache = sprite;
        return sprite;
    }

    public static Sprite PanelGradient() => Gradient(PanelTop, PanelBottom, ref _gradPanel);
    public static Sprite HeaderGradient() => Gradient(HeaderTop, HeaderBottom, ref _gradHeader);

    public static Image GlowImage(string name, Transform parent, Color32 color)
    {
        var rt = Rect(name, parent);
        var img = rt.gameObject.AddComponent<Image>();
        img.sprite = Glow();
        img.type = Image.Type.Sliced;
        img.color = color;
        img.raycastTarget = false;
        return img;
    }

    public static TMP_FontAsset? GameFont()
    {
        if (_font != null) return _font;
        if (HudManager.InstanceExists)
        {
            foreach (var t in HudManager.Instance.GetComponentsInChildren<TextMeshPro>(true))
                if (t != null && t.font != null) { _font = t.font; break; }
        }
        if (_font == null)
        {
            foreach (var t in Object.FindObjectsOfType<TextMeshPro>())
                if (t != null && t.font != null) { _font = t.font; break; }
        }
        return _font;
    }

    public static RectTransform Rect(string name, Transform parent)
    {
        var go = new GameObject(name);
        go.layer = LayerMask.NameToLayer("UI");
        var rt = go.AddComponent<RectTransform>();
        rt.SetParent(parent, false);
        return rt;
    }

    public static RectTransform Anchor(this RectTransform rt, Vector2 min, Vector2 max, Vector2 pivot)
    {
        rt.anchorMin = min;
        rt.anchorMax = max;
        rt.pivot = pivot;
        return rt;
    }

    public static Image Panel(string name, Transform parent, Color32 color, bool rounded = true, bool soft = false)
    {
        var rt = Rect(name, parent);
        var img = rt.gameObject.AddComponent<Image>();
        img.sprite = rounded ? Rounded(soft) : Solid(Color.white);
        if (rounded) img.type = Image.Type.Sliced;
        img.color = color;
        img.raycastTarget = false;
        return img;
    }

    public static TextMeshProUGUI Text(string name, Transform parent, string content, float size,
        Color32 color, TextAlignmentOptions align, FontStyles style = FontStyles.Normal)
    {
        var rt = Rect(name, parent);
        var tmp = rt.gameObject.AddComponent<TextMeshProUGUI>();
        var font = GameFont();
        if (font != null) tmp.font = font;
        tmp.text = content;
        tmp.fontSize = size;
        tmp.color = color;
        tmp.alignment = align;
        tmp.fontStyle = style;
        tmp.richText = true;
        tmp.enableWordWrapping = false;
        tmp.raycastTarget = false;
        tmp.overflowMode = TextOverflowModes.Overflow;
        return tmp;
    }

    public static RectTransform SectionHeader(string name, RectTransform pane, string content,
        float paneW, float y, float height)
    {
        var rt = Rect("Section_" + name, pane);
        rt.Anchor(new Vector2(0f, 1f), new Vector2(1f, 1f), new Vector2(0.5f, 1f));
        rt.sizeDelta = new Vector2(0f, height);
        rt.anchoredPosition = new Vector2(0f, y);

        var label = Text("SectionLabel", rt, content, 15f, TextMuted, TextAlignmentOptions.Left, FontStyles.Bold);
        label.characterSpacing = 4f;
        label.rectTransform.Anchor(new Vector2(0f, 0f), new Vector2(1f, 1f), new Vector2(0f, 0.5f));
        label.rectTransform.offsetMin = new Vector2(Row.EdgePad, 0f);
        label.rectTransform.offsetMax = new Vector2(-Row.EdgePad, -2f);
        return rt;
    }

    public static bool Contains(RectTransform rt)
    {
        if (rt == null) return false;
        return RectTransformUtility.RectangleContainsScreenPoint(rt, Input.mousePosition, null);
    }

    public static bool LocalPoint(RectTransform rt, out Vector2 local)
    {
        return RectTransformUtility.ScreenPointToLocalPointInRectangle(
            rt, Input.mousePosition, null, out local);
    }

    public static float AppearScale(float t)
    {
        const float c1 = 1.70158f;
        const float c3 = c1 + 1f;
        float eased = 1f + c3 * (t - 1f) * (t - 1f) * (t - 1f) + c1 * (t - 1f) * (t - 1f);
        return Mathf.LerpUnclamped(0.6f, 1f, eased);
    }

    public static Color32 Lerp(Color32 a, Color32 b, float t)
    {
        t = Mathf.Clamp01(t);
        return new Color32(
            (byte)Mathf.Lerp(a.r, b.r, t),
            (byte)Mathf.Lerp(a.g, b.g, t),
            (byte)Mathf.Lerp(a.b, b.b, t),
            (byte)Mathf.Lerp(a.a, b.a, t));
    }

    /// <summary>
    /// Grays a control the way Unity tints a non-interactable ColorTint graphic, used for
    /// settings the current player is not allowed to change (host-only rows for guests).
    /// </summary>
    public static Color32 Dim(Color32 c)
        => Lerp(c, new Color32(128, 128, 128, c.a), 0.55f);

    // ── World-space drawing (SpriteRenderer + TextMeshPro) ──────────────────
    // The public-lobby browser draws itself in world space the way Perfect Comms'
    // VoiceLobbyBrowserUi does instead of on the overlay canvas: plates and text are
    // sized in world units and sorted on the game's "UI" layer, above ordinary HUD
    // art but below the 32760+ voice overlays.

    /// <summary>Sorting layer the game's own HUD renders on (Perfect Comms' VCSorting.Layer).</summary>
    public const string WorldSortLayer = "UI";

    /// <summary>Floor for a world panel's sorting orders: plate +1/+2, controls +3,
    /// rows +4, text +5 — all under the 32760+ voice HUD overlays.</summary>
    public const int WorldSortBase = 32752;

    private static readonly Dictionary<float, Sprite> _worldRounded = new();

    /// <summary>White rounded-rectangle sprite with the corner radius baked into the
    /// sprite border, so <see cref="WorldSprite"/> can nine-slice it to any size —
    /// scaling a whole sprite instead would smear the corners on non-square plates.</summary>
    public static Sprite WorldRounded(float radius)
    {
        const float ppu = 64f;
        const int s = 64;
        radius = Mathf.Clamp(radius, 1f / ppu, s * 0.5f / ppu);
        if (_worldRounded.TryGetValue(radius, out var cached) && cached != null) return cached;

        int rad = Mathf.Clamp(Mathf.RoundToInt(radius * ppu), 1, s / 2);
        var tex = new Texture2D(s, s, TextureFormat.RGBA32, false) { hideFlags = HideFlags.HideAndDontSave };
        for (int y = 0; y < s; y++)
        for (int x = 0; x < s; x++)
        {
            float cx = Mathf.Clamp(x + 0.5f, rad, s - rad);
            float cy = Mathf.Clamp(y + 0.5f, rad, s - rad);
            float dx = x + 0.5f - cx, dy = y + 0.5f - cy;
            tex.SetPixel(x, y, dx * dx + dy * dy <= rad * rad ? Color.white : Color.clear);
        }
        tex.Apply();
        var sprite = Sprite.Create(tex, new Rect(0, 0, s, s), new Vector2(0.5f, 0.5f), ppu, 0,
            SpriteMeshType.FullRect, new Vector4(rad, rad, rad, rad));
        sprite.hideFlags |= HideFlags.HideAndDontSave;
        sprite.name = "VC_WorldRounded" + rad;
        _worldRounded[radius] = sprite;
        return sprite;
    }

    /// <summary>Empty world-space node placed in <paramref name="parent"/>'s local
    /// coordinates. Goes through <see cref="VCUiKit.NewRect"/>: a fresh GameObject
    /// carries a plain Transform and casting that to RectTransform throws under IL2CPP.</summary>
    public static RectTransform WorldRect(string name, Transform parent, Vector3 localPosition)
    {
        var rt = VCUiKit.NewRect(parent, name);
        rt.localPosition = localPosition;
        rt.localScale = Vector3.one;
        rt.gameObject.layer = parent.gameObject.layer;
        return rt;
    }

    /// <summary>Nine-sliced world sprite plate. <paramref name="size"/> is in the
    /// parent's local (world) units, <paramref name="order"/> a <see cref="WorldSortBase"/> offset.</summary>
    public static SpriteRenderer WorldSprite(string name, Transform parent, Vector3 localPosition,
        Vector2 size, Sprite sprite, Color32 color, int order)
    {
        var rt = WorldRect(name, parent, localPosition);
        var sr = rt.gameObject.AddComponent<SpriteRenderer>();
        sr.sprite = sprite;
        sr.color = color;
        sr.drawMode = SpriteDrawMode.Sliced;
        sr.size = size;
        sr.sortingLayerName = WorldSortLayer;
        sr.sortingOrder = order;
        return sr;
    }

    /// <summary>World-space TextMeshPro sharing the plates' sorting layer, so a text's
    /// order alone decides what it covers.</summary>
    public static TextMeshPro WorldText(string name, Transform parent, Vector3 localPosition,
        string content, float fontSize, Color32 color, TextAlignmentOptions align, int order,
        Vector2 size, bool wordWrap = false)
    {
        var rt = WorldRect(name, parent, localPosition);
        var tmp = rt.gameObject.AddComponent<TextMeshPro>();
        var font = GameFont();
        if (font != null) tmp.font = font;
        tmp.text = content;
        tmp.fontSize = fontSize;
        tmp.color = color;
        tmp.alignment = align;
        tmp.richText = false;
        tmp.enableWordWrapping = wordWrap;
        tmp.rectTransform.sizeDelta = size;
        tmp.sortingLayerID = SortingLayer.NameToID(WorldSortLayer);
        tmp.sortingOrder = order;
        return tmp;
    }

    public sealed class PanelShell
    {
        public readonly GameObject Root;
        public readonly RectTransform RootRect;
        public readonly CanvasGroup Group;
        public readonly RectTransform HeaderRect;
        public readonly RectTransform RailRoot;
        public readonly RectTransform PaneRoot;
        public readonly RectTransform PaneClip;
        public readonly float Width;
        public readonly float Height;
        public readonly float RailWidth;
        public readonly float PaneWidth;
        public readonly float PaneHeight;

        private readonly Image _closeImg;
        private readonly RectTransform _closeRt;
        private readonly Image _closeBar1;
        private readonly Image _closeBar2;
        private float _closeScale = 1f;
        private readonly Action _onClose;
        private readonly bool _guided;
        private readonly Color32 _closeRestColor;
        private bool _dragging;
        private Vector2 _dragOffset;
        private Vector2 _userPosition;
        private Vector2 _layoutOffset;

        public PanelShell(
            string objName,
            string title,
            float w,
            float h,
            Action onClose,
            bool rail = true,
            bool backdrop = true,
            bool guided = false)
        {
            Width = w; Height = h;
            _onClose = onClose;
            _guided = guided;
            _closeRestColor = guided ? new Color32(20, 25, 33, 205) : ControlBg;

            Root = Rect(objName, Canvas.transform).gameObject;
            RootRect = Root.GetComponent<RectTransform>();
            RootRect.Anchor(new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f));
            RootRect.sizeDelta = new Vector2(w, h);
            _userPosition = Vector2.zero;
            _layoutOffset = Vector2.zero;
            ApplyRootPosition();

            Group = Root.AddComponent<CanvasGroup>();

            if (backdrop)
            {
                var backdropRt = Rect("Backdrop", RootRect);
                backdropRt.Anchor(new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f));
                backdropRt.sizeDelta = new Vector2(8000f, 8000f);
                backdropRt.anchoredPosition = Vector2.zero;
                backdropRt.SetAsFirstSibling();
                var backdropImg = backdropRt.gameObject.AddComponent<Image>();
                backdropImg.sprite = Solid(Color.white);
                backdropImg.color = Backdrop;
                backdropImg.raycastTarget = true;
            }

            var shadow = GlowImage("DropShadow", RootRect,
                guided ? new Color32(0, 0, 0, 112) : PanelShadow);
            shadow.rectTransform.Anchor(Vector2.zero, Vector2.one, new Vector2(0.5f, 0.5f));
            shadow.rectTransform.offsetMin = guided
                ? new Vector2(-26f, -30f)
                : new Vector2(-46f, -54f);
            shadow.rectTransform.offsetMax = guided
                ? new Vector2(26f, 22f)
                : new Vector2(46f, 38f);

            var rimGlow = GlowImage("RimGlow", RootRect,
                guided ? new Color32(34, 211, 238, 18) : AccentGlow);
            rimGlow.rectTransform.Anchor(Vector2.zero, Vector2.one, new Vector2(0.5f, 0.5f));
            float rimInset = guided ? 10f : 22f;
            rimGlow.rectTransform.offsetMin = new Vector2(-rimInset, -rimInset);
            rimGlow.rectTransform.offsetMax = new Vector2(rimInset, rimInset);

            var surface = Rect("Surface", RootRect);
            surface.Anchor(Vector2.zero, Vector2.one, new Vector2(0.5f, 0.5f));
            surface.offsetMin = Vector2.zero;
            surface.offsetMax = Vector2.zero;
            var surfaceImg = surface.gameObject.AddComponent<Image>();
            surfaceImg.sprite = PanelGradient();
            surfaceImg.type = Image.Type.Sliced;
            surfaceImg.color = Color.white;
            surfaceImg.raycastTarget = false;

            const float headerH = 76f;
            HeaderRect = Rect("Header", RootRect);
            HeaderRect.Anchor(new Vector2(0f, 1f), new Vector2(1f, 1f), new Vector2(0.5f, 1f));
            HeaderRect.sizeDelta = new Vector2(0f, headerH);
            HeaderRect.anchoredPosition = Vector2.zero;
            var headerBg = HeaderRect.gameObject.AddComponent<Image>();
            headerBg.sprite = guided ? Solid(Color.white) : HeaderGradient();
            headerBg.type = guided ? Image.Type.Simple : Image.Type.Sliced;
            headerBg.color = guided ? new Color32(16, 20, 27, 255) : Color.white;
            headerBg.raycastTarget = false;

            var headerDivider = Panel("HeaderDivider", HeaderRect, Divider, false);
            headerDivider.rectTransform.Anchor(new Vector2(0f, 0f), new Vector2(1f, 0f), new Vector2(0.5f, 0f));
            headerDivider.rectTransform.sizeDelta = new Vector2(0f, 1.5f);
            headerDivider.rectTransform.anchoredPosition = new Vector2(0f, 0f);

            float titleSize = guided ? 30f : 32f;
            var titleTmp = Text("Title", HeaderRect, title, titleSize, TextBright,
                TextAlignmentOptions.Left, FontStyles.Bold);
            titleTmp.characterSpacing = guided ? 1.5f : 6f;
            titleTmp.overflowMode = TextOverflowModes.Ellipsis;
            if (guided)
            {
                titleTmp.enableAutoSizing = true;
                titleTmp.fontSizeMin = 24f;
                titleTmp.fontSizeMax = titleSize;
            }
            titleTmp.rectTransform.Anchor(new Vector2(0f, 0.5f), new Vector2(1f, 0.5f), new Vector2(0f, 0.5f));
            titleTmp.rectTransform.sizeDelta = new Vector2(guided ? -170f : -200f, headerH);
            titleTmp.rectTransform.anchoredPosition = new Vector2(guided ? 32f : 38f, 0f);

            _closeRt = Rect("Close", HeaderRect);
            _closeRt.Anchor(new Vector2(1f, 0.5f), new Vector2(1f, 0.5f), new Vector2(1f, 0.5f));
            _closeRt.sizeDelta = new Vector2(46f, 46f);
            _closeRt.anchoredPosition = new Vector2(-24f, 0f);
            _closeImg = _closeRt.gameObject.AddComponent<Image>();
            _closeImg.sprite = Rounded(true);
            _closeImg.type = Image.Type.Sliced;
            _closeImg.color = _closeRestColor;
            _closeImg.raycastTarget = false;
            _closeBar1 = CloseBar(_closeRt, 45f);
            _closeBar2 = CloseBar(_closeRt, -45f);

            const float pad = 20f;
            const float clipPad = 8f;
            RailWidth = rail ? Mathf.Round(w * 0.25f) : 0f;

            RailRoot = Rect("Rail", RootRect);
            RailRoot.Anchor(new Vector2(0f, 0f), new Vector2(0f, 1f), new Vector2(0f, 0.5f));
            RailRoot.offsetMin = new Vector2(0f, 0f);
            RailRoot.offsetMax = new Vector2(RailWidth, -headerH);
            if (rail)
            {
                var railBg = RailRoot.gameObject.AddComponent<Image>();
                railBg.sprite = Rounded(true);
                railBg.type = Image.Type.Sliced;
                railBg.color = RailSurface;
                railBg.raycastTarget = false;

                var railDiv = Rect("RailDivider", RootRect);
                railDiv.Anchor(new Vector2(0f, 0f), new Vector2(0f, 1f), new Vector2(0f, 0.5f));
                railDiv.offsetMin = new Vector2(RailWidth, 0f);
                railDiv.offsetMax = new Vector2(RailWidth + 1.5f, -headerH);
                var railDivImg = railDiv.gameObject.AddComponent<Image>();
                railDivImg.sprite = Solid(Color.white);
                railDivImg.color = Divider;
                railDivImg.raycastTarget = false;
            }

            PaneWidth = w - RailWidth - pad * 2f;
            PaneHeight = h - headerH - pad * 2f;

            var inner = Panel("InnerPane", RootRect, PanelInner, true);
            // Guided/onboarding content must remain legible over the animated main menu.
            // The regular settings shell intentionally keeps its lighter translucent inset.
            inner.color = guided ? new Color32(10, 14, 20, 255) : new Color32(0, 0, 0, 70);
            inner.rectTransform.Anchor(new Vector2(0f, 0f), new Vector2(1f, 1f), new Vector2(0.5f, 0.5f));
            inner.rectTransform.offsetMin = new Vector2(RailWidth + pad, pad);
            inner.rectTransform.offsetMax = new Vector2(-pad, -headerH - pad);

            PaneClip = Rect("PaneClip", RootRect);
            PaneClip.Anchor(new Vector2(0f, 0f), new Vector2(1f, 1f), new Vector2(0.5f, 0.5f));
            PaneClip.offsetMin = new Vector2(RailWidth + pad + clipPad, pad + clipPad);
            PaneClip.offsetMax = new Vector2(-pad - clipPad, -headerH - pad - clipPad);
            PaneClip.gameObject.AddComponent<RectMask2D>();

            PaneRoot = Rect("PaneContent", PaneClip);
            PaneRoot.Anchor(new Vector2(0f, 1f), new Vector2(1f, 1f), new Vector2(0.5f, 1f));
            PaneRoot.offsetMin = new Vector2(0f, 0f);
            PaneRoot.offsetMax = new Vector2(0f, 0f);
            PaneRoot.sizeDelta = new Vector2(0f, 0f);
            PaneRoot.anchoredPosition = Vector2.zero;
        }

        private static Image CloseBar(RectTransform parent, float angle)
        {
            var rt = Rect("CloseBar", parent);
            rt.Anchor(new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f));
            rt.sizeDelta = new Vector2(22f, 3.4f);
            rt.anchoredPosition = Vector2.zero;
            rt.localRotation = Quaternion.Euler(0f, 0f, angle);
            var img = rt.gameObject.AddComponent<Image>();
            img.sprite = Rounded(true);
            img.type = Image.Type.Sliced;
            img.color = TextMuted;
            img.raycastTarget = false;
            return img;
        }

        public void TickHeader()
        {
            bool overClose = Contains(_closeRt);
            _closeImg.color = Lerp(_closeImg.color, overClose ? CloseHover : _closeRestColor, 0.3f);
            var barColor = Lerp(_closeBar1.color, overClose ? TextBright : TextMuted, 0.3f);
            _closeBar1.color = barColor;
            _closeBar2.color = barColor;
            _closeScale = Mathf.Lerp(_closeScale, overClose ? 1.14f : 1f, 0.3f);
            _closeRt.localScale = new Vector3(_closeScale, _closeScale, 1f);

            if (Input.GetMouseButtonDown(0))
            {
                if (overClose) { _onClose(); return; }
                if (!_guided && Contains(HeaderRect) && LocalPoint(CanvasRect, out var lp))
                {
                    _dragging = true;
                    _dragOffset = _userPosition - lp;
                }
            }
            else if (Input.GetMouseButton(0) && _dragging)
            {
                if (LocalPoint(CanvasRect, out var lp))
                {
                    _userPosition = lp + _dragOffset;
                    ApplyRootPosition();
                }
            }
            else
            {
                _dragging = false;
            }
        }

        public void SetLayoutOffset(Vector2 offset)
        {
            _layoutOffset = offset;
            ApplyRootPosition();
        }

        private void ApplyRootPosition()
        {
            if (RootRect != null)
                RootRect.anchoredPosition = _userPosition + _layoutOffset;
        }
    }

    public sealed class CategoryRail
    {
        private sealed class Item
        {
            public RectTransform Root = null!;
            public Image Hover = null!;
            public TextMeshProUGUI Label = null!;
            public float Y;
        }

        private readonly List<Item> _items = new();
        private int _selected;
        public int Selected => _selected;
        public Action<int>? OnSelect;

        private RectTransform _highlight = null!;
        private RectTransform _hiBar = null!;
        private float _hiY;
        private const float RowH = 62f;
        private const float ItemH = RowH - 8f;

        public void Build(RectTransform railRoot, float railWidth, string[] labels,
            (int index, string title)[]? sections = null)
        {
            const float top = -22f;
            const float sectionGap = 30f;

            var hiGlow = GlowImage("RailGlow", railRoot, AccentGlow);
            _highlight = Rect("RailHighlight", railRoot);
            _highlight.Anchor(new Vector2(0f, 1f), new Vector2(1f, 1f), new Vector2(0.5f, 1f));
            _highlight.sizeDelta = new Vector2(-16f, ItemH);
            var hiImg = _highlight.gameObject.AddComponent<Image>();
            hiImg.sprite = Rounded();
            hiImg.type = Image.Type.Sliced;
            hiImg.color = AccentFaint;
            hiImg.raycastTarget = false;
            hiGlow.rectTransform.SetParent(_highlight, false);
            hiGlow.rectTransform.Anchor(Vector2.zero, Vector2.one, new Vector2(0.5f, 0.5f));
            hiGlow.rectTransform.offsetMin = new Vector2(-6f, -6f);
            hiGlow.rectTransform.offsetMax = new Vector2(6f, 6f);

            _hiBar = Rect("RailBar", _highlight);
            _hiBar.Anchor(new Vector2(0f, 0.5f), new Vector2(0f, 0.5f), new Vector2(0f, 0.5f));
            _hiBar.sizeDelta = new Vector2(4f, ItemH - 14f);
            _hiBar.anchoredPosition = new Vector2(6f, 0f);
            var barImg = _hiBar.gameObject.AddComponent<Image>();
            barImg.sprite = Rounded(true);
            barImg.type = Image.Type.Sliced;
            barImg.color = Accent;
            barImg.raycastTarget = false;

            float y = top;
            for (int i = 0; i < labels.Length; i++)
            {
                if (sections != null)
                {
                    for (int sidx = 0; sidx < sections.Length; sidx++)
                    {
                        if (sections[sidx].index != i) continue;
                        y -= sectionGap;
                        BuildSection(railRoot, sections[sidx].title, y + ItemH * 0.5f);
                        break;
                    }
                }

                var rt = Rect("Cat_" + labels[i], railRoot);
                rt.Anchor(new Vector2(0f, 1f), new Vector2(1f, 1f), new Vector2(0.5f, 1f));
                rt.sizeDelta = new Vector2(-16f, ItemH);
                rt.anchoredPosition = new Vector2(0f, y);

                var hover = rt.gameObject.AddComponent<Image>();
                hover.sprite = Rounded();
                hover.type = Image.Type.Sliced;
                hover.color = Clear;
                hover.raycastTarget = false;

                var label = Text("Label", rt, labels[i], 19f, TextMuted, TextAlignmentOptions.Left, FontStyles.Bold);
                label.characterSpacing = 3f;
                label.rectTransform.Anchor(new Vector2(0f, 0.5f), new Vector2(1f, 0.5f), new Vector2(0f, 0.5f));
                label.rectTransform.sizeDelta = new Vector2(-32f, RowH);
                label.rectTransform.anchoredPosition = new Vector2(22f, 0f);

                _items.Add(new Item { Root = rt, Hover = hover, Label = label, Y = y });
                y -= RowH;
            }

            _hiY = _items.Count > 0 ? _items[_selected].Y : top;
            _highlight.anchoredPosition = new Vector2(0f, _hiY);
            Apply();
        }

        public void Tick()
        {
            for (int i = 0; i < _items.Count; i++)
            {
                bool sel = i == _selected;
                var it = _items[i];
                bool hover = !sel && Contains(it.Root);
                it.Hover.color = Lerp(it.Hover.color, hover ? RowHover : Clear, 0.25f);
                it.Label.color = Lerp(it.Label.color, sel ? Accent : (hover ? TextPrimary : TextMuted), 0.25f);
                if (hover && Input.GetMouseButtonDown(0)) Select(i);
            }

            if (_items.Count > 0)
            {
                float target = _items[_selected].Y;
                _hiY = Mathf.Lerp(_hiY, target, 0.3f);
                _highlight.anchoredPosition = new Vector2(0f, _hiY);
            }
        }

        public void Select(int idx)
        {
            if (idx < 0 || idx >= _items.Count || idx == _selected) { if (idx == _selected) return; }
            _selected = Mathf.Clamp(idx, 0, _items.Count - 1);
            Apply();
            OnSelect?.Invoke(_selected);
        }

        private void Apply()
        {
            for (int i = 0; i < _items.Count; i++)
                _items[i].Label.color = i == _selected ? Accent : TextMuted;
        }

        private static void BuildSection(RectTransform railRoot, string title, float y)
        {
            var div = Rect("RailSectionDiv", railRoot);
            div.Anchor(new Vector2(0f, 1f), new Vector2(1f, 1f), new Vector2(0.5f, 1f));
            div.sizeDelta = new Vector2(-32f, 1f);
            div.anchoredPosition = new Vector2(0f, y + 13f);
            var divImg = div.gameObject.AddComponent<Image>();
            divImg.sprite = Solid(Color.white);
            divImg.color = Divider;
            divImg.raycastTarget = false;

            var label = Text("RailSectionLabel", railRoot, title, 13f, TextFaint, TextAlignmentOptions.Left, FontStyles.Bold);
            label.characterSpacing = 4f;
            label.rectTransform.Anchor(new Vector2(0f, 1f), new Vector2(1f, 1f), new Vector2(0.5f, 1f));
            label.rectTransform.sizeDelta = new Vector2(-44f, 20f);
            label.rectTransform.anchoredPosition = new Vector2(22f, y - 4f);
        }
    }

    public abstract class Row
    {
        public RectTransform Root = null!;
        public Image? Hover;
        public TextMeshProUGUI Title = null!;
        private RectTransform? _helpRt;
        private Image? _helpBadge;
        private TextMeshProUGUI? _helpGlyph;
        private RectTransform? _clip;
        private string? _helpText;
        public float Height = 72f;
        protected float PaneW;
        public virtual void Tick(float dt) { }
        public virtual bool IsDragging => false;
        public virtual void OnMouseDown() { }
        public virtual void OnMouseDrag() { }
        public virtual void OnMouseUp() { }

        public const float EdgePad = 22f;
        public const float ColGap = 24f;
        public const float ValueColW = 110f;

        protected virtual float LabelColW => Mathf.Round(PaneW * 0.42f);
        protected float ControlLeft => EdgePad + LabelColW + ColGap;
        protected float ControlRight => PaneW - EdgePad - ValueColW - ColGap;
        // Never manufacture width past the allocated control column. The previous 120px
        // floor made narrow rows draw underneath their value pill instead of getting smaller.
        protected float ControlColW => Mathf.Max(1f, ControlRight - ControlLeft);

        protected bool PointerWithinClip => _clip == null || Contains(_clip);
        protected bool HelpHovered => _helpRt != null
            && _helpRt.gameObject.activeInHierarchy
            && PointerWithinClip
            && Contains(_helpRt);

        protected void BuildBase(
            RectTransform pane,
            string label,
            float width,
            float y,
            float height,
            string? helpText = null)
        {
            Height = height;
            PaneW = width;
            // Rows are usually direct children of PaneRoot, but setup groups them inside cards.
            // Resolve the real RectMask2D ancestor instead of assuming the immediate parent is
            // the clip; a zero-height intermediate page root otherwise disables hover/help hit
            // testing for every nested row.
            _clip = null;
            Transform? ancestor = pane;
            while (ancestor != null)
            {
                if (ancestor is RectTransform rt && rt.GetComponent<RectMask2D>() != null)
                {
                    _clip = rt;
                    break;
                }
                ancestor = ancestor.parent;
            }
            Root = Rect("Row", pane);
            Root.Anchor(new Vector2(0f, 1f), new Vector2(1f, 1f), new Vector2(0.5f, 1f));
            Root.sizeDelta = new Vector2(0f, height);
            Root.anchoredPosition = new Vector2(0f, y);

            Hover = Panel("Hover", Root, Clear, true);
            Hover.rectTransform.Anchor(Vector2.zero, Vector2.one, new Vector2(0.5f, 0.5f));
            Hover.rectTransform.offsetMin = new Vector2(0f, 3f);
            Hover.rectTransform.offsetMax = new Vector2(0f, -3f);

            var title = Text("RowLabel", Root, label, 22f, TextPrimary, TextAlignmentOptions.Left);
            title.overflowMode = TextOverflowModes.Ellipsis;
            title.enableAutoSizing = true;
            title.fontSizeMax = 22f;
            title.fontSizeMin = 17f;
            title.rectTransform.Anchor(new Vector2(0f, 0.5f), new Vector2(0f, 0.5f), new Vector2(0f, 0.5f));
            title.rectTransform.sizeDelta = new Vector2(LabelColW, height);
            title.rectTransform.anchoredPosition = new Vector2(EdgePad, 0f);
            Title = title;

            if (!string.IsNullOrWhiteSpace(helpText))
                BuildHelp(helpText);
        }

        private void BuildHelp(string helpText)
        {
            const float hitD = 26f;
            const float badgeD = 20f;
            const float gap = 6f;
            _helpText = helpText;

            float availableTitleW = Mathf.Max(40f, LabelColW - hitD - gap);
            Title.rectTransform.sizeDelta = new Vector2(availableTitleW, Height);
            float preferredW = Title.GetPreferredValues(Title.text).x;
            float iconX = Mathf.Min(preferredW, availableTitleW) + gap + hitD * 0.5f;

            _helpRt = Rect("Help", Title.rectTransform);
            _helpRt.Anchor(new Vector2(0f, 0.5f), new Vector2(0f, 0.5f), new Vector2(0.5f, 0.5f));
            _helpRt.sizeDelta = new Vector2(hitD, hitD);
            _helpRt.anchoredPosition = new Vector2(iconX, 0f);

            var badge = Rect("Badge", _helpRt);
            badge.Anchor(new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f));
            badge.sizeDelta = new Vector2(badgeD, badgeD);
            _helpBadge = badge.gameObject.AddComponent<Image>();
            _helpBadge.sprite = Rounded(true);
            _helpBadge.type = Image.Type.Sliced;
            _helpBadge.color = ControlBg;
            _helpBadge.raycastTarget = false;

            _helpGlyph = Text("Glyph", badge, "?", 17f, Accent,
                TextAlignmentOptions.Center, FontStyles.Bold);
            _helpGlyph.rectTransform.Anchor(Vector2.zero, Vector2.one, new Vector2(0.5f, 0.5f));
            _helpGlyph.rectTransform.offsetMin = Vector2.zero;
            _helpGlyph.rectTransform.offsetMax = Vector2.zero;
        }

        protected void TickHover()
        {
            if (Hover == null) return;
            bool over = PointerWithinClip && Contains(Root);
            Hover.color = Lerp(Hover.color, over ? RowHover : Clear, 0.22f);
            TickHelp();
        }

        private void TickHelp()
        {
            if (_helpRt == null || _helpBadge == null || _helpGlyph == null
                || string.IsNullOrWhiteSpace(_helpText)) return;
            bool over = HelpHovered;
            _helpBadge.color = Lerp(
                _helpBadge.color,
                over ? AccentFaint : ControlBg,
                0.28f);
            _helpGlyph.color = Lerp(
                _helpGlyph.color,
                over ? TextBright : Accent,
                0.28f);
            float scale = Mathf.Lerp(_helpRt.localScale.x, over ? 1.12f : 1f, 0.3f);
            _helpRt.localScale = new Vector3(scale, scale, 1f);
            if (over) RequestTooltip(Title.text, _helpText!);
        }
    }

    public sealed class ActionRow : Row
    {
        private const float ButtonWidth = 220f;
        private readonly Action _onClick;
        private readonly Func<bool> _enabled;
        private RectTransform _buttonRt = null!;
        private Image _button = null!;
        private Image _buttonGlow = null!;
        private TextMeshProUGUI _buttonLabel = null!;

        public ActionRow(Action onClick, Func<bool>? enabled = null)
        {
            _onClick = onClick ?? throw new ArgumentNullException(nameof(onClick));
            _enabled = enabled ?? (() => true);
        }

        protected override float LabelColW =>
            Mathf.Round(PaneW - EdgePad * 2f - ColGap - ButtonWidth);

        public ActionRow Build(
            RectTransform pane,
            string label,
            string buttonText,
            float width,
            float y,
            float height,
            string? helpText = null)
        {
            BuildBase(pane, label, width, y, height, helpText);

            _buttonRt = Rect("ActionButton", Root);
            _buttonRt.Anchor(new Vector2(1f, 0.5f), new Vector2(1f, 0.5f), new Vector2(1f, 0.5f));
            _buttonRt.sizeDelta = new Vector2(ButtonWidth, 40f);
            _buttonRt.anchoredPosition = new Vector2(-EdgePad, 0f);

            _buttonGlow = GlowImage("ActionButtonGlow", _buttonRt, Clear);
            _buttonGlow.rectTransform.Anchor(Vector2.zero, Vector2.one, new Vector2(0.5f, 0.5f));
            _buttonGlow.rectTransform.offsetMin = new Vector2(-10f, -10f);
            _buttonGlow.rectTransform.offsetMax = new Vector2(10f, 10f);

            _button = _buttonRt.gameObject.AddComponent<Image>();
            _button.sprite = Rounded();
            _button.type = Image.Type.Sliced;
            _button.color = ControlBg;
            _button.raycastTarget = false;

            _buttonLabel = Text("ActionButtonLabel", _buttonRt, buttonText, 17f,
                TextPrimary, TextAlignmentOptions.Center, FontStyles.Bold);
            _buttonLabel.characterSpacing = 1f;
            _buttonLabel.enableAutoSizing = true;
            _buttonLabel.fontSizeMax = 17f;
            _buttonLabel.fontSizeMin = 12f;
            _buttonLabel.overflowMode = TextOverflowModes.Ellipsis;
            _buttonLabel.rectTransform.Anchor(Vector2.zero, Vector2.one, new Vector2(0.5f, 0.5f));
            _buttonLabel.rectTransform.offsetMin = new Vector2(10f, 0f);
            _buttonLabel.rectTransform.offsetMax = new Vector2(-10f, 0f);
            return this;
        }

        public override void OnMouseDown()
        {
            if (!_enabled() || !PointerWithinClip || !Contains(_buttonRt)) return;
            RebindRow.CancelCaptureForExternalPointer();
            _onClick();
        }

        public override void Tick(float dt)
        {
            TickHover();
            bool enabled = _enabled();
            bool over = enabled && PointerWithinClip && Contains(_buttonRt);
            bool pressed = over && Input.GetMouseButton(0);

            _button.color = Lerp(
                _button.color,
                !enabled ? ToggleOffTrack : (over ? ControlHover : ControlBg),
                0.25f);
            _buttonLabel.color = Lerp(
                _buttonLabel.color,
                !enabled ? TextFaint : (over ? TextBright : TextPrimary),
                0.25f);
            _buttonGlow.color = Lerp(
                _buttonGlow.color,
                over ? AccentGlow : Clear,
                0.25f);

            float targetScale = pressed ? 0.95f : (over ? 1.03f : 1f);
            float scale = Mathf.Lerp(_buttonRt.localScale.x, targetScale, 0.35f);
            _buttonRt.localScale = new Vector3(scale, scale, 1f);
        }
    }

    public sealed class ToggleRow : Row
    {
        private readonly Func<bool> _get;
        private readonly Action<bool> _set;
        private readonly Func<bool> _enabled;
        private Image _track = null!;
        private Image _glow = null!;
        private RectTransform _knob = null!;
        private Image _knobImg = null!;
        private Image _knobShadow = null!;
        private float _knobT;
        private bool _stacked;
        private bool _dimmed;

        public ToggleRow(Func<bool> get, Action<bool> set, Func<bool>? enabled = null)
        {
            _get = get;
            _set = set;
            _enabled = enabled ?? (() => true);
        }

        protected override float LabelColW => _stacked
            ? Mathf.Round(PaneW - 24f)
            : Mathf.Round(PaneW - EdgePad * 2f - ColGap - 66f);

        public ToggleRow Build(
            RectTransform pane,
            string label,
            float width,
            float y,
            float height,
            string? helpText = null,
            bool stacked = false)
        {
            _stacked = stacked;
            BuildBase(pane, label, width, y, height, helpText);
            if (_stacked)
            {
                Title.fontSizeMin = 15f;
                Title.rectTransform.Anchor(
                    new Vector2(0f, 1f),
                    new Vector2(0f, 1f),
                    new Vector2(0f, 1f));
                Title.rectTransform.sizeDelta =
                    new Vector2(Title.rectTransform.sizeDelta.x, 20f);
                Title.rectTransform.anchoredPosition = new Vector2(12f, 0f);
            }

            var trackRt = Rect("Track", Root);
            if (_stacked)
            {
                trackRt.Anchor(
                    new Vector2(0.5f, 0f),
                    new Vector2(0.5f, 0f),
                    new Vector2(0.5f, 0f));
                trackRt.sizeDelta = new Vector2(66f, 34f);
                trackRt.anchoredPosition = Vector2.zero;
            }
            else
            {
                trackRt.Anchor(
                    new Vector2(0f, 0.5f),
                    new Vector2(0f, 0.5f),
                    new Vector2(0f, 0.5f));
                trackRt.sizeDelta = new Vector2(66f, 34f);
                trackRt.anchoredPosition = new Vector2(ControlLeft, 0f);
            }

            _glow = GlowImage("TrackGlow", trackRt, Clear);
            _glow.rectTransform.Anchor(Vector2.zero, Vector2.one, new Vector2(0.5f, 0.5f));
            _glow.rectTransform.offsetMin = new Vector2(-10f, -10f);
            _glow.rectTransform.offsetMax = new Vector2(10f, 10f);

            _track = trackRt.gameObject.AddComponent<Image>();
            _track.sprite = Rounded(true);
            _track.type = Image.Type.Sliced;
            _track.color = ToggleOffTrack;
            _track.raycastTarget = false;

            _knob = Rect("Knob", trackRt);
            _knob.Anchor(new Vector2(0f, 0.5f), new Vector2(0f, 0.5f), new Vector2(0.5f, 0.5f));
            _knob.sizeDelta = new Vector2(26f, 26f);

            _knobShadow = GlowImage("KnobShadow", _knob, new Color32(0, 0, 0, 130));
            _knobShadow.rectTransform.Anchor(Vector2.zero, Vector2.one, new Vector2(0.5f, 0.5f));
            _knobShadow.rectTransform.offsetMin = new Vector2(-5f, -7f);
            _knobShadow.rectTransform.offsetMax = new Vector2(5f, 3f);

            _knobImg = Rect("KnobFill", _knob).gameObject.AddComponent<Image>();
            _knobImg.rectTransform.Anchor(Vector2.zero, Vector2.one, new Vector2(0.5f, 0.5f));
            _knobImg.rectTransform.offsetMin = Vector2.zero;
            _knobImg.rectTransform.offsetMax = Vector2.zero;
            _knobImg.sprite = Rounded(true);
            _knobImg.type = Image.Type.Sliced;
            _knobImg.color = KnobOff;
            _knobImg.raycastTarget = false;

            _knobT = _get() ? 1f : 0f;
            ApplyKnob();
            return this;
        }

        private void ApplyKnob()
        {
            float e = _knobT * _knobT * (3f - 2f * _knobT);
            _knob.anchoredPosition = new Vector2(Mathf.Lerp(20f, 46f, e), 0f);
            _track.color = Lerp(ToggleOffTrack, Accent, _knobT);
            _knobImg.color = Lerp(KnobOff, TextBright, _knobT);
            var g = AccentGlow; g.a = (byte)(AccentGlow.a * _knobT); _glow.color = g;
        }

        public override void OnMouseDown()
        {
            if (Contains(Root) && !HelpHovered && _enabled())
            {
                RebindRow.CancelCaptureForExternalPointer();
                _set(!_get());
            }
        }

        public override void Tick(float dt)
        {
            TickHover();
            float target = _get() ? 1f : 0f;
            if (Mathf.Abs(_knobT - target) > 0.001f)
            {
                _knobT = Mathf.MoveTowards(_knobT, target, dt * 8f);
                ApplyKnob();
            }
            if (!_enabled())
            {
                _track.color = Lerp(_track.color, Dim(_track.color), 0.25f);
                _knobImg.color = Lerp(_knobImg.color, Dim(_knobImg.color), 0.25f);
                var g = _glow.color;
                g.a = (byte)(g.a * 0.6f);
                _glow.color = g;
                _dimmed = true;
            }
            else if (_dimmed)
            {
                // regained host rights (or otherwise re-enabled): restore the live colors
                _dimmed = false;
                ApplyKnob();
            }
        }
    }

    public sealed class SliderRow : Row
    {
        private readonly Func<float> _get;
        private readonly Action<float> _set;
        private readonly float _min, _max;
        private readonly Func<float, string> _fmt;
        private readonly bool _stacked;
        private readonly Func<bool> _enabled;
        private bool _dimmed;
        private RectTransform _hitBand = null!;
        private RectTransform _track = null!;
        private RectTransform _fill = null!;
        private Image _fillGlow = null!;
        private Image? _fillImage;
        private Image? _knobImage;
        private RectTransform _knob = null!;
        private TextMeshProUGUI _value = null!;
        private bool _dragging;
        public override bool IsDragging => _dragging;

        protected override float LabelColW => _stacked
            ? Mathf.Max(40f, PaneW - EdgePad * 2f - ValueColW - 14f)
            : base.LabelColW;

        public SliderRow(
            Func<float> get,
            Action<float> set,
            float min,
            float max,
            Func<float, string> fmt,
            bool stacked = false,
            Func<bool>? enabled = null)
        {
            _get = get;
            _set = set;
            _min = min;
            _max = max;
            _fmt = fmt;
            _stacked = stacked;
            _enabled = enabled ?? (() => true);
        }

        public SliderRow Build(
            RectTransform pane,
            string label,
            float width,
            float y,
            float height,
            string? helpText = null)
        {
            BuildBase(pane, label, width, y, height, helpText);

            var pill = Rect("Pill", Root);
            if (_stacked)
            {
                const float topControlHeight = 28f;
                Title.rectTransform.Anchor(new Vector2(0f, 1f), new Vector2(0f, 1f), new Vector2(0f, 1f));
                Title.rectTransform.sizeDelta = new Vector2(Title.rectTransform.sizeDelta.x, topControlHeight);
                Title.rectTransform.anchoredPosition = new Vector2(EdgePad, -1f);
                pill.Anchor(new Vector2(1f, 1f), new Vector2(1f, 1f), new Vector2(1f, 1f));
                pill.sizeDelta = new Vector2(ValueColW, topControlHeight);
                pill.anchoredPosition = new Vector2(-EdgePad, -1f);
            }
            else
            {
                pill.Anchor(new Vector2(1f, 0.5f), new Vector2(1f, 0.5f), new Vector2(1f, 0.5f));
                pill.sizeDelta = new Vector2(ValueColW, 36f);
                pill.anchoredPosition = new Vector2(-EdgePad, 0f);
            }
            var pillImg = pill.gameObject.AddComponent<Image>();
            pillImg.sprite = Rounded(true);
            pillImg.type = Image.Type.Sliced;
            pillImg.color = AccentFaint;
            pillImg.raycastTarget = false;

            _value = Text("Value", pill, "", 19f, Accent, TextAlignmentOptions.Center, FontStyles.Bold);
            if (_stacked)
            {
                _value.enableAutoSizing = true;
                _value.fontSizeMin = 13f;
                _value.fontSizeMax = 18f;
                _value.overflowMode = TextOverflowModes.Ellipsis;
            }
            _value.rectTransform.Anchor(Vector2.zero, Vector2.one, new Vector2(0.5f, 0.5f));
            _value.rectTransform.offsetMin = new Vector2(6f, 0f);
            _value.rectTransform.offsetMax = new Vector2(-6f, 0f);

            _track = Rect("Track", Root);
            _track.Anchor(
                _stacked ? new Vector2(0f, 1f) : new Vector2(0f, 0.5f),
                _stacked ? new Vector2(0f, 1f) : new Vector2(0f, 0.5f),
                new Vector2(0f, 0.5f));
            float trackW = _stacked ? Mathf.Max(1f, PaneW - EdgePad * 2f) : ControlColW;
            float trackHeight = _stacked ? 7f : 9f;
            _track.sizeDelta = new Vector2(trackW, trackHeight);
            _track.anchoredPosition = _stacked
                ? new Vector2(EdgePad, -Mathf.Max(38f, height - 10f))
                : new Vector2(ControlLeft, 0f);
            var trackImg = _track.gameObject.AddComponent<Image>();
            trackImg.sprite = Rounded(true);
            trackImg.type = Image.Type.Sliced;
            trackImg.color = TrackBg;
            trackImg.raycastTarget = false;

            // Keep the visual track slim, but give it a forgiving pointer/touch target.
            // The old 7px stacked track was needlessly difficult to grab at 720p/768p.
            _hitBand = Rect("TrackHitBand", Root);
            _hitBand.Anchor(
                _stacked ? new Vector2(0f, 1f) : new Vector2(0f, 0.5f),
                _stacked ? new Vector2(0f, 1f) : new Vector2(0f, 0.5f),
                new Vector2(0f, 0.5f));
            _hitBand.sizeDelta = new Vector2(trackW, _stacked ? 30f : 40f);
            _hitBand.anchoredPosition = _track.anchoredPosition;

            _fillGlow = GlowImage("FillGlow", _track, AccentGlow);
            _fillGlow.rectTransform.Anchor(new Vector2(0f, 0.5f), new Vector2(0f, 0.5f), new Vector2(0f, 0.5f));

            _fill = Rect("Fill", _track);
            _fill.Anchor(new Vector2(0f, 0.5f), new Vector2(0f, 0.5f), new Vector2(0f, 0.5f));
            _fill.sizeDelta = new Vector2(0f, trackHeight);
            var fillImg = _fill.gameObject.AddComponent<Image>();
            fillImg.sprite = Rounded(true);
            fillImg.type = Image.Type.Sliced;
            fillImg.color = Accent;
            fillImg.raycastTarget = false;
            _fillImage = fillImg;

            _knob = Rect("Knob", _track);
            _knob.Anchor(new Vector2(0f, 0.5f), new Vector2(0f, 0.5f), new Vector2(0.5f, 0.5f));
            float knobSize = _stacked ? 18f : 22f;
            _knob.sizeDelta = new Vector2(knobSize, knobSize);
            var knobShadow = GlowImage("KnobShadow", _knob, new Color32(0, 0, 0, 140));
            knobShadow.rectTransform.Anchor(Vector2.zero, Vector2.one, new Vector2(0.5f, 0.5f));
            knobShadow.rectTransform.offsetMin = new Vector2(-5f, -7f);
            knobShadow.rectTransform.offsetMax = new Vector2(5f, 3f);
            var knobImg = Rect("KnobFill", _knob).gameObject.AddComponent<Image>();
            knobImg.rectTransform.Anchor(Vector2.zero, Vector2.one, new Vector2(0.5f, 0.5f));
            knobImg.rectTransform.offsetMin = Vector2.zero;
            knobImg.rectTransform.offsetMax = Vector2.zero;
            knobImg.sprite = Rounded(true);
            knobImg.type = Image.Type.Sliced;
            knobImg.color = TextBright;
            knobImg.raycastTarget = false;
            _knobImage = knobImg;

            ApplyVisual(Normalized());
            return this;
        }

        private float Normalized() => Mathf.Approximately(_max, _min) ? 0f : Mathf.Clamp01((_get() - _min) / (_max - _min));

        private float _lastRaw = float.NaN;
        private void ApplyVisual(float t)
        {
            float w = _track.sizeDelta.x;
            float trackHeight = _stacked ? 7f : 9f;
            _fill.sizeDelta = new Vector2(w * t, trackHeight);
            _fillGlow.rectTransform.sizeDelta = new Vector2(w * t + 14f, _stacked ? 18f : 22f);
            _knob.anchoredPosition = new Vector2(w * t, 0f);
            float raw = _get();
            if (raw != _lastRaw)
            {
                _lastRaw = raw;
                _value.text = _fmt(raw);
            }
        }

        public override void OnMouseDown()
        {
            if (!_enabled()) return;
            if (Contains(_hitBand) || Contains(_knob))
            {
                RebindRow.CancelCaptureForExternalPointer();
                _dragging = true;
                ApplyFromMouse();
            }
        }

        public override void OnMouseDrag()
        {
            if (_dragging) ApplyFromMouse();
        }

        public override void OnMouseUp() => _dragging = false;

        private void ApplyFromMouse()
        {
            if (!LocalPoint(_track, out var lp)) return;
            float w = _track.sizeDelta.x;
            float t = Mathf.Clamp01(lp.x / w);
            _set(_min + t * (_max - _min));
            ApplyVisual(t);
        }

        public override void Tick(float dt)
        {
            TickHover();
            if (!_dragging) ApplyVisual(Normalized());
            if (!_enabled())
            {
                if (_fillImage != null)
                    _fillImage.color = Lerp(_fillImage.color, Dim(_fillImage.color), 0.25f);
                if (_knobImage != null)
                    _knobImage.color = Lerp(_knobImage.color, Dim(_knobImage.color), 0.25f);
                _dimmed = true;
            }
            else if (_dimmed)
            {
                // regained host rights (or otherwise re-enabled): restore the live colors
                _dimmed = false;
                if (_fillImage != null) _fillImage.color = Accent;
                if (_knobImage != null) _knobImage.color = TextBright;
            }
        }
    }

    /// <summary>Read-only live level meter: a slim track whose fill follows a
    /// 0..1 getter, plus a value pill. No pointer handling — used for the
    /// microphone test bar in the settings panel.</summary>
    public sealed class MeterRow : Row
    {
        private readonly Func<float> _get;
        private readonly Func<float, string> _fmt;
        private RectTransform _track = null!;
        private RectTransform _fill = null!;
        private Image _fillGlow = null!;
        private TextMeshProUGUI _value = null!;
        private float _smooth;

        private const float TrackHeight = 9f;

        public MeterRow(Func<float> get, Func<float, string> fmt)
        {
            _get = get ?? throw new ArgumentNullException(nameof(get));
            _fmt = fmt ?? throw new ArgumentNullException(nameof(fmt));
        }

        public MeterRow Build(
            RectTransform pane,
            string label,
            float width,
            float y,
            float height,
            string? helpText = null)
        {
            BuildBase(pane, label, width, y, height, helpText);

            var pill = Rect("Pill", Root);
            pill.Anchor(new Vector2(1f, 0.5f), new Vector2(1f, 0.5f), new Vector2(1f, 0.5f));
            pill.sizeDelta = new Vector2(ValueColW, 36f);
            pill.anchoredPosition = new Vector2(-EdgePad, 0f);
            var pillImg = pill.gameObject.AddComponent<Image>();
            pillImg.sprite = Rounded(true);
            pillImg.type = Image.Type.Sliced;
            pillImg.color = AccentFaint;
            pillImg.raycastTarget = false;

            _value = Text("Value", pill, "", 19f, Accent, TextAlignmentOptions.Center, FontStyles.Bold);
            _value.rectTransform.Anchor(Vector2.zero, Vector2.one, new Vector2(0.5f, 0.5f));
            _value.rectTransform.offsetMin = new Vector2(6f, 0f);
            _value.rectTransform.offsetMax = new Vector2(-6f, 0f);

            _track = Rect("Track", Root);
            _track.Anchor(new Vector2(0f, 0.5f), new Vector2(0f, 0.5f), new Vector2(0f, 0.5f));
            _track.sizeDelta = new Vector2(ControlColW, TrackHeight);
            _track.anchoredPosition = new Vector2(ControlLeft, 0f);
            var trackImg = _track.gameObject.AddComponent<Image>();
            trackImg.sprite = Rounded(true);
            trackImg.type = Image.Type.Sliced;
            trackImg.color = TrackBg;
            trackImg.raycastTarget = false;

            _fillGlow = GlowImage("FillGlow", _track, AccentGlow);
            _fillGlow.rectTransform.Anchor(new Vector2(0f, 0.5f), new Vector2(0f, 0.5f), new Vector2(0f, 0.5f));

            _fill = Rect("Fill", _track);
            _fill.Anchor(new Vector2(0f, 0.5f), new Vector2(0f, 0.5f), new Vector2(0f, 0.5f));
            _fill.sizeDelta = new Vector2(0f, TrackHeight);
            var fillImg = _fill.gameObject.AddComponent<Image>();
            fillImg.sprite = Rounded(true);
            fillImg.type = Image.Type.Sliced;
            fillImg.color = Accent;
            fillImg.raycastTarget = false;

            ApplyVisual(Mathf.Clamp01(_get()));
            return this;
        }

        private void ApplyVisual(float t)
        {
            float w = _track.sizeDelta.x;
            _fill.sizeDelta = new Vector2(w * t, TrackHeight);
            _fillGlow.rectTransform.sizeDelta = new Vector2(w * t + 14f, 22f);
            _value.text = _fmt(t);
        }

        public override void Tick(float dt)
        {
            TickHover();
            float raw = Mathf.Clamp01(_get());
            // Instant attack, exponential decay — the classic VU behaviour: peaks
            // register on the frame they happen, then fall away readable.
            _smooth = raw >= _smooth
                ? raw
                : Mathf.Lerp(_smooth, raw, 1f - Mathf.Exp(-9f * Mathf.Max(dt, 0f)));
            ApplyVisual(_smooth);
        }
    }

    public sealed class StepperRow : Row
    {
        private readonly Func<int> _getIndex;
        private readonly Action<int> _setIndex;
        private readonly Func<int> _count;
        private readonly Func<int, string> _labelOf;
        private readonly bool _fullWidthValue;
        private readonly bool _compactFullWidth;
        private Image _left = null!;
        private Image _right = null!;
        private TextMeshProUGUI _value = null!;
        private RectTransform _valuePill = null!;

        public StepperRow(
            Func<int> getIndex,
            Action<int> setIndex,
            Func<int> count,
            Func<int, string> labelOf,
            bool fullWidthValue = false,
            bool compactFullWidth = false)
        {
            _getIndex = getIndex;
            _setIndex = setIndex;
            _count = count;
            _labelOf = labelOf;
            _compactFullWidth = compactFullWidth;
            // Compact full-width is a distinct layout, so selecting it also selects the
            // wide value treatment even when the caller omits fullWidthValue: true.
            _fullWidthValue = fullWidthValue || compactFullWidth;
        }

        protected override float LabelColW => _compactFullWidth
            ? Mathf.Max(40f, PaneW - EdgePad * 2f)
            : base.LabelColW;

        public StepperRow Build(
            RectTransform pane,
            string label,
            float width,
            float y,
            float height,
            string? helpText = null)
        {
            BuildBase(pane, label, width, y, height, helpText);

            float groupW = _fullWidthValue
                ? PaneW - EdgePad * 2f
                : (PaneW - EdgePad) - ControlLeft;
            var group = Rect("Stepper", Root);
            if (_compactFullWidth)
            {
                const float groupHeight = 44f;
                float groupTop = Mathf.Min(40f, Mathf.Max(36f, height - groupHeight - 2f));
                group.Anchor(new Vector2(0f, 1f), new Vector2(0f, 1f), new Vector2(0f, 1f));
                group.sizeDelta = new Vector2(groupW, groupHeight);
                group.anchoredPosition = new Vector2(EdgePad, -groupTop);

                Title.rectTransform.Anchor(new Vector2(0f, 1f), new Vector2(0f, 1f), new Vector2(0f, 1f));
                Title.rectTransform.sizeDelta = new Vector2(Title.rectTransform.sizeDelta.x, 34f);
                Title.rectTransform.anchoredPosition = new Vector2(EdgePad, -2f);
            }
            else
            {
                group.Anchor(new Vector2(0f, 0.5f), new Vector2(0f, 0.5f), new Vector2(0f, 0.5f));
                group.sizeDelta = new Vector2(groupW, _fullWidthValue ? 76f : 40f);
                group.anchoredPosition = _fullWidthValue
                    ? new Vector2(EdgePad, -height * 0.23f)
                    : new Vector2(ControlLeft, 0f);
            }

            if (_fullWidthValue && !_compactFullWidth)
            {
                Title.rectTransform.sizeDelta = new Vector2(Title.rectTransform.sizeDelta.x, 42f);
                Title.rectTransform.anchoredPosition = new Vector2(EdgePad, height * 0.25f);
            }

            _valuePill = Rect("ValuePill", group);
            _valuePill.Anchor(new Vector2(0f, 0f), new Vector2(1f, 1f), new Vector2(0.5f, 0.5f));
            _valuePill.offsetMin = new Vector2(44f, 2f);
            _valuePill.offsetMax = new Vector2(-44f, -2f);
            var valuePillImg = _valuePill.gameObject.AddComponent<Image>();
            valuePillImg.sprite = Rounded(true);
            valuePillImg.type = Image.Type.Sliced;
            valuePillImg.color = AccentFaint;
            valuePillImg.raycastTarget = false;

            float valueSize = _compactFullWidth ? 20f : 18f;
            _value = Text("Value", _valuePill, "", valueSize, Accent, TextAlignmentOptions.Center, FontStyles.Bold);
            _value.overflowMode = _fullWidthValue
                ? TextOverflowModes.Overflow
                : TextOverflowModes.Ellipsis;
            _value.enableAutoSizing = _fullWidthValue;
            _value.fontSizeMax = valueSize;
            _value.fontSizeMin = _compactFullWidth ? 16f : 10f;
            _value.enableWordWrapping = _fullWidthValue;
            _value.richText = !_fullWidthValue;
            _value.rectTransform.Anchor(Vector2.zero, Vector2.one, new Vector2(0.5f, 0.5f));
            _value.rectTransform.offsetMin = new Vector2(8f, 0f);
            _value.rectTransform.offsetMax = new Vector2(-8f, 0f);

            _left = Arrow(group, "<", new Vector2(0f, 0.5f), new Vector2(0f, 0.5f), new Vector2(2f, 0f));
            _right = Arrow(group, ">", new Vector2(1f, 0.5f), new Vector2(1f, 0.5f), new Vector2(-2f, 0f));

            Refresh();
            return this;
        }

        private Image Arrow(RectTransform parent, string glyph, Vector2 aMin, Vector2 aMax, Vector2 pos)
        {
            var rt = Rect("Arrow", parent);
            rt.Anchor(aMin, aMax, new Vector2(aMin.x, 0.5f));
            rt.sizeDelta = new Vector2(38f, 38f);
            rt.anchoredPosition = pos;
            var img = rt.gameObject.AddComponent<Image>();
            img.sprite = Rounded();
            img.type = Image.Type.Sliced;
            img.color = ControlBg;
            img.raycastTarget = false;
            var t = Text("G", rt, glyph, 24f, TextMuted, TextAlignmentOptions.Center, FontStyles.Bold);
            t.rectTransform.Anchor(Vector2.zero, Vector2.one, new Vector2(0.5f, 0.5f));
            t.rectTransform.offsetMin = Vector2.zero;
            t.rectTransform.offsetMax = Vector2.zero;
            return img;
        }

        public void Refresh()
        {
            int n = _count();
            int i = n > 0 ? Mathf.Clamp(_getIndex(), 0, n - 1) : 0;
            if (n > 0)
            {
                _value.text = _labelOf(i);
                _value.color = Accent;
            }
            else
            {
                _value.text = _fullWidthValue ? "--" : "<color=#607282>--</color>";
                _value.color = _fullWidthValue ? TextFaint : Accent;
            }
        }

        public override void OnMouseDown()
        {
            int n = _count();
            if (n <= 0) return;
            int cur = Mathf.Clamp(_getIndex(), 0, n - 1);
            if (Contains(_left.GetComponent<RectTransform>()))
            {
                RebindRow.CancelCaptureForExternalPointer();
                _setIndex((cur - 1 + n) % n);
                Refresh();
            }
            else if (Contains(_right.GetComponent<RectTransform>()))
            {
                RebindRow.CancelCaptureForExternalPointer();
                _setIndex((cur + 1) % n);
                Refresh();
            }
        }

        public override void Tick(float dt)
        {
            TickHover();
            ArrowTick(_left);
            ArrowTick(_right);
            if (_fullWidthValue && PointerWithinClip && Contains(_valuePill))
            {
                int n = _count();
                int i = n > 0 ? Mathf.Clamp(_getIndex(), 0, n - 1) : 0;
                if (n > 0) RequestTooltip(
                    Title.text, _labelOf(i), plainTextDescription: true);
            }
        }

        private static void ArrowTick(Image arrow)
        {
            var rt = arrow.GetComponent<RectTransform>();
            bool over = Contains(rt);
            bool press = over && Input.GetMouseButton(0);
            arrow.color = Lerp(arrow.color, over ? ControlHover : ControlBg, 0.25f);
            float s = Mathf.Lerp(rt.localScale.x, press ? 0.9f : 1f, 0.35f);
            rt.localScale = new Vector3(s, s, 1f);
        }
    }

    /// <summary>
    /// Key capture row: clicking the button arms the row and the next key press —
    /// keyboard or mouse button — becomes the binding. Esc cancels, Delete/Backspace
    /// clears, and a modifier pressed on its own waits for either a follow-up key or
    /// its own release, so Ctrl/Shift/Alt can be bound too. Modifiers held at the moment
    /// of the press are stored with the key as one chord (<c>LeftControl+M</c>) and the
    /// row shows it compressed (<c>CTRL + M</c>). Only one row captures at a time; every
    /// other control calls <see cref="CancelCaptureForExternalPointer"/> before it acts
    /// on the same mouse press.
    /// </summary>
    public sealed class RebindRow : Row
    {
        private readonly Func<string> _getChord;
        private readonly Action<string> _setChord;
        private readonly Action _clear;

        private Image _btn = null!;
        private RectTransform _btnRt = null!;
        private TextMeshProUGUI _label = null!;
        private RectTransform _capRow = null!;
        private Image _clearBtn = null!;
        private Image _cancelBtn = null!;
        private float _normalRight;
        private float _captureRight;
        private bool _capturing;
        private bool _armed;
        private KeyCode _pendingModifier;

        private static RebindRow? _active;
        private static int _lastConsumedInputFrame = -1;
        private static KeyCode _suppressUntilPrimaryReleased;
        private static KeyCode _suppressUntilModifierReleased;

        private const float NormalBtnW = 185f;
        private const float NormalBtnH = 40f;
        private const float CapBtnW = 150f;
        private const float CapW = 170f;
        private const float CapH = 36f;
        private const float CapButtonW = 78f;

        /// <param name="getChord">Reads the stored chord string exactly as cfg holds it.</param>
        /// <param name="setChord">Writes a whole chord, modifiers joined with '+'
        /// (<c>LeftControl+M</c>) — never a bare KeyCode, so a combo binding survives.</param>
        public RebindRow(Func<string> getChord, Action<string> setChord, Action clear)
        {
            _getChord = getChord ?? throw new ArgumentNullException(nameof(getChord));
            _setChord = setChord ?? throw new ArgumentNullException(nameof(setChord));
            _clear = clear ?? throw new ArgumentNullException(nameof(clear));
        }

        // Same right-edge button as ActionRow, so the label column lines up with it.
        protected override float LabelColW =>
            Mathf.Round(PaneW - EdgePad * 2f - ColGap - NormalBtnW);

        public static bool IsCapturing => _active != null;

        /// <summary>True while a capture owns the keyboard, on the frame it consumed an
        /// input, and until the consumed keys are released again.</summary>
        public static bool ShouldSuppressKeybinds
        {
            get
            {
                if (_active != null || _lastConsumedInputFrame == Time.frameCount) return true;
                if (_suppressUntilPrimaryReleased == KeyCode.None
                    && _suppressUntilModifierReleased == KeyCode.None) return false;
                if (IsHeld(_suppressUntilPrimaryReleased)
                    || IsHeld(_suppressUntilModifierReleased)) return true;

                _suppressUntilPrimaryReleased = KeyCode.None;
                _suppressUntilModifierReleased = KeyCode.None;
                return false;
            }
        }

        public static void CancelCapture()
        {
            if (_active != null) _active.EndCapture();
        }

        /// <summary>Another control is about to handle this mouse press: end the capture
        /// and eat Mouse0 so the click is not swallowed as the new binding.</summary>
        public static void CancelCaptureForExternalPointer()
        {
            if (_active == null) return;
            _active.EndCapture();
            ConsumeCaptureInput(KeyCode.Mouse0);
        }

        public RebindRow Build(
            RectTransform pane,
            string label,
            float width,
            float y,
            float height,
            string? helpText = null)
        {
            BuildBase(pane, label, width, y, height, helpText);

            _normalRight = -EdgePad;
            _captureRight = -(EdgePad + CapW + ColGap);

            _btnRt = Rect("Bind", Root);
            _btnRt.Anchor(new Vector2(1f, 0.5f), new Vector2(1f, 0.5f), new Vector2(1f, 0.5f));
            _btnRt.sizeDelta = new Vector2(NormalBtnW, NormalBtnH);
            _btnRt.anchoredPosition = new Vector2(_normalRight, 0f);
            _btn = _btnRt.gameObject.AddComponent<Image>();
            _btn.sprite = Rounded();
            _btn.type = Image.Type.Sliced;
            _btn.color = ControlBg;
            _btn.raycastTarget = false;

            _label = Text("Key", _btnRt, "", 17f, TextPrimary,
                TextAlignmentOptions.Center, FontStyles.Bold);
            _label.enableAutoSizing = true;
            _label.fontSizeMax = 17f;
            _label.fontSizeMin = 11f;
            _label.overflowMode = TextOverflowModes.Ellipsis;
            _label.rectTransform.Anchor(Vector2.zero, Vector2.one, new Vector2(0.5f, 0.5f));
            _label.rectTransform.offsetMin = new Vector2(10f, 0f);
            _label.rectTransform.offsetMax = new Vector2(-10f, 0f);

            _capRow = Rect("CapControls", Root);
            _capRow.Anchor(new Vector2(1f, 0.5f), new Vector2(1f, 0.5f), new Vector2(1f, 0.5f));
            _capRow.sizeDelta = new Vector2(CapW, CapH);
            _capRow.anchoredPosition = new Vector2(-EdgePad, 0f);
            _clearBtn = CapButton(_capRow, TranslationHelper.Get("vc.common.clear", "Clear"), new Vector2(0f, 0.5f), new Vector2(0f, 0.5f), DangerDim);
            _cancelBtn = CapButton(_capRow, TranslationHelper.Get("vc.common.cancel", "Cancel"), new Vector2(1f, 0.5f), new Vector2(1f, 0.5f), ControlBg);
            _capRow.gameObject.SetActive(false);

            RefreshLabel();
            return this;
        }

        private void SetCapLayout(bool capturing)
        {
            if (capturing)
            {
                // The button slides left to make room for Clear/Cancel and the row
                // label steps aside — the button itself becomes the prompt.
                _btnRt.anchoredPosition = new Vector2(_captureRight, 0f);
                _btnRt.sizeDelta = new Vector2(CapBtnW, NormalBtnH);
                _capRow.gameObject.SetActive(true);
                Title.gameObject.SetActive(false);
            }
            else
            {
                _btnRt.anchoredPosition = new Vector2(_normalRight, 0f);
                _btnRt.sizeDelta = new Vector2(NormalBtnW, NormalBtnH);
                _capRow.gameObject.SetActive(false);
                Title.gameObject.SetActive(true);
            }
        }

        private static Image CapButton(
            RectTransform parent,
            string text,
            Vector2 aMin,
            Vector2 aMax,
            Color32 color)
        {
            var rt = Rect("Cap_" + text, parent);
            rt.Anchor(aMin, aMax, new Vector2(aMin.x, 0.5f));
            rt.sizeDelta = new Vector2(CapButtonW, CapH);
            rt.anchoredPosition = Vector2.zero;
            var img = rt.gameObject.AddComponent<Image>();
            img.sprite = Rounded(true);
            img.type = Image.Type.Sliced;
            img.color = color;
            img.raycastTarget = false;
            var t = Text("T", rt, text, 16f, TextBright,
                TextAlignmentOptions.Center, FontStyles.Bold);
            t.rectTransform.Anchor(Vector2.zero, Vector2.one, new Vector2(0.5f, 0.5f));
            t.rectTransform.offsetMin = Vector2.zero;
            t.rectTransform.offsetMax = Vector2.zero;
            return img;
        }

        private void RefreshLabel()
        {
            if (_capturing)
            {
                if (!_armed)
                {
                    _label.text = "<color=#8C9CB2>Release to bind...</color>";
                }
                else if (_pendingModifier != KeyCode.None)
                {
                    _label.text =
                        $"<color=#22D3EE>Release for {FormatKey(_pendingModifier)}, or press a key</color>";
                }
                else
                {
                    _label.text = "<color=#22D3EE>Press any key...</color>";
                }
                return;
            }

            _label.text = FormatChord(_getChord());
        }

        private void EndCapture()
        {
            _capturing = false;
            _armed = false;
            _pendingModifier = KeyCode.None;
            if (_active == this) _active = null;
            SetCapLayout(false);
            RefreshLabel();
        }

        public override void OnMouseDown()
        {
            if (_capturing) return;
            if (!PointerWithinClip || !Contains(_btnRt)) return;
            CancelCaptureForExternalPointer();
            _capturing = true;
            _armed = false;
            _pendingModifier = KeyCode.None;
            _active = this;
            SetCapLayout(true);
            RefreshLabel();
        }

        public override void Tick(float dt)
        {
            TickHover();

            bool over = !_capturing && PointerWithinClip && Contains(_btnRt);
            bool pressed = over && Input.GetMouseButton(0);
            _btn.color = Lerp(
                _btn.color,
                _capturing ? AccentFaint : (over ? ControlHover : ControlBg),
                0.25f);
            float scale = Mathf.Lerp(
                _btnRt.localScale.x,
                pressed ? 0.95f : (over ? 1.03f : 1f),
                0.35f);
            _btnRt.localScale = new Vector3(scale, scale, 1f);

            if (!_capturing) return;

            _clearBtn.color = Lerp(
                _clearBtn.color,
                Contains(_clearBtn.GetComponent<RectTransform>()) ? Danger : DangerDim,
                0.25f);
            _cancelBtn.color = Lerp(
                _cancelBtn.color,
                Contains(_cancelBtn.GetComponent<RectTransform>()) ? ControlHover : ControlBg,
                0.25f);

            // The click that started the capture is still held; wait for its release
            // before any key — including Mouse0 — may be read as the binding.
            if (!_armed)
            {
                if (Input.GetMouseButtonUp(0)) { _armed = true; RefreshLabel(); }
                return;
            }

            if (Input.GetKeyDown(KeyCode.Escape))
            { ConsumeCaptureInput(KeyCode.Escape); EndCapture(); return; }
            if (Input.GetKeyDown(KeyCode.Delete))
            { _clear(); ConsumeCaptureInput(KeyCode.Delete); EndCapture(); return; }
            if (Input.GetKeyDown(KeyCode.Backspace))
            { _clear(); ConsumeCaptureInput(KeyCode.Backspace); EndCapture(); return; }

            for (int m = 0; m <= 6; m++)
            {
                if (!Input.GetMouseButtonDown(m)) continue;
                if (Contains(_clearBtn.GetComponent<RectTransform>()))
                { _clear(); ConsumeCaptureInput(MouseToKey(m)); EndCapture(); return; }
                if (Contains(_cancelBtn.GetComponent<RectTransform>()))
                { ConsumeCaptureInput(MouseToKey(m)); EndCapture(); return; }
                // Left click stays a click and never a binding: it is how rows, pills
                // and buttons are pressed, so binding it would fire the hotkey on every
                // ordinary press. Clicking elsewhere instead cancels the capture —
                // pressing away always means "not this key".
                if (m == 0) { EndCapture(); return; }
                Commit(MouseToKey(m));
                return;
            }

            // A modifier on its own is a valid binding, but only once it is clear
            // whether another key follows it: hold it as pending until the release
            // or until a real key shows up.
            if (_pendingModifier == KeyCode.None)
            {
                var pressedModifier = PressedModifier();
                if (pressedModifier != KeyCode.None)
                {
                    _pendingModifier = pressedModifier;
                    RefreshLabel();
                }
            }

            foreach (var kc in _keyCandidates)
            {
                if (kc == KeyCode.Escape || kc == KeyCode.Delete || kc == KeyCode.Backspace) continue;
                if (IsModifierKey(kc)) continue;
                if (Input.GetKeyDown(kc)) { Commit(kc); return; }
            }

            if (_pendingModifier != KeyCode.None && Input.GetKeyUp(_pendingModifier))
                Commit(_pendingModifier);
        }

        private void Commit(KeyCode key)
        {
            // Whatever modifier class is held on the press rides along with the key, so
            // Ctrl+M persists as "LeftControl+M" and can only ever fire with Ctrl down.
            // The key's own class is stripped first — binding Ctrl on its own records Ctrl
            // as the key, never as key *and* as a requirement it must keep satisfying.
            var mods = VoiceConfig.HeldMods() & ~VoiceConfig.ModOf(key);
            _setChord(BuildChord(mods, key));
            ConsumeCaptureInput(key, _pendingModifier);
            EndCapture();
        }

        /// <summary>Modifiers first in a fixed order, then the key — the exact shape
        /// <see cref="VoiceConfig"/> parses back, so a chord round-trips through cfg
        /// unchanged however many times the row is rewritten.</summary>
        private static string BuildChord(VoiceChordMods mods, KeyCode key)
        {
            if (mods == VoiceChordMods.None) return key.ToString();
            var parts = new List<string>(5);
            if ((mods & VoiceChordMods.Ctrl) != 0) parts.Add(KeyCode.LeftControl.ToString());
            if ((mods & VoiceChordMods.Shift) != 0) parts.Add(KeyCode.LeftShift.ToString());
            if ((mods & VoiceChordMods.Alt) != 0) parts.Add(KeyCode.LeftAlt.ToString());
            if ((mods & VoiceChordMods.Cmd) != 0) parts.Add(KeyCode.LeftCommand.ToString());
            parts.Add(key.ToString());
            return string.Join("+", parts);
        }

        /// <summary>How the row draws a stored chord: each modifier class collapsed to a
        /// short name (cfg value <c>LeftControl+M</c> reads as "CTRL + M"), the key itself
        /// through <see cref="FormatKey"/>, and a cleared or unparseable entry as None.</summary>
        private static string FormatChord(string chord)
        {
            string none = "<color=#607282>" + TranslationHelper.Get("vc.key.none", "None") + "</color>";
            if (string.IsNullOrWhiteSpace(chord)) return none;
            var parts = new List<string>(4);
            foreach (var raw in chord.Split('+'))
            {
                var tok = raw.Trim();
                if (tok.Length == 0) continue;
                if (!Enum.TryParse(tok, true, out KeyCode kc)) continue;
                // A cleared binding is KeyCode.None — filtered on the code itself, not
                // the (translated) label, so "None" can read as 无. Duplicates would mean
                // the same modifier captured twice (Ctrl held + Ctrl pressed again).
                if (kc == KeyCode.None) continue;
                var label = kc switch
                {
                    KeyCode.LeftControl or KeyCode.RightControl => "CTRL",
                    KeyCode.LeftShift or KeyCode.RightShift => "SHIFT",
                    KeyCode.LeftAlt or KeyCode.RightAlt or KeyCode.AltGr => "ALT",
                    KeyCode.LeftCommand or KeyCode.RightCommand
                        or KeyCode.LeftWindows or KeyCode.RightWindows => "CMD",
                    _ => FormatKey(kc),
                };
                if (parts.Contains(label)) continue;
                parts.Add(label);
            }
            return parts.Count == 0 ? none : string.Join(" + ", parts);
        }

        private static void ConsumeCaptureInput(
            KeyCode primary = KeyCode.None,
            KeyCode modifier = KeyCode.None)
        {
            _lastConsumedInputFrame = Time.frameCount;
            _suppressUntilPrimaryReleased = primary;
            _suppressUntilModifierReleased = modifier;
        }

        private static bool IsHeld(KeyCode key)
            => key != KeyCode.None && Input.GetKey(key);

        private static bool IsModifierKey(KeyCode k)
            => k is KeyCode.LeftShift or KeyCode.RightShift
                or KeyCode.LeftControl or KeyCode.RightControl
                or KeyCode.LeftAlt or KeyCode.RightAlt or KeyCode.AltGr
                or KeyCode.LeftCommand or KeyCode.RightCommand
                or KeyCode.LeftWindows or KeyCode.RightWindows;

        private static KeyCode PressedModifier()
        {
            foreach (var modifier in _modifierCandidates)
                if (Input.GetKeyDown(modifier)) return modifier;
            return KeyCode.None;
        }

        private static readonly KeyCode[] _modifierCandidates =
        {
            KeyCode.LeftShift, KeyCode.RightShift,
            KeyCode.LeftControl, KeyCode.RightControl,
            KeyCode.LeftAlt, KeyCode.RightAlt, KeyCode.AltGr,
            KeyCode.LeftCommand, KeyCode.RightCommand,
            KeyCode.LeftWindows, KeyCode.RightWindows,
        };

        private static KeyCode MouseToKey(int m) => m switch
        {
            0 => KeyCode.Mouse0,
            1 => KeyCode.Mouse1,
            2 => KeyCode.Mouse2,
            3 => KeyCode.Mouse3,
            4 => KeyCode.Mouse4,
            5 => KeyCode.Mouse5,
            6 => KeyCode.Mouse6,
            _ => KeyCode.None
        };

        private static string FormatKey(KeyCode key) => key switch
        {
            KeyCode.None => TranslationHelper.Get("vc.key.none", "None"),
            KeyCode.LeftShift => TranslationHelper.Get("vc.key.leftShift", "Left Shift"),
            KeyCode.RightShift => TranslationHelper.Get("vc.key.rightShift", "Right Shift"),
            KeyCode.LeftControl => TranslationHelper.Get("vc.key.leftCtrl", "Left Ctrl"),
            KeyCode.RightControl => TranslationHelper.Get("vc.key.rightCtrl", "Right Ctrl"),
            KeyCode.LeftAlt => TranslationHelper.Get("vc.key.leftAlt", "Left Alt"),
            KeyCode.RightAlt => TranslationHelper.Get("vc.key.rightAlt", "Right Alt"),
            KeyCode.AltGr => "AltGr",
            KeyCode.LeftCommand => TranslationHelper.Get("vc.key.leftCommand", "Left Command"),
            KeyCode.RightCommand => TranslationHelper.Get("vc.key.rightCommand", "Right Command"),
            KeyCode.LeftWindows => TranslationHelper.Get("vc.key.leftWindows", "Left Windows"),
            KeyCode.RightWindows => TranslationHelper.Get("vc.key.rightWindows", "Right Windows"),
            KeyCode.Mouse0 => "MB1",
            KeyCode.Mouse1 => "MB2",
            KeyCode.Mouse2 => "MB3",
            KeyCode.Mouse3 => "MB4",
            KeyCode.Mouse4 => "MB5",
            KeyCode.Mouse5 => "MB6",
            KeyCode.Mouse6 => "MB7",
            _ => key.ToString(),
        };

        private static readonly KeyCode[] _keyCandidates = BuildKeyCandidates();

        private static KeyCode[] BuildKeyCandidates()
        {
            var list = new List<KeyCode>();
            foreach (var v in Enum.GetValues(typeof(KeyCode)))
            {
                var kc = (KeyCode)v;
                if ((int)kc >= (int)KeyCode.Mouse0 && (int)kc <= (int)KeyCode.Mouse6) continue;
                list.Add(kc);
            }
            return list.ToArray();
        }
    }

    /// <summary>
    /// Read-only setting display: the label column plus a value field spanning the
    /// control column, used for values that are edited in the config file.
    /// </summary>
    public sealed class ValueRow : Row
    {
        private TextMeshProUGUI _value = null!;

        public ValueRow Build(
            RectTransform pane,
            string label,
            string value,
            float width,
            float y,
            float height,
            string? helpText = null)
        {
            BuildBase(pane, label, width, y, height, helpText);

            var field = Rect("ValueField", Root);
            field.Anchor(new Vector2(0f, 0.5f), new Vector2(0f, 0.5f), new Vector2(0f, 0.5f));
            float fieldW = Mathf.Max(1f, (PaneW - EdgePad) - ControlLeft);
            field.sizeDelta = new Vector2(fieldW, 36f);
            field.anchoredPosition = new Vector2(ControlLeft, 0f);
            var fieldImg = field.gameObject.AddComponent<Image>();
            fieldImg.sprite = Rounded(true);
            fieldImg.type = Image.Type.Sliced;
            fieldImg.color = ControlBg;
            fieldImg.raycastTarget = false;

            _value = Text("Value", field, value, 18f, TextPrimary, TextAlignmentOptions.Left, FontStyles.Normal);
            _value.overflowMode = TextOverflowModes.Ellipsis;
            _value.enableAutoSizing = true;
            _value.fontSizeMax = 18f;
            _value.fontSizeMin = 12f;
            _value.rectTransform.Anchor(Vector2.zero, Vector2.one, new Vector2(0.5f, 0.5f));
            _value.rectTransform.offsetMin = new Vector2(14f, 0f);
            _value.rectTransform.offsetMax = new Vector2(-14f, 0f);
            return this;
        }

        public override void Tick(float dt) => TickHover();
    }

    /// <summary>
    /// Full-width informational line used when a whole group of settings is unavailable
    /// on the current platform.
    /// </summary>
    public sealed class InfoRow : Row
    {
        public InfoRow Build(
            RectTransform pane,
            string message,
            float width,
            float y,
            float height)
        {
            BuildBase(pane, message, width, y, height);
            Title.color = TextMuted;
            Title.fontSizeMax = 19f;
            Title.fontSizeMin = 14f;
            Title.rectTransform.sizeDelta = new Vector2(PaneW - EdgePad * 2f, Height);
            return this;
        }

        public override void Tick(float dt) => TickHover();
    }

    /// <summary>
    /// Standalone live level bar (track + fill) used inside the player volume rows:
    /// instant attack, linear release, green → yellow → red across the 0..1 range.
    /// </summary>
    public sealed class LiveLevelMeter
    {
        private readonly RectTransform _root;
        private readonly RectTransform _fill;
        private readonly Image _fillImage;
        private readonly float _width;
        private readonly float _height;
        private float _display;
        private float _target;
        private bool _speaking;

        public LiveLevelMeter(RectTransform parent, string name, float width, float height = 6f)
        {
            _width = Mathf.Max(1f, width);
            _height = Mathf.Max(2f, height);

            _root = Rect(name, parent);
            _root.sizeDelta = new Vector2(_width, _height);
            var trackImage = _root.gameObject.AddComponent<Image>();
            trackImage.sprite = Rounded(true);
            trackImage.type = Image.Type.Sliced;
            trackImage.color = TrackBg;
            trackImage.raycastTarget = false;

            _fill = Rect("Fill", _root);
            _fill.Anchor(new Vector2(0f, 0.5f), new Vector2(0f, 0.5f), new Vector2(0f, 0.5f));
            _fill.sizeDelta = new Vector2(0f, _height);
            _fill.anchoredPosition = Vector2.zero;
            _fillImage = _fill.gameObject.AddComponent<Image>();
            _fillImage.sprite = Rounded(true);
            _fillImage.type = Image.Type.Sliced;
            _fillImage.color = LevelMeterGreen;
            _fillImage.raycastTarget = false;
        }

        public RectTransform Root => _root;
        public float DisplayLevel => _display;
        public bool IsSpeaking => _speaking;

        public void SetLevel(float level, bool speaking)
        {
            _target = Mathf.Sqrt(Mathf.Clamp01(level));
            _speaking = speaking;
        }

        public void ClearImmediately()
        {
            _target = 0f;
            _display = 0f;
            _speaking = false;
            _fill.sizeDelta = new Vector2(0f, _height);
            _fillImage.color = LevelMeterGreen;
        }

        public void Tick(float dt)
        {
            dt = Mathf.Max(0f, dt);
            _display = _target >= _display
                ? _target
                : Mathf.Max(_target, _display - LevelMeterRelease * dt);
            _fill.sizeDelta = new Vector2(_width * _display, _height);
            _fillImage.color = LevelMeterColor(_display);
        }
    }

    private static Color LevelMeterColor(float t) => t < 0.55f
        ? Color.Lerp(LevelMeterGreen, LevelMeterYellow, t / 0.55f)
        : Color.Lerp(LevelMeterYellow, LevelMeterRed, (t - 0.55f) / 0.45f);

    /// <summary>
    /// Per-player volume row for the F3 window: avatar + name with a live level
    /// meter, a 0..2 volume slider, a percentage pill and a reset button. The row
    /// anatomy follows the Perfect Comms volume menu exactly (avatar 50, text
    /// column 156, track 9 with a 22 knob and a 2px midpoint mark, pill 60x34,
    /// reset 36x36); only the avatar/reset-icon asset lookup is local to
    /// Interstellar, which ships neither the crewmate renderer nor the icon.
    /// </summary>
    public sealed class PlayerVolumeRow : Row
    {
        private readonly Func<float> _get;
        private readonly Action<float> _onChange;
        private readonly Action _onCommit;
        private readonly PlayerControl? _pc;
        private readonly float _min, _max;
        private readonly Func<bool>? _enabled;
        /// <summary>Auto-volume state/toggle — Interstellar has no Perfect-Comms
        /// equivalent, so it rides as a compact chip beside the value pill. Null
        /// toggle = no chip (row geometry then matches the reference exactly).</summary>
        private readonly Func<bool>? _autoGet;
        private readonly Action? _autoToggle;

        /// <summary>Voice client this row edits, so the window can feed the meter.</summary>
        public byte PlayerId;

        private RectTransform _track = null!;
        private RectTransform _fill = null!;
        private Image _fillImg = null!;
        private RectTransform _knob = null!;
        private Image _knobImg = null!;
        private LiveLevelMeter _levelMeter = null!;
        private Image _avatarGlow = null!;
        private Image _avatarImg = null!;
        private RectTransform _resetRt = null!;
        private Image _resetImg = null!;
        private RectTransform _autoRt = null!;
        private Image _autoImg = null!;
        private TextMeshProUGUI _autoLabel = null!;
        private TextMeshProUGUI _value = null!;
        private Color _glowColor;
        private float _trackW;
        private bool _dragging;
        private bool _changed;
        private bool _dimmed;
        public override bool IsDragging => _dragging;

        private static readonly Color32 FillLow   = new(46, 82, 140, 235);
        private static readonly Color32 FillBoost = new(235, 171, 61, 235);

        private static Sprite? _resetIcon;
        private static Sprite ResetIcon => _resetIcon ??= BuildResetIcon();

        public PlayerVolumeRow(
            Func<float> get,
            Action<float> onChange,
            Action onCommit,
            PlayerControl? pc,
            float min,
            float max,
            Func<bool>? enabled = null,
            Func<bool>? autoGet = null,
            Action? autoToggle = null)
        {
            _get = get;
            _onChange = onChange;
            _onCommit = onCommit;
            _pc = pc;
            _min = min;
            _max = max;
            _enabled = enabled;
            _autoGet = autoGet;
            _autoToggle = autoToggle;
        }

        public PlayerVolumeRow Build(RectTransform pane, string name, float width, float y, float height)
        {
            BuildBase(pane, "", width, y, height);

            const float avatarD = 50f;
            float avatarCx = EdgePad + avatarD * 0.5f;
            float textLeft = EdgePad + avatarD + 14f;
            const float textColW = 156f;

            _avatarGlow = GlowImage("AvatarGlow", Root, Clear);
            _avatarGlow.rectTransform.Anchor(new Vector2(0f, 0.5f), new Vector2(0f, 0.5f), new Vector2(0.5f, 0.5f));
            _avatarGlow.rectTransform.sizeDelta = new Vector2(avatarD + 22f, avatarD + 22f);
            _avatarGlow.rectTransform.anchoredPosition = new Vector2(avatarCx, 0f);

            _glowColor = CrewmateIcon.ColorFor(_pc);

            var avatarRt = Rect("Avatar", Root);
            avatarRt.Anchor(new Vector2(0f, 0.5f), new Vector2(0f, 0.5f), new Vector2(0.5f, 0.5f));
            avatarRt.sizeDelta = new Vector2(avatarD, avatarD);
            avatarRt.anchoredPosition = new Vector2(avatarCx, 0f);
            _avatarImg = avatarRt.gameObject.AddComponent<Image>();
            // Speaking-bar style avatar: the stable-identity crewmate wearing the player's
            // live hat/skin/visor, composed by CrewmateIcon. Falls back to the identity
            // body while cosmetics load or when the player is dead.
            var body = CrewmateIcon.AvatarFor(_pc);
            if (body != null)
            {
                _avatarImg.sprite = body;
                _avatarImg.preserveAspect = true;
                _avatarImg.color = Color.white;
            }
            else
            {
                _avatarImg.sprite = Rounded(true);
                _avatarImg.type = Image.Type.Sliced;
                _avatarImg.color = _glowColor;
            }
            _avatarImg.raycastTarget = false;

            var nameTmp = Text("Name", Root, name, 19f, TextPrimary, TextAlignmentOptions.Left, FontStyles.Bold);
            nameTmp.overflowMode = TextOverflowModes.Ellipsis;
            nameTmp.rectTransform.Anchor(new Vector2(0f, 0.5f), new Vector2(0f, 0.5f), new Vector2(0f, 0.5f));
            nameTmp.rectTransform.sizeDelta = new Vector2(textColW, 30f);
            nameTmp.rectTransform.anchoredPosition = new Vector2(textLeft, 13f);

            _levelMeter = new LiveLevelMeter(Root, "MeterTrack", textColW);
            _levelMeter.Root.Anchor(
                new Vector2(0f, 0.5f), new Vector2(0f, 0.5f), new Vector2(0f, 0.5f));
            _levelMeter.Root.anchoredPosition = new Vector2(textLeft, -15f);

            const float resetD = 36f;
            const float pillW = 60f;
            const float gap = 14f;
            // Auto-volume gets a 56px chip between the slider and the value pill; with
            // no chip callback the layout collapses back to the reference geometry.
            const float autoW = 56f;
            const float autoGap = 10f;
            float sliderLeft = textLeft + textColW + ColGap;
            float resetLeft = PaneW - EdgePad - resetD;
            float pillLeft = resetLeft - gap - pillW;
            float autoLeft = _autoToggle == null ? pillLeft : pillLeft - autoGap - autoW;
            float sliderRight = autoLeft - gap;
            _trackW = Mathf.Max(120f, sliderRight - sliderLeft);

            _track = Rect("Track", Root);
            _track.Anchor(new Vector2(0f, 0.5f), new Vector2(0f, 0.5f), new Vector2(0f, 0.5f));
            _track.sizeDelta = new Vector2(_trackW, 9f);
            _track.anchoredPosition = new Vector2(sliderLeft, 0f);
            var trackImg = _track.gameObject.AddComponent<Image>();
            trackImg.sprite = Rounded(true);
            trackImg.type = Image.Type.Sliced;
            trackImg.color = TrackBg;
            trackImg.raycastTarget = false;

            float midT = Mathf.Approximately(_max, _min) ? 0.5f : (1f - _min) / (_max - _min);
            var mid = Rect("Mid", _track);
            mid.Anchor(new Vector2(0f, 0.5f), new Vector2(0f, 0.5f), new Vector2(0.5f, 0.5f));
            mid.sizeDelta = new Vector2(2f, 18f);
            mid.anchoredPosition = new Vector2(_trackW * midT, 0f);
            var midImg = mid.gameObject.AddComponent<Image>();
            midImg.sprite = Solid(Color.white);
            midImg.color = AccentSoft;
            midImg.raycastTarget = false;

            _fill = Rect("Fill", _track);
            _fill.Anchor(new Vector2(0f, 0.5f), new Vector2(0f, 0.5f), new Vector2(0f, 0.5f));
            _fill.sizeDelta = new Vector2(0f, 9f);
            _fillImg = _fill.gameObject.AddComponent<Image>();
            _fillImg.sprite = Rounded(true);
            _fillImg.type = Image.Type.Sliced;
            _fillImg.color = Accent;
            _fillImg.raycastTarget = false;

            _knob = Rect("Knob", _track);
            _knob.Anchor(new Vector2(0f, 0.5f), new Vector2(0f, 0.5f), new Vector2(0.5f, 0.5f));
            _knob.sizeDelta = new Vector2(22f, 22f);
            var knobShadow = GlowImage("KnobShadow", _knob, new Color32(0, 0, 0, 140));
            knobShadow.rectTransform.Anchor(Vector2.zero, Vector2.one, new Vector2(0.5f, 0.5f));
            knobShadow.rectTransform.offsetMin = new Vector2(-5f, -7f);
            knobShadow.rectTransform.offsetMax = new Vector2(5f, 3f);
            _knobImg = Rect("KnobFill", _knob).gameObject.AddComponent<Image>();
            _knobImg.rectTransform.Anchor(Vector2.zero, Vector2.one, new Vector2(0.5f, 0.5f));
            _knobImg.rectTransform.offsetMin = Vector2.zero;
            _knobImg.rectTransform.offsetMax = Vector2.zero;
            _knobImg.sprite = Rounded(true);
            _knobImg.type = Image.Type.Sliced;
            _knobImg.color = TextBright;
            _knobImg.raycastTarget = false;

            var pill = Rect("Pill", Root);
            pill.Anchor(new Vector2(0f, 0.5f), new Vector2(0f, 0.5f), new Vector2(0f, 0.5f));
            pill.sizeDelta = new Vector2(pillW, 34f);
            pill.anchoredPosition = new Vector2(pillLeft, 0f);
            var pillImg = pill.gameObject.AddComponent<Image>();
            pillImg.sprite = Rounded(true);
            pillImg.type = Image.Type.Sliced;
            pillImg.color = AccentFaint;
            pillImg.raycastTarget = false;
            _value = Text("Value", pill, "", 18f, Accent, TextAlignmentOptions.Center, FontStyles.Bold);
            _value.rectTransform.Anchor(Vector2.zero, Vector2.one, new Vector2(0.5f, 0.5f));
            _value.rectTransform.offsetMin = new Vector2(4f, 0f);
            _value.rectTransform.offsetMax = new Vector2(-4f, 0f);

            if (_autoToggle != null)
            {
                _autoRt = Rect("Auto", Root);
                _autoRt.Anchor(new Vector2(0f, 0.5f), new Vector2(0f, 0.5f), new Vector2(0f, 0.5f));
                _autoRt.sizeDelta = new Vector2(autoW, 34f);
                _autoRt.anchoredPosition = new Vector2(autoLeft, 0f);
                _autoImg = _autoRt.gameObject.AddComponent<Image>();
                _autoImg.sprite = Rounded(true);
                _autoImg.type = Image.Type.Sliced;
                _autoImg.color = ControlBg;
                _autoImg.raycastTarget = false;
                _autoLabel = Text("AutoLabel", _autoRt, TranslationHelper.Get("vc.common.auto", "AUTO"), 15f, TextMuted,
                    TextAlignmentOptions.Center, FontStyles.Bold);
                _autoLabel.rectTransform.Anchor(Vector2.zero, Vector2.one, new Vector2(0.5f, 0.5f));
                _autoLabel.rectTransform.offsetMin = new Vector2(3f, 0f);
                _autoLabel.rectTransform.offsetMax = new Vector2(-3f, 0f);
            }

            _resetRt = Rect("Reset", Root);
            _resetRt.Anchor(new Vector2(0f, 0.5f), new Vector2(0f, 0.5f), new Vector2(0f, 0.5f));
            _resetRt.sizeDelta = new Vector2(resetD, resetD);
            _resetRt.anchoredPosition = new Vector2(resetLeft, 0f);
            _resetImg = _resetRt.gameObject.AddComponent<Image>();
            _resetImg.sprite = Rounded();
            _resetImg.type = Image.Type.Sliced;
            _resetImg.color = ControlBg;
            _resetImg.raycastTarget = false;
            var iconRt = Rect("ResetIcon", _resetRt);
            iconRt.Anchor(new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f));
            iconRt.sizeDelta = new Vector2(22f, 22f);
            var iconImg = iconRt.gameObject.AddComponent<Image>();
            iconImg.sprite = ResetIcon;
            iconImg.preserveAspect = true;
            iconImg.color = TextPrimary;
            iconImg.raycastTarget = false;

            ApplyVisual();
            return this;
        }

        /// <summary>Circular-arrow glyph drawn in code: Interstellar ships no reset icon
        /// asset and this row must not add one.</summary>
        private static Sprite BuildResetIcon()
        {
            const int s = 64;
            const float c = (s - 1) * 0.5f;
            const float radius = 21f;
            const float stroke = 4.5f;
            const float gapStart = 40f;
            const float gapEnd = 120f;

            float headRad = gapStart * Mathf.Deg2Rad;
            var headDir = new Vector2(-Mathf.Sin(headRad), Mathf.Cos(headRad)); // counter-clockwise
            var headNorm = new Vector2(Mathf.Cos(headRad), Mathf.Sin(headRad));
            var headBase = new Vector2(c + radius * Mathf.Cos(headRad), c + radius * Mathf.Sin(headRad));
            var headTip = headBase + headDir * 12f;
            var headLeft = headBase + headNorm * 8f;
            var headRight = headBase - headNorm * 8f;

            var tex = new Texture2D(s, s, TextureFormat.RGBA32, false) { hideFlags = HideFlags.HideAndDontSave };
            for (int y = 0; y < s; y++)
            for (int x = 0; x < s; x++)
            {
                float dx = x - c;
                float dy = y - c;
                float r = Mathf.Sqrt(dx * dx + dy * dy);
                float a = Mathf.Atan2(dy, dx) * Mathf.Rad2Deg;
                if (a < 0f) a += 360f;
                bool arc = Mathf.Abs(r - radius) <= stroke * 0.5f
                           && (a <= gapStart || a >= gapEnd);
                bool head = InTriangle(new Vector2(x, y), headTip, headLeft, headRight);
                tex.SetPixel(x, y, new Color(1f, 1f, 1f, arc || head ? 1f : 0f));
            }
            tex.Apply();

            var sprite = Sprite.Create(tex, new Rect(0, 0, s, s), new Vector2(0.5f, 0.5f), s);
            sprite.hideFlags |= HideFlags.HideAndDontSave;
            return sprite;
        }

        private static bool InTriangle(Vector2 p, Vector2 a, Vector2 b, Vector2 c)
        {
            float d1 = Cross(p, a, b);
            float d2 = Cross(p, b, c);
            float d3 = Cross(p, c, a);
            bool neg = d1 < 0f || d2 < 0f || d3 < 0f;
            bool pos = d1 > 0f || d2 > 0f || d3 > 0f;
            return !(neg && pos);
        }

        private static float Cross(Vector2 p, Vector2 a, Vector2 b)
            => (a.x - p.x) * (b.y - p.y) - (a.y - p.y) * (b.x - p.x);

        private static Color FillColor(float v)
        {
            if (v <= 1f) return Color.Lerp(FillLow, Accent, Mathf.Clamp01(v));
            return Color.Lerp(Accent, FillBoost, Mathf.Clamp01(v - 1f));
        }

        private static string ValueColor(float v)
            => v < 0.005f ? "#8C9CB2" : v > 1.005f ? "#F2AB3D" : "#22D3EE";

        private int _lastPct = int.MinValue;
        private string? _lastValueColor;

        private void ApplyVisual()
        {
            float v = Mathf.Clamp(_get(), _min, _max);
            float t = Mathf.Approximately(_max, _min) ? 0f : (v - _min) / (_max - _min);
            _fill.sizeDelta = new Vector2(_trackW * t, 9f);
            if (!_dimmed) _fillImg.color = FillColor(v);
            _knob.anchoredPosition = new Vector2(_trackW * t, 0f);
            int pct = Mathf.RoundToInt(v * 100f);
            string c = ValueColor(v);
            if (pct != _lastPct || !ReferenceEquals(c, _lastValueColor))
            {
                _lastPct = pct;
                _lastValueColor = c;
                _value.text = $"<color={c}>{pct}%</color>";
            }
        }

        /// <summary>Feeds the row's level meter from the voice client.</summary>
        public void SetLevel(float level, bool speaking) => _levelMeter.SetLevel(level, speaking);

        public bool HasVisibleMeter =>
            _levelMeter.IsSpeaking || _levelMeter.DisplayLevel > 0.001f;

        public void ClearLevelImmediately()
        {
            _levelMeter.ClearImmediately();
            var glow = _glowColor;
            glow.a = 0f;
            _avatarGlow.color = glow;
        }

        public override void OnMouseDown()
        {
            if (Contains(_resetRt))
            {
                RebindRow.CancelCaptureForExternalPointer();
                _onChange(1f);
                _onCommit();
                ApplyVisual();
                return;
            }
            // Auto chip sits outside the `_enabled` gate on purpose: it is the control
            // that turns auto-volume off again (and the only one that turns it on).
            if (_autoRt != null && Contains(_autoRt))
            {
                RebindRow.CancelCaptureForExternalPointer();
                try { _autoToggle?.Invoke(); } catch { }
                ApplyVisual();
                return;
            }
            // Auto-volume owns this player's volume: keep the slider readable but inert.
            if (_enabled != null && !_enabled()) return;
            if (Contains(_track) || Contains(_knob))
            {
                RebindRow.CancelCaptureForExternalPointer();
                _dragging = true;
                ApplyFromMouse();
            }
        }

        public override void OnMouseDrag()
        {
            if (_dragging) ApplyFromMouse();
        }

        public override void OnMouseUp()
        {
            if (_dragging && _changed) { _onCommit(); _changed = false; }
            _dragging = false;
        }

        private void ApplyFromMouse()
        {
            if (!LocalPoint(_track, out var lp)) return;
            float t = Mathf.Clamp01(lp.x / _trackW);
            float v = (float)Math.Round(_min + t * (_max - _min), 2);
            if (Mathf.Abs(v - _get()) < 0.0001f) return;
            _onChange(v);
            _changed = true;
            ApplyVisual();
        }

        public override void Tick(float dt)
        {
            TickHover();
            if (!_dragging) ApplyVisual();
            _resetImg.color = Lerp(_resetImg.color, Contains(_resetRt) ? ControlHover : ControlBg, 0.25f);

            if (_autoImg != null)
            {
                bool on = _autoGet != null && _autoGet();
                bool over = Contains(_autoRt);
                _autoImg.color = Lerp(_autoImg.color,
                    on ? AccentFaint : over ? ControlHover : ControlBg, 0.25f);
                _autoLabel.color = Lerp(_autoLabel.color,
                    on ? Accent : over ? TextBright : TextMuted, 0.25f);
            }

            if (_enabled != null && !_enabled())
            {
                _fillImg.color = Lerp(_fillImg.color, Dim(_fillImg.color), 0.25f);
                _knobImg.color = Lerp(_knobImg.color, Dim(_knobImg.color), 0.25f);
                _dimmed = true;
            }
            else if (_dimmed)
            {
                // auto-volume released: restore the live fill/knob colors
                _dimmed = false;
                _knobImg.color = TextBright;
                _fillImg.color = FillColor(Mathf.Clamp(_get(), _min, _max));
            }

            // Cosmetics finish loading after the row is built (and an outfit swap can land
            // mid-session): re-query the avatar — resolved entries are a dictionary hit.
            if (_avatarImg != null)
            {
                var av = CrewmateIcon.AvatarFor(_pc);
                if (av != null && !ReferenceEquals(av, _avatarImg.sprite)) _avatarImg.sprite = av;
            }

            _levelMeter.Tick(dt);
            float shown = _levelMeter.DisplayLevel;
            var g = _glowColor;
            g.a = _levelMeter.IsSpeaking ? 0.30f + 0.45f * shown : 0f;
            _avatarGlow.color = g;
        }
    }
}
