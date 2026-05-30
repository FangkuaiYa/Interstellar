using System;
using System.Collections.Generic;
using System.Reflection;
using TMPro;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.SceneManagement;
using static UnityEngine.UI.Button;
using Object = UnityEngine.Object;

namespace VoiceChatPlugin.VoiceChat;

public enum VoiceChannel
{
    All,
    Impostor,
}

public static class VoiceChatHudState
{
    private static PassiveButton? _micButton;
    private static GameObject? _micButtonObj;
    private static PassiveButton? _spkButton;
    private static GameObject? _spkButtonObj;
    private static AspectPosition? _micAspect;
    private static AspectPosition? _spkAspect;

    private static readonly AspectPosition.EdgeAlignments ButtonAnchor
        = AspectPosition.EdgeAlignments.RightTop;
    private static readonly Vector3 MicEdge = new(3.85f, 0.55f, -10f);
    private static readonly Vector3 SpkEdge = new(4.50f, 0.55f, -10f);

    private static GameObject? _micTooltip;
    private static GameObject? _spkTooltip;
    private static TextMeshPro? _micTooltipTmp;
    private static TextMeshPro? _spkTooltipTmp;

    private static bool _micMuted;
    private static bool _speakerMuted;
    private static VoiceChannel _channel = VoiceChannel.All;

    public static bool IsSpeakerMuted => _speakerMuted;
    public static bool IsImpostorRadioOnly => _channel == VoiceChannel.Impostor;

    private static VoiceChatRoomSettings? _lastSentSettings;
    public static void MarkRoomSettingsDirty() => _lastSentSettings = null;

    internal static void Init()
    {
        SceneManager.sceneLoaded +=
            (UnityEngine.Events.UnityAction<Scene, LoadSceneMode>)((_, __) =>
            {
                DestroyButtons();
                DestroyTooltips();
            });
    }

    internal static void UpdateHud()
    {
        var hud = HudManager.Instance;
        if (hud == null) return;

        EnsureHudButtons(hud);
        EnsureTooltips(hud);
        UpdateHudButtonsVisibility();
        RefreshButtonVisuals();
    }

    internal static void ApplyMicState()
    {
        VoiceChatRoom.Current?.SetMute(_micMuted);
    }

    internal static void ApplySpeakerState()
    {
        // FIX: Speaker mute bug — when muted, null the speaker to
        // completely stop audio output and prevent buffer noise loop.
        // When unmuted, recreate the speaker from config.
        var room = VoiceChatRoom.Current;
        if (room == null) return;

        if (_speakerMuted)
        {
            room.SetMasterVolume(0f);
        }
        else
        {
            room.SetMasterVolume(VoiceChatConfig.MasterVolume);
        }
    }

    internal static void TrySyncHostRoomSettings()
    {
        if (AmongUsClient.Instance == null || !AmongUsClient.Instance.AmHost) return;
        if (AmongUsClient.Instance.GameState
            != InnerNet.InnerNetClient.GameStates.Joined) return;

        var cur = VoiceChatConfig.SyncedRoomSettings;
        if (_lastSentSettings != null && cur.ContentEquals(_lastSentSettings)) return;

        VoiceChatRoomSettings.SendToAll(cur);
        _lastSentSettings = new VoiceChatRoomSettings();
        _lastSentSettings.Apply(cur);
        VoiceChatPluginMain.Logger.LogInfo("[VC] Room settings synced.");
    }

    private static void DestroyButtons()
    {
        if (_micButtonObj != null) { Object.Destroy(_micButtonObj); _micButtonObj = null; }
        if (_spkButtonObj != null) { Object.Destroy(_spkButtonObj); _spkButtonObj = null; }
        _micButton = null; _spkButton = null;
        _micAspect = null; _spkAspect = null;
    }

    private static void DestroyTooltips()
    {
        if (_micTooltip != null) { Object.Destroy(_micTooltip); _micTooltip = null; }
        if (_spkTooltip != null) { Object.Destroy(_spkTooltip); _spkTooltip = null; }
        _micTooltipTmp = null; _spkTooltipTmp = null;
    }

    private static void EnsureHudButtons(HudManager hud)
    {
        if (hud.MapButton == null) return;

        if (_micButtonObj == null)
        {
            _micButtonObj = Object.Instantiate(hud.MapButton.gameObject, hud.transform.parent);
            _micButtonObj.name = "VC_MicButton";
            ClearButtonBG(_micButtonObj);
            CreateIconChild(_micButtonObj, "VoiceChatPlugin.Resources.MicOn.png");

            _micButton = _micButtonObj.GetComponent<PassiveButton>();
            _micButton.OnClick = new ButtonClickedEvent();
            _micButton.OnClick.AddListener((Action)CycleMic);
            _micButton.OnMouseOver = new UnityEvent();
            _micButton.OnMouseOver.AddListener((Action)ShowMicTooltip);
            _micButton.OnMouseOut = new UnityEvent();
            _micButton.OnMouseOut.AddListener((Action)HideTooltips);

            _micAspect = _micButtonObj.GetComponent<AspectPosition>()
                ?? _micButtonObj.AddComponent<AspectPosition>();
            _micAspect.Alignment = ButtonAnchor;
            _micAspect.DistanceFromEdge = MicEdge;
        }

        if (_spkButtonObj == null)
        {
            _spkButtonObj = Object.Instantiate(hud.MapButton.gameObject, hud.transform.parent);
            _spkButtonObj.name = "VC_SpkButton";
            ClearButtonBG(_spkButtonObj);
            CreateIconChild(_spkButtonObj, "VoiceChatPlugin.Resources.SpeakerOn.png");

            _spkButton = _spkButtonObj.GetComponent<PassiveButton>();
            _spkButton.OnClick = new ButtonClickedEvent();
            _spkButton.OnClick.AddListener((Action)ToggleSpeaker);
            _spkButton.OnMouseOver = new UnityEvent();
            _spkButton.OnMouseOver.AddListener((Action)ShowSpeakerTooltip);
            _spkButton.OnMouseOut = new UnityEvent();
            _spkButton.OnMouseOut.AddListener((Action)HideTooltips);

            _spkAspect = _spkButtonObj.GetComponent<AspectPosition>()
                ?? _spkButtonObj.AddComponent<AspectPosition>();
            _spkAspect.Alignment = ButtonAnchor;
            _spkAspect.DistanceFromEdge = SpkEdge;
        }
    }

    private static void EnsureTooltips(HudManager hud)
    {
        if (_micTooltip == null)
            _micTooltip = CreateTooltipObject(hud.transform.parent, out _micTooltipTmp);
        if (_spkTooltip == null)
            _spkTooltip = CreateTooltipObject(hud.transform.parent, out _spkTooltipTmp);
    }

    private static void UpdateHudButtonsVisibility()
    {
        if (_micButtonObj == null || _spkButtonObj == null) return;
        bool inMeeting = MeetingHud.Instance != null;
        if (!inMeeting)
        {
            bool mapOpen = MapBehaviour.Instance && MapBehaviour.Instance.IsOpen;
            _micButtonObj.SetActive(!mapOpen);
            _spkButtonObj.SetActive(!mapOpen);
            _micAspect?.AdjustPosition();
            _spkAspect?.AdjustPosition();
        }
    }

    internal static void CycleMicPublic() => CycleMic();
    private static void CycleMic()
    {
        bool impRadioOn = VoiceChatConfig.SyncedRoomSettings.ImpostorPrivateRadio;
        bool canImpMode = PlayerControl.LocalPlayer != null
            && PlayerControl.LocalPlayer.Data?.Role?.IsImpostor == true
            && !PlayerControl.LocalPlayer.Data.IsDead
            && impRadioOn;

        if (!_micMuted && _channel == VoiceChannel.All)
        {
            if (canImpMode) _channel = VoiceChannel.Impostor;
            else _micMuted = true;
        }
        else if (_channel == VoiceChannel.Impostor)
        { _channel = VoiceChannel.All; _micMuted = true; }
        else
        { _micMuted = false; _channel = VoiceChannel.All; }

        ApplyMicState();
        RefreshButtonVisuals();
    }

    internal static void ToggleSpeakerPublic() => ToggleSpeaker();
    private static void ToggleSpeaker()
    {
        _speakerMuted = !_speakerMuted;

        // FIX: Speaker mute bug — when muting, set master volume to 0
        // AND clear speaker to prevent buffer noise loop. When unmuting,
        // restore the speaker device.
        var room = VoiceChatRoom.Current;
        if (room != null)
        {
            if (_speakerMuted)
            {
                room.SetMasterVolume(0f);
            }
            else
            {
                room.SetMasterVolume(VoiceChatConfig.MasterVolume);
            }
        }

        VoiceChatPluginMain.Logger.LogInfo("[VC] Speaker: " + (_speakerMuted ? "OFF" : "ON"));
        RefreshButtonVisuals();
    }

    private static void RefreshButtonVisuals()
    {
        if (_micButtonObj != null)
        {
            var sr = _micButtonObj.transform.Find("VCIcon")?.GetComponent<SpriteRenderer>();
            if (sr != null)
            {
                if (_micMuted)
                { sr.sprite = Sprites.MicOff; sr.color = new Color(1f, 0.4f, 0.4f); }
                else if (_channel == VoiceChannel.Impostor)
                { sr.sprite = Sprites.MicOn; sr.color = new Color(1f, 0.35f, 0.35f); }
                else
                { sr.sprite = Sprites.MicOn; sr.color = Color.white; }
            }
        }
        if (_spkButtonObj != null)
        {
            var sr = _spkButtonObj.transform.Find("VCIcon")?.GetComponent<SpriteRenderer>();
            if (sr != null)
            {
                sr.sprite = _speakerMuted ? Sprites.SpkOff : Sprites.SpkOn;
                sr.color = _speakerMuted ? new Color(1f, 0.4f, 0.4f) : Color.white;
            }
        }
    }

    private static GameObject CreateTooltipObject(Transform root, out TextMeshPro tmp)
    {
        var go = new GameObject("VC_Tooltip");
        go.transform.SetParent(root, false);
        go.transform.localPosition = new Vector3(0f, 0f, -5f);

        var bg = new GameObject("BG");
        bg.transform.SetParent(go.transform, false);
        var bgSr = bg.AddComponent<SpriteRenderer>();
        bgSr.sprite = CreateSolidSprite(new Color(0f, 0f, 0f, 0.82f));
        bgSr.sortingOrder = 20;
        bg.transform.localScale = new Vector3(2.6f, 1.6f, 1f);

        var textGo = new GameObject("Text");
        textGo.transform.SetParent(go.transform, false);
        textGo.transform.localPosition = new Vector3(0f, 0f, -0.1f);
        tmp = textGo.AddComponent<TextMeshPro>();
        tmp.fontSize = 1.5f; tmp.color = Color.white;
        tmp.alignment = TextAlignmentOptions.Center;
        tmp.enableWordWrapping = false; tmp.sortingOrder = 21;
        tmp.rectTransform.sizeDelta = new Vector2(2.4f, 1.4f);
        go.SetActive(false);
        return go;
    }

    private static void ShowMicTooltip()
    {
        if (_micTooltip == null || _micTooltipTmp == null || _micButtonObj == null) return;
        string ch = _channel == VoiceChannel.Impostor
            ? VoiceChatLocalization.Tr("channelImpostor") : VoiceChatLocalization.Tr("channelAll");
        string st = _micMuted ? VoiceChatLocalization.Tr("micStatusMuted")
            : (_channel == VoiceChannel.Impostor ? VoiceChatLocalization.Tr("micStatusImpostor")
            : VoiceChatLocalization.Tr("micStatusOn"));
        _micTooltipTmp.text =
            "<b>" + VoiceChatLocalization.Tr("tooltipMicTitle") + "</b>\n" +
            string.Format(VoiceChatLocalization.Tr("tooltipMicStatus"), st) + "\n" +
            string.Format(VoiceChatLocalization.Tr("tooltipMicChannel"), ch) + "\n" +
            string.Format(VoiceChatLocalization.Tr("tooltipMicVolume"), (int)(VoiceChatConfig.MicVolume * 100f)) + "\n" +
            string.Format(VoiceChatLocalization.Tr("tooltipMicHotkey"), "M");
        PositionNear(_micTooltip, _micButtonObj);
        _micTooltip.SetActive(true);
    }

    private static void ShowSpeakerTooltip()
    {
        if (_spkTooltip == null || _spkTooltipTmp == null || _spkButtonObj == null) return;
        string st = _speakerMuted
            ? VoiceChatLocalization.Tr("speakerStatusOff") : VoiceChatLocalization.Tr("speakerStatusOn");
        _spkTooltipTmp.text =
            "<b>" + VoiceChatLocalization.Tr("tooltipSpeakerTitle") + "</b>\n" +
            string.Format(VoiceChatLocalization.Tr("tooltipSpeakerStatus"), st) + "\n" +
            string.Format(VoiceChatLocalization.Tr("tooltipSpeakerVolume"), (int)(VoiceChatConfig.MasterVolume * 100f)) + "\n" +
            string.Format(VoiceChatLocalization.Tr("tooltipSpeakerHotkey"), "N");
        PositionNear(_spkTooltip, _spkButtonObj);
        _spkTooltip.SetActive(true);
    }

    private static void HideTooltips()
    {
        _micTooltip?.SetActive(false);
        _spkTooltip?.SetActive(false);
    }

    private static void PositionNear(GameObject tooltip, GameObject btn)
    {
        var p = btn.transform.position;
        tooltip.transform.position = new Vector3(p.x - 0.2f, p.y - 0.8f, p.z - 1f);
    }

    private static void ClearButtonBG(GameObject obj)
    {
        foreach (var sr in obj.GetComponentsInChildren<SpriteRenderer>())
            sr.color = Color.clear;
    }

    private static void CreateIconChild(GameObject parent, string resource)
    {
        var go = new GameObject("VCIcon");
        go.transform.SetParent(parent.transform, false);
        go.transform.localPosition = Vector3.zero;
        go.layer = parent.layer;
        var sr = go.AddComponent<SpriteRenderer>();
        sr.sprite = LoadSprite(resource);
        sr.sortingOrder = 500;
    }

    private static Sprite CreateSolidSprite(Color c)
    {
        var tex = new Texture2D(1, 1);
        tex.SetPixel(0, 0, c); tex.Apply();
        return Sprite.Create(tex, new Rect(0, 0, 1, 1), new Vector2(0.5f, 0.5f));
    }

    private static readonly Dictionary<string, Sprite> _spriteCache = new();

    public static Sprite LoadSprite(string path)
    {
        if (_spriteCache.TryGetValue(path, out var cached)) return cached;
        try
        {
            var tex = new Texture2D(0, 0, TextureFormat.RGBA32, false)
            { wrapMode = TextureWrapMode.Clamp };
            var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(path)!;
            using var ms = new MemoryStream();
            stream.CopyTo(ms);
            tex.LoadImage(ms.ToArray(), false);
            var spr = Sprite.Create(tex, new Rect(0, 0, tex.width, tex.height),
                new Vector2(0.5f, 0.5f), 900f);
            spr.hideFlags |= HideFlags.HideAndDontSave | HideFlags.DontSaveInEditor;
            _spriteCache[path] = spr;
            return spr;
        }
        catch
        {
            VoiceChatPluginMain.Logger.LogError("[VC] Sprite load failed: " + path);
            return null!;
        }
    }

    private static class Sprites
    {
        public static Sprite MicOn => VoiceChatHudState.LoadSprite("VoiceChatPlugin.Resources.MicOn.png");
        public static Sprite MicOff => VoiceChatHudState.LoadSprite("VoiceChatPlugin.Resources.MicOff.png");
        public static Sprite SpkOn => VoiceChatHudState.LoadSprite("VoiceChatPlugin.Resources.SpeakerOn.png");
        public static Sprite SpkOff => VoiceChatHudState.LoadSprite("VoiceChatPlugin.Resources.SpeakerOff.png");
    }
}
