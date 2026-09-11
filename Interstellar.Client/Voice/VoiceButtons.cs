using System.Reflection;
using TMPro;
using UnityEngine;
using Interstellar.Voice;
using Object = UnityEngine.Object;

namespace Interstellar;

[HarmonyLib.HarmonyPatch(typeof(HudManager), nameof(HudManager.Update))]
public static class VoiceButtons
{
    static PassiveButton? toggleMicButton;
    static GameObject? toggleMicButtonObject;
    static SpriteRenderer? micInactive, micActive;

    static PassiveButton? toggleSpkButton;
    static GameObject? toggleSpkButtonObject;
    static SpriteRenderer? spkInactive, spkActive;

    static PassiveButton? toggleSetButton;
    static GameObject? toggleSetButtonObject;
    static SpriteRenderer? setInactive, setActive;

    static GameObject? voiceBgObject;
    static SpriteRenderer? voiceBgRenderer;

    private static bool _micMuted, _speakerMuted;
    private static VoiceChannel _channel = VoiceChannel.All;
    public static bool IsSpeakerMuted => _speakerMuted;
    public static bool IsImpostorRadioOnly => _channel == VoiceChannel.Impostor;

    static void Postfix(HudManager __instance)
    {
        if (__instance.MapButton == null) return;

        var settingsBtn = __instance.SettingsButton;
        float btnY = settingsBtn != null && settingsBtn.gameObject.active
            ? settingsBtn.transform.localPosition.y
            : __instance.MapButton.transform.localPosition.y;

        if (!voiceBgObject)
        {
            voiceBgObject = new GameObject("VC_BtnBG");
            voiceBgRenderer = voiceBgObject.AddComponent<SpriteRenderer>();
            voiceBgRenderer.sprite = LoadSprite("Interstellar.Resources.VoiceButtonsBG.png", 175f);
            voiceBgObject.transform.SetParent(__instance.transform, false);
            voiceBgObject.layer = __instance.MapButton.gameObject.layer;
            voiceBgObject.transform.SetSiblingIndex(1);
        }
        bool settingsActive = __instance.SettingsButton != null && __instance.SettingsButton.gameObject.active;
        voiceBgObject!.SetActive(settingsActive);
        voiceBgObject!.transform.localPosition = new Vector3(0f, btnY, -500f);

        if (!toggleMicButton || !toggleMicButtonObject)
        {
            toggleMicButtonObject = Object.Instantiate(__instance.MapButton.gameObject, __instance.transform);
            toggleMicButtonObject.name = "VC_MicBtn";
            toggleMicButtonObject.transform.Find("Background").gameObject.SetActive(false);
            micInactive = toggleMicButtonObject.transform.Find("Inactive").GetComponent<SpriteRenderer>();
            micActive = toggleMicButtonObject.transform.Find("Active").GetComponent<SpriteRenderer>();
            toggleMicButton = toggleMicButtonObject.GetComponent<PassiveButton>();
            toggleMicButton.OnClick.RemoveAllListeners();
            toggleMicButton.OnClick.AddListener((Action)CycleMic);
        }
        toggleMicButtonObject!.SetActive(settingsActive);
        toggleMicButtonObject!.transform.localPosition = new Vector3(-0.6f, btnY + 0.03f, -500f);

        if (!toggleSpkButton || !toggleSpkButtonObject)
        {
            toggleSpkButtonObject = Object.Instantiate(__instance.MapButton.gameObject, __instance.transform);
            toggleSpkButtonObject.name = "VC_SpkBtn";
            toggleSpkButtonObject.transform.Find("Background").gameObject.SetActive(false);
            spkInactive = toggleSpkButtonObject.transform.Find("Inactive").GetComponent<SpriteRenderer>();
            spkActive = toggleSpkButtonObject.transform.Find("Active").GetComponent<SpriteRenderer>();
            toggleSpkButton = toggleSpkButtonObject.GetComponent<PassiveButton>();
            toggleSpkButton.OnClick.RemoveAllListeners();
            toggleSpkButton.OnClick.AddListener((Action)ToggleSpeaker);
        }
        toggleSpkButtonObject!.SetActive(settingsActive);
        toggleSpkButtonObject!.transform.localPosition = new Vector3(0f, btnY + 0.03f, -500f);

        if (!toggleSetButton || !toggleSetButtonObject)
        {
            toggleSetButtonObject = Object.Instantiate(__instance.MapButton.gameObject, __instance.transform);
            toggleSetButtonObject.name = "VC_SetBtn";
            toggleSetButtonObject.transform.Find("Background").gameObject.SetActive(false);
            setInactive = toggleSetButtonObject.transform.Find("Inactive").GetComponent<SpriteRenderer>();
            setActive = toggleSetButtonObject.transform.Find("Active").GetComponent<SpriteRenderer>();
            toggleSetButton = toggleSetButtonObject.GetComponent<PassiveButton>();
            toggleSetButton.OnClick.RemoveAllListeners();
            toggleSetButton.OnClick.AddListener((Action)(() =>
            {
                var w = VoiceSettingsWindow.Instance;
                if (w != null) { if (!w.ShowWindow) w.Open(); else w.Close(); }
            }));
            setInactive.sprite = LoadSprite("Interstellar.Resources.Settings_Button.png", 100f);
            setActive.sprite = LoadSprite("Interstellar.Resources.Settings_ButtonActive.png", 100f);
        }
        toggleSetButtonObject!.SetActive(settingsActive);
        toggleSetButtonObject!.transform.localPosition = new Vector3(0.6f, btnY + 0.03f, -500f);

        RefreshVisuals();
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
        bool micOff = _micMuted;
        bool inChannel = _channel != VoiceChannel.All;
        Color channelColor = _channel == VoiceChannel.Impostor ? new Color(1f, 0.2f, 0.2f) : Color.white;

        Color micColor;
        if (micOff) micColor = new Color(0.5f, 0.5f, 0.5f, 1f);
        else if (inChannel) micColor = channelColor;
        else micColor = Color.white;

        if (micInactive != null && micActive != null)
        {
            micInactive.sprite = LoadSprite(micOff ? "Interstellar.Resources.MicOff.png" : "Interstellar.Resources.MicOn.png", 100f);
            micActive.sprite = LoadSprite(micOff ? "Interstellar.Resources.MicOffOver.png" : "Interstellar.Resources.MicOnOver.png", 100f);
            micInactive.color = micColor;
            micActive.color = new Color(micColor.r * 0.75f, micColor.g * 0.75f, micColor.b * 0.75f, micColor.a);
        }

        if (spkInactive != null && spkActive != null)
        {
            spkInactive.sprite = LoadSprite(_speakerMuted ? "Interstellar.Resources.SpeakerOff.png" : "Interstellar.Resources.SpeakerOn.png", 100f);
            spkActive.sprite = LoadSprite(_speakerMuted ? "Interstellar.Resources.SpeakerOffOver.png" : "Interstellar.Resources.SpeakerOnOver.png", 100f);
            spkInactive.color = Color.white;
            spkActive.color = new Color(0.75f, 0.75f, 0.75f, 1f);
        }
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
