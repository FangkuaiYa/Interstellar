using System;
using System.Collections.Generic;
using HarmonyLib;
using TMPro;
using UnityEngine;
using UnityEngine.Events;
using static UnityEngine.UI.Button;
using Object = UnityEngine.Object;

namespace VoiceChatPlugin.VoiceChat;

[HarmonyPatch]
public static class VoiceChatOptionsPatches
{
    private static GameObject? _popUp;
    private static ToggleButtonBehaviour? _buttonPrefab;
    private static TextMeshPro? _titleTmp;

    private static readonly List<string> _micDevices = new();
    private static readonly List<string> _spkDevices = new();

    private const string ButtonName = "VoiceChatOptionsButton";

    // ── Prefab setup ────────────────────────────────────────────────────

    [HarmonyPatch(typeof(MainMenuManager), nameof(MainMenuManager.Start))]
    [HarmonyPostfix]
    static void MainMenuManager_Start()
    {
        if (!_titleTmp)
        {
            var go = new GameObject("VCTitle");
            var tmp = go.AddComponent<TextMeshPro>();
            tmp.fontSize = 4;
            tmp.alignment = TextAlignmentOptions.Center;
            tmp.transform.localPosition += Vector3.left * 0.2f;
            _titleTmp = Object.Instantiate(tmp);
            _titleTmp.gameObject.SetActive(false);
            Object.DontDestroyOnLoad(_titleTmp);
        }

        if (_popUp) { Object.Destroy(_popUp); _popUp = null; }
    }

    [HarmonyPatch(typeof(OptionsMenuBehaviour), nameof(OptionsMenuBehaviour.Start))]
    [HarmonyPostfix]
    static void OptionsMenu_Start(OptionsMenuBehaviour __instance)
    {
        if (!__instance.CensorChatButton) return;
        if (!_popUp) BuildPopup(__instance);
        if (!_buttonPrefab)
        {
            _buttonPrefab = Object.Instantiate(__instance.CensorChatButton);
            Object.DontDestroyOnLoad(_buttonPrefab);
            _buttonPrefab.name = "VC_ButtonPrefab";
            _buttonPrefab.gameObject.SetActive(false);
        }
        AddEntryButton(__instance);
    }

    // ── Popup shell (clone OptionsMenuBehaviour, keep BG + close btn) ────

    private static void BuildPopup(OptionsMenuBehaviour src)
    {
        _popUp = Object.Instantiate(src.gameObject);
        Object.DontDestroyOnLoad(_popUp);
        var t = _popUp.transform;
        var p = t.localPosition; p.z = -860f; t.localPosition = p;
        Object.Destroy(_popUp.GetComponent<OptionsMenuBehaviour>());

        for (int i = t.childCount - 1; i >= 0; i--)
        {
            var ch = t.GetChild(i).gameObject;
            if (ch.name is not ("Background" or "CloseButton"))
                Object.Destroy(ch);
        }

        var bg = t.Find("Background")?.GetComponent<SpriteRenderer>();
        if (bg) bg.color = new Color32(12, 14, 22, 242);
        bg.sortingOrder = 32766;

        var close = _popUp.GetComponentInChildren<PassiveButton>();
        if (close)
        {
            close.OnClick = new ButtonClickedEvent();
            close.OnClick.AddListener((Action)(() => _popUp!.SetActive(false)));
        }

        _popUp.SetActive(false);
    }

    // ── VC entry button in options menu ──────────────────────────────────

    private static void AddEntryButton(OptionsMenuBehaviour inst)
    {
        var parent = inst.CensorChatButton.transform.parent;
        var existing = parent.Find(ButtonName);
        if (existing) Object.Destroy(existing.gameObject);

        var btn = Object.Instantiate(_buttonPrefab!, parent);
        btn.name = ButtonName;
        bool inGame = AmongUsClient.Instance?.GameState == InnerNet.InnerNetClient.GameStates.Joined;
        btn.transform.localPosition = inGame
            ? new Vector3(-1.94f, -1.58f, 0f)
            : new Vector3(-1.34f, 2.99f, 0f);
        btn.transform.localScale = new Vector3(0.49f, 0.82f, 1f);
        btn.Text.text = "VC";
        btn.Text.transform.localScale = new Vector3(1.8f, 0.95f, 1f);
        btn.gameObject.SetActive(true);

        var pb = btn.GetComponent<PassiveButton>();
        pb.OnClick = new ButtonClickedEvent();
        pb.OnClick.AddListener((Action)(() =>
        {
            if (!_popUp) return;
            bool closeUnderlying = inst.transform.parent == HudManager.Instance?.transform;
            if (closeUnderlying)
            {
                _popUp.transform.SetParent(HudManager.Instance!.transform);
                _popUp.transform.localPosition = new Vector3(0f, 0f, -860f);
            }
            else
            {
                _popUp.transform.SetParent(null);
                Object.DontDestroyOnLoad(_popUp);
            }

            CheckSetTitle();
            RefreshContent();
            if (closeUnderlying) inst.Close();
        }));
    }

    // ── Refresh content ──────────────────────────────────────────────────

    private static void CheckSetTitle()
    {
        if (!_popUp || !_titleTmp || _popUp.transform.Find("VCTitle")) return;
        var t = Object.Instantiate(_titleTmp, _popUp.transform);
        t.GetComponent<RectTransform>().localPosition = Vector3.up * 2.3f;
        t.gameObject.SetActive(true);
        t.text = VoiceChatLocalization.Tr("header");
        t.name = "VCTitle";
        t.sortingOrder = 32767;
    }

    private static void RefreshContent()
    {
        if (!_popUp || !_buttonPrefab) return;
        var content = _popUp.transform;

        // Destroy old content (keep Background, CloseButton, VCTitle)
        for (int i = content.childCount - 1; i >= 0; i--)
        {
            var ch = content.GetChild(i).gameObject;
            if (ch.name is "Background" or "CloseButton" or "VCTitle") continue;
            Object.Destroy(ch);
        }

        RefreshDeviceCaches();
        BuildContent();
        _popUp.SetActive(true);
    }

    private static void BuildContent()
    {
        int idx = 0;

        // Mic volume +/-
        MakeLabel("micVolume", ref idx);
        float micVol = VoiceChatConfig.MicVolume;
        MakeValueRow(ref idx,
            () => $"{micVol:0.00}",
            () => { micVol = Mathf.Clamp(micVol - 0.1f, 0.1f, 2f); VoiceChatConfig.SetMicVolume(micVol); VoiceChatRoom.Current?.SetMicVolume(micVol); RefreshContent(); },
            () => { micVol = Mathf.Clamp(micVol + 0.1f, 0.1f, 2f); VoiceChatConfig.SetMicVolume(micVol); VoiceChatRoom.Current?.SetMicVolume(micVol); RefreshContent(); });

        // Mic device cycle
        MakeLabel("microphone", ref idx);
        string micDev = _micDevices.Count > 0 ? (_micDevices[CycleIdx(_micDevices, VoiceChatConfig.MicrophoneDevice)] ?? "Default") : "Default";
        MakeCycleRow(ref idx, micDev,
            () => CycleDevice(_micDevices, VoiceChatConfig.MicrophoneDevice, -1, v => { VoiceChatConfig.SetMicrophoneDevice(v); VoiceChatRoom.Current?.SetMicrophone(v); VoiceChatRoom.Current?.SetMicVolume(VoiceChatConfig.MicVolume); }),
            () => CycleDevice(_micDevices, VoiceChatConfig.MicrophoneDevice, 1, v => { VoiceChatConfig.SetMicrophoneDevice(v); VoiceChatRoom.Current?.SetMicrophone(v); VoiceChatRoom.Current?.SetMicVolume(VoiceChatConfig.MicVolume); }));

        // Master volume +/-
        MakeLabel("speakerVolume", ref idx);
        float masterVol = VoiceChatConfig.MasterVolume;
        MakeValueRow(ref idx,
            () => $"{masterVol:0.00}",
            () => { masterVol = Mathf.Clamp(masterVol - 0.1f, 0.1f, 2f); VoiceChatConfig.SetMasterVolume(masterVol); VoiceChatRoom.Current?.SetMasterVolume(masterVol); RefreshContent(); },
            () => { masterVol = Mathf.Clamp(masterVol + 0.1f, 0.1f, 2f); VoiceChatConfig.SetMasterVolume(masterVol); VoiceChatRoom.Current?.SetMasterVolume(masterVol); RefreshContent(); });

        // Speaker device cycle
        MakeLabel("speaker", ref idx);
        string spkDev = _spkDevices.Count > 0 ? (_spkDevices[CycleIdx(_spkDevices, VoiceChatConfig.SpeakerDevice)] ?? "Default") : "Default";
        MakeCycleRow(ref idx, spkDev,
            () => CycleDevice(_spkDevices, VoiceChatConfig.SpeakerDevice, -1, v => { VoiceChatConfig.SetSpeakerDevice(v); VoiceChatRoom.Current?.SetSpeaker(v); }),
            () => CycleDevice(_spkDevices, VoiceChatConfig.SpeakerDevice, 1, v => { VoiceChatConfig.SetSpeakerDevice(v); VoiceChatRoom.Current?.SetSpeaker(v); }));
    }

    // ── UI widgets ───────────────────────────────────────────────────────

    private static void MakeLabel(string key, ref int idx)
    {
        var btn = Object.Instantiate(_buttonPrefab!, _popUp!.transform);
        btn.transform.localPosition = new Vector3(0f, 1.8f - idx * 0.7f, -0.5f);
        btn.transform.localScale = new Vector3(0.9f, 0.8f, 1f);
        btn.Text.text = VoiceChatLocalization.Tr(key);
        btn.Text.fontSizeMin = btn.Text.fontSizeMax = 1.6f;
        btn.Text.color = new Color32(130, 165, 220, 255);
        btn.Text.alignment = TextAlignmentOptions.Left;
        btn.Background.color = Color.clear;
        btn.onState = false;
        btn.gameObject.SetActive(true);
        foreach (var sr in btn.GetComponentsInChildren<SpriteRenderer>())
            sr.size = new Vector2(3f, 0.45f);
        idx++;
    }

    private static void MakeValueRow(ref int idx, Func<string> valText, Action onMinus, Action onPlus)
    {
        float y = 1.8f - idx * 0.7f;
        SmallBtn("−", new Vector3(-1.5f, y, -0.5f), onMinus);
        SmallBtn("+", new Vector3(1.5f, y, -0.5f), onPlus);

        var btn = Object.Instantiate(_buttonPrefab!, _popUp!.transform);
        btn.transform.localPosition = new Vector3(0f, y, -0.5f);
        btn.transform.localScale = new Vector3(0.7f, 0.8f, 1f);
        btn.Text.text = valText();
        btn.Text.fontSizeMin = btn.Text.fontSizeMax = 1.4f;
        btn.Text.alignment = TextAlignmentOptions.Center;
        btn.Text.color = Color.white;
        btn.Background.color = new Color32(24, 30, 50, 255);
        btn.onState = false;
        btn.gameObject.SetActive(true);
        foreach (var sr in btn.GetComponentsInChildren<SpriteRenderer>())
            sr.size = new Vector2(2f, 0.45f);
        idx++;
    }

    private static void MakeCycleRow(ref int idx, string current, Action onPrev, Action onNext)
    {
        float y = 1.8f - idx * 0.7f;
        SmallBtn("◀", new Vector3(-1.5f, y, -0.5f), onPrev);
        SmallBtn("▶", new Vector3(1.5f, y, -0.5f), onNext);

        var btn = Object.Instantiate(_buttonPrefab!, _popUp!.transform);
        btn.transform.localPosition = new Vector3(0f, y, -0.5f);
        btn.transform.localScale = new Vector3(0.7f, 0.8f, 1f);
        btn.Text.text = current.Length > 22 ? current[..20] + "…" : current;
        btn.Text.fontSizeMin = btn.Text.fontSizeMax = 1.2f;
        btn.Text.alignment = TextAlignmentOptions.Center;
        btn.Text.color = Color.white;
        btn.Background.color = new Color32(24, 30, 50, 255);
        btn.onState = false;
        btn.gameObject.SetActive(true);
        foreach (var sr in btn.GetComponentsInChildren<SpriteRenderer>())
            sr.size = new Vector2(2.6f, 0.45f);
        idx++;
    }

    private static void SmallBtn(string label, Vector3 pos, Action onClick)
    {
        var btn = Object.Instantiate(_buttonPrefab!, _popUp!.transform);
        btn.transform.localPosition = pos;
        btn.transform.localScale = new Vector3(0.4f, 0.7f, 1f);
        btn.Text.text = label;
        btn.Text.fontSizeMin = btn.Text.fontSizeMax = 1.8f;
        btn.Text.alignment = TextAlignmentOptions.Center;
        btn.Text.color = new Color32(200, 215, 255, 255);
        btn.Background.color = new Color32(52, 64, 98, 255);
        btn.onState = false;
        btn.gameObject.SetActive(true);
        foreach (var sr in btn.GetComponentsInChildren<SpriteRenderer>())
            sr.size = new Vector2(0.6f, 0.42f);
        var pb = btn.GetComponent<PassiveButton>();
        pb.OnClick = new ButtonClickedEvent();
        pb.OnClick.AddListener((Action)(() => { onClick(); RefreshContent(); }));
        pb.OnMouseOver = new UnityEvent();
        pb.OnMouseOut = new UnityEvent();
        pb.OnMouseOver.AddListener((Action)(() => btn.Background.color = new Color32(75, 90, 130, 255)));
        pb.OnMouseOut.AddListener((Action)(() => btn.Background.color = new Color32(52, 64, 98, 255)));
    }

    // ── Device helpers ──────────────────────────────────────────────────

    private static int CycleIdx(List<string> devs, string cur)
    {
        for (int i = 0; i < devs.Count; i++)
            if (devs[i] == cur) return i;
        return 0;
    }

    private static void CycleDevice(List<string> devs, string cur, int dir, Action<string> apply)
    {
        int idx = CycleIdx(devs, cur);
        idx = (idx + dir + devs.Count) % devs.Count;
        apply(devs[idx]);
        RefreshContent();
    }

    private static void RefreshDeviceCaches()
    {
        _micDevices.Clear();
        _micDevices.Add("");
        try
        {
            for (int i = 0; i < NAudio.Wave.WaveInEvent.DeviceCount; i++)
            {
                var cap = NAudio.Wave.WaveInEvent.GetCapabilities(i);
                if (!string.IsNullOrWhiteSpace(cap.ProductName))
                    _micDevices.Add(cap.ProductName);
            }
        }
        catch { }

        _spkDevices.Clear();
        _spkDevices.Add("");
        try
        {
            using var e = new NAudio.CoreAudioApi.MMDeviceEnumerator();
            foreach (var dev in e.EnumerateAudioEndPoints(NAudio.CoreAudioApi.DataFlow.Render, NAudio.CoreAudioApi.DeviceState.Active))
                if (!string.IsNullOrWhiteSpace(dev.FriendlyName))
                    _spkDevices.Add(dev.FriendlyName);
        }
        catch { }
    }
}
