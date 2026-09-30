using System.Reflection;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;
using Interstellar.Voice;
using Object = UnityEngine.Object;

namespace Interstellar;

/// <summary>
/// The hot buttons — microphone (mute cycle) and speaker (on/off) — drawn on the
/// overlay canvas in the screen's bottom-left corner, on top of the vertical
/// VoiceButtonsBG plate, which carries exactly two slots.
///
/// They used to be clones of the map button hung off the HUD in world space, which
/// put them wherever the HUD happened to be at whatever the current resolution
/// scaled it to and showed them only while the game's own settings button was up.
/// Anchoring them to <see cref="VCUiKit"/>'s screen-space canvas — CanvasScaler
/// 1920x1080, ScaleWithScreenSize — makes the corner position and the icon size
/// identical at any resolution.
///
/// The old gear entry is gone: settings are opened from the menu chip (see
/// VoiceChipEntries) or with F11, so the plate carries two icons and nothing else.
///
/// Visible whenever a voice room exists in an online game (lobby or round),
/// hidden in the main menu.
/// </summary>
[HarmonyLib.HarmonyPatch(typeof(HudManager), nameof(HudManager.Update))]
public static class VoiceButtons
{
    /// <summary>Android build: the floating movement joystick owns the bottom-left touch
    /// zone, and the plate sits right inside it — the whole plate (both buttons) rides
    /// higher there so it stops eating joystick touches.</summary>
    private static readonly bool IsAndroid = OperatingSystem.IsAndroid();

    /// <summary>Bottom-left of the canvas, just above the server-info line. That text
    /// dropped to 18pt, so the plate closes ~30px (a bit over a third of a button) of
    /// the gap it used to leave — 72 still clears the text's 20..56 box. On Android it
    /// lifts by 2.5 button heights (2.5 × the 94px slot face = 235) out of the joystick.</summary>
    private static readonly Vector2 CornerPos = IsAndroid ? new Vector2(30f, 72f + 2.5f * 94f) : new Vector2(30f, 72f);

    // VoiceButtonsBG.png, measured pixel by pixel: a 154x267 plate (opaque box x5..142 /
    // y7..260) holding two recessed button slots — a brighter face inside a dark outline,
    // slot 1 at png y34..127 and slot 2 at y139..232, both 94px tall with an 11px divider
    // between, outlined x22..125 (103 wide) and centred on the plate at x 73.5. Flipping
    // png's top-down y into Unity's bottom-up y puts the slot centres at 185.5 (top) and
    // 80.5 (bottom). Icons sit in the slots at their source size 89x78: 7px of slot face
    // either side, 8px above and below, and no upscaling of the glyph into a blur.
    private static readonly Vector2 PlateSize = new(154f, 267f);
    private static readonly Vector2 IconSize = new(89f, 78f);
    /// <summary>Top slot (microphone) — bottom-left pivot of the icon rect.</summary>
    private static readonly Vector2 MicPos = new(29f, 146.5f);
    /// <summary>Bottom slot (speaker).</summary>
    private static readonly Vector2 SpkPos = new(29f, 41.5f);

    private static GameObject? _root;
    private static Image? _micImg, _spkImg;
    private static Button? _micBtn, _spkBtn;
    private static bool _built, _hooked;
    private static int _visualKey = int.MinValue;
    /// <summary>Last applied value of <see cref="VoiceConfig.HotkeyButtonScale"/> — the
    /// root transform is only rewritten when the slider actually moves.</summary>
    private static float _scale = 1f;

    private static bool _micMuted, _speakerMuted;
    private static VoiceChannel _channel = VoiceChannel.All;
    public static bool IsSpeakerMuted => _speakerMuted;
    public static bool IsImpostorRadioOnly => _channel == VoiceChannel.Impostor;

    static void Postfix(HudManager __instance)
    {
        EnsureHooks();
        if (!_built) Build();
        Tick();
    }

    /// <summary>HudManager only exists inside a game, so it cannot notice us
    /// returning to the main menu — the scene hook closes that gap.</summary>
    private static void EnsureHooks()
    {
        if (_hooked) return;
        _hooked = true;
        SceneManager.sceneLoaded += (UnityEngine.Events.UnityAction<Scene, LoadSceneMode>)((_, __) =>
        { try { Tick(); } catch { } });
    }

    private static void Build()
    {
        _built = true;
        var canvas = VCUiKit.EnsureCanvas();

        // Through NewRect, not `new GameObject` + a cast: a fresh GameObject carries a
        // plain Transform, and downcasting it to RectTransform throws under IL2CPP —
        // which is why this stack never appeared in game (Build threw before drawing).
        var root = VCUiKit.NewRect(canvas.transform, "VC_HotButtons");
        _root = root.gameObject;
        root.anchorMin = root.anchorMax = Vector2.zero;
        root.pivot = Vector2.zero;
        root.anchoredPosition = CornerPos;
        root.sizeDelta = PlateSize;
        // Scale the whole unit — plate, both icons, their hit targets — around the
        // bottom-left pivot, so the fixed CornerPos keeps the Android lift clear of
        // the joystick no matter how big the buttons get.
        _scale = VoiceConfig.HotkeyButtonScale;
        root.localScale = Vector3.one * _scale;

        // The plate is decoration only: drawn first (behind the icons) and never a click
        // target, so a press lands on the button under the pointer, not on the margin.
        var plate = LoadSprite("Interstellar.Resources.VoiceButtonsBG.png", 100f);
        if (plate != null)
        {
            var bg = VCUiKit.CreateImage(root, "VC_ButtonsBG", Vector2.zero, PlateSize, plate, Color.white);
            bg.raycastTarget = false;
        }

        // Top to bottom: microphone, speaker. The settings gear that used to sit above
        // them is gone — the menu chip and F11 are the settings entries now.
        _micImg = CreateIconButton(root, "VC_MicBtn", MicPos, CycleMic);
        _micBtn = _micImg.GetComponent<Button>();
        _spkImg = CreateIconButton(root, "VC_SpkBtn", SpkPos, ToggleSpeaker);
        _spkBtn = _spkImg.GetComponent<Button>();

        _visualKey = int.MinValue;
        RefreshVisuals();
    }

    private static Image CreateIconButton(Transform parent, string name, Vector2 pos, Action onClick)
    {
        var rt = VCUiKit.NewRect(parent, name);
        rt.anchorMin = rt.anchorMax = Vector2.zero;
        rt.pivot = Vector2.zero;
        rt.anchoredPosition = pos;
        rt.sizeDelta = IconSize;

        var img = rt.gameObject.AddComponent<Image>();
        img.preserveAspect = true;
        img.raycastTarget = true;

        var btn = rt.gameObject.AddComponent<Button>();
        btn.targetGraphic = img;
        btn.transition = Button.Transition.SpriteSwap;
        Action handler = () => { try { onClick?.Invoke(); } catch { } };
        btn.onClick.AddListener(handler);
        return img;
    }

    /// <summary>Show only where there is something to mute: an online game with a
    /// live voice room. The main menu shows nothing.</summary>
    private static void Tick()
    {
        if (_root == null) return;
        // The settings slider can retune the size at any moment — apply it live so a
        // drag resizes plate, icons and hit targets together on the very next frame.
        float s = VoiceConfig.HotkeyButtonScale;
        if (s != _scale) { _scale = s; _root.transform.localScale = Vector3.one * s; }
        bool show = VoiceRoom.Current != null
            && SceneManager.GetActiveScene().name == "OnlineGame";
        if (_root.activeSelf != show) _root.SetActive(show);
        if (show) RefreshVisuals();
    }

    internal static void CycleMic()
    {
        bool impRadioOn = VoiceConfig.SyncedRoomSettings.ImpostorPrivateRadio;
        bool canImpMode = PlayerControl.LocalPlayer != null && PlayerControl.LocalPlayer.Data?.Role?.IsImpostor == true && !PlayerControl.LocalPlayer.Data.IsDead && impRadioOn;
        if (!_micMuted && _channel == VoiceChannel.All) { if (canImpMode) _channel = VoiceChannel.Impostor; else _micMuted = true; }
        else if (_channel == VoiceChannel.Impostor) { _channel = VoiceChannel.All; _micMuted = true; }
        else { _micMuted = false; _channel = VoiceChannel.All; }
        ApplyMicState();
        RefreshVisuals();
    }

    internal static void ToggleSpeaker()
    {
        _speakerMuted = !_speakerMuted;
        var room = VoiceRoom.Current;
        if (room != null)
        {
            if (_speakerMuted) { room.SetMasterVolume(0f); room.SetSpeaker(null!); }
            else { room.SetSpeaker(VoiceConfig.SpeakerDevice); room.SetMasterVolume(VoiceConfig.MasterVolume); }
        }
        RefreshVisuals();
    }

    internal static void ApplyMicState() => VoiceRoom.Current?.SetMute(_micMuted, _channel == VoiceChannel.Impostor);
    internal static void ApplySpeakerState()
    {
        var room = VoiceRoom.Current;
        if (room == null) return;
        if (_speakerMuted) { room.SetMasterVolume(0f); room.SetSpeaker(null!); }
        else if (!room.HasSpeaker) { room.SetSpeaker(VoiceConfig.SpeakerDevice); room.SetMasterVolume(VoiceConfig.MasterVolume); }
    }

    static void RefreshVisuals()
    {
        if (_micImg == null || _spkImg == null) return;

        bool micOff = _micMuted;
        bool inChannel = _channel != VoiceChannel.All;
        // Only repaint when something actually changed — this runs every frame.
        int key = (micOff ? 1 : 0) | ((int)_channel << 1) | (_speakerMuted ? 8 : 0);
        if (key == _visualKey) return;
        _visualKey = key;

        Color channelColor = _channel == VoiceChannel.Impostor ? new Color(1f, 0.2f, 0.2f) : Color.white;
        Color micColor = micOff ? new Color(0.5f, 0.5f, 0.5f, 1f)
                       : inChannel ? channelColor
                       : Color.white;

        ApplyIcon(_micBtn, _micImg,
            LoadSprite(micOff ? "Interstellar.Resources.MicOff.png" : "Interstellar.Resources.MicOn.png", 100f),
            LoadSprite(micOff ? "Interstellar.Resources.MicOffOver.png" : "Interstellar.Resources.MicOnOver.png", 100f),
            micColor);

        ApplyIcon(_spkBtn, _spkImg,
            LoadSprite(_speakerMuted ? "Interstellar.Resources.SpeakerOff.png" : "Interstellar.Resources.SpeakerOn.png", 100f),
            LoadSprite(_speakerMuted ? "Interstellar.Resources.SpeakerOffOver.png" : "Interstellar.Resources.SpeakerOnOver.png", 100f),
            Color.white);
    }

    private static void ApplyIcon(Button? btn, Image? img, Sprite? normal, Sprite? over, Color tint)
    {
        if (img == null) return;
        img.sprite = normal;
        img.color = tint;
        if (btn == null) return;
        var st = btn.spriteState;
        st.highlightedSprite = over;
        st.pressedSprite = over;
        st.selectedSprite = over;
        btn.spriteState = st;
    }

    static readonly Dictionary<string, Sprite> _spriteCache = new();
    static Sprite? LoadSprite(string path, float ppu)
    {
        if (_spriteCache.TryGetValue(path, out var c)) return c;
        try
        {
            var s = Assembly.GetExecutingAssembly().GetManifestResourceStream(path);
            if (s == null) return null;
            var t = new Texture2D(0, 0, TextureFormat.RGBA32, false) { wrapMode = TextureWrapMode.Clamp };
            using var m = new System.IO.MemoryStream(); s.CopyTo(m);
            t.LoadImage(m.ToArray(), false);
            var sp = Sprite.Create(t, new Rect(0, 0, t.width, t.height), new Vector2(0.5f, 0.5f), ppu);
            sp.hideFlags |= HideFlags.HideAndDontSave | HideFlags.DontSaveInEditor;
            _spriteCache[path] = sp; return sp;
        }
        catch { return null; }
    }
}

public enum VoiceChannel
{
    All,
    Impostor,
}
