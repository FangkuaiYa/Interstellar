using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.SceneManagement;
using static VoiceChatPlugin.VoiceChat.TranslationHelper;

namespace VoiceChatPlugin.VoiceChat;

/// <summary>
/// IMGUI-based settings window for Interstellar Voice Chat.
/// Renders Stereo-style: bold centered section headers, box-wrapped setting groups,
/// scroll view, toggles, sliders, and device cycle selectors.
/// </summary>
public class VoiceChatSettingsWindow : MonoBehaviour
{
    // ── Public toggle ─────────────────────────────────────────────────────

    public bool ShowWindow { get; private set; }

    /// <summary>Toggles the window and refreshes device caches if opening.</summary>
    public void Toggle()
    {
        ShowWindow = !ShowWindow;
        if (ShowWindow)
        {
            _needsDeviceRefresh = true;

            // Auto-close the vanilla game settings when VC settings open
            var opt = UnityEngine.Object.FindObjectOfType<OptionsMenuBehaviour>();
            if (opt) opt.Close();
        }
    }

    /// <summary>Closes the window without side effects.</summary>
    public void Close()
    {
        ShowWindow = false;
    }

    // ── IMGUI state ───────────────────────────────────────────────────────

    private Vector2 _scrollPosition;
    private bool _needsDeviceRefresh = true;

    // ── Keyboard shortcut ─────────────────────────────────────────────────
    // F1 toggles the settings window. Change this key if it conflicts.

    private const KeyCode ToggleKey = KeyCode.F1;

    // ── Styles (built once in Awake) ───────────────────────────────────────

    private GUIStyle? _sectionLabelStyle;
    private GUIStyle? _boxStyle;
    private GUIStyle? _titleStyle;
    private bool _stylesBuilt;

    // ── Lifecycle ─────────────────────────────────────────────────────────

    void Awake()
    {
        SceneManager.sceneLoaded += (Action<Scene, LoadSceneMode>)OnSceneLoaded;
    }

    void OnDestroy()
    {
        SceneManager.sceneLoaded -= (Action<Scene, LoadSceneMode>)OnSceneLoaded;
    }

    void Update()
    {
        if (Input.GetKeyDown(ToggleKey))
            Toggle();
    }

    void OnGUI()
    {
        if (!ShowWindow) return;

        // Refresh device caches only when the window first opens (not every frame!)
        if (_needsDeviceRefresh)
        {
            VoiceChatConfig.RefreshDeviceCaches();
            _needsDeviceRefresh = false;
        }

        BuildStyles();

        float winW = 440f;
        float winH = 540f;
        float x = (Screen.width - winW) / 2f;
        float y = (Screen.height - winH) / 2f;

        // Dark backdrop
        var oldColor = GUI.color;
        GUI.color = new Color(0.04f, 0.05f, 0.08f, 0.95f);
        GUI.Box(new Rect(x - 4, y - 4, winW + 8, winH + 8), "");
        GUI.color = new Color(0.06f, 0.08f, 0.12f, 0.98f);
        GUI.Box(new Rect(x, y, winW, winH), "");
        GUI.color = oldColor;

        GUILayout.BeginArea(new Rect(x + 10, y + 8, winW - 20, winH - 16));
        {
            // ── Title bar ──
            GUILayout.BeginHorizontal();
            GUILayout.Label($"<b>{Get("vc.settings.title", "Voice Chat")}</b>", _titleStyle);
            GUILayout.FlexibleSpace();
            GUILayout.Label("F1", new GUIStyle(GUI.skin.label) { fontSize = 10, normal = new GUIStyleState { textColor = Color.gray } });
            GUILayout.Space(4f);
            if (GUILayout.Button("X", GUILayout.Width(28f), GUILayout.Height(20f)))
                Close();
            GUILayout.EndHorizontal();

            GUILayout.Space(5f);

            // ── Scrollable content ──
            _scrollPosition = GUILayout.BeginScrollView(_scrollPosition, GUILayout.Height(winH - 50f));
            {
                bool isHost = AmongUsClient.Instance?.AmHost ?? false;

                RenderPersonalSection();
                GUILayout.Space(20f);
                RenderRoomSection(isHost);
                GUILayout.Space(10f);
            }
            GUILayout.EndScrollView();
        }
        GUILayout.EndArea();
    }

    // ── Style builder ─────────────────────────────────────────────────────

    void BuildStyles()
    {
        if (_stylesBuilt) return;
        _stylesBuilt = true;

        _sectionLabelStyle = new GUIStyle(GUI.skin.label)
        {
            fontStyle = FontStyle.Bold,
            alignment = TextAnchor.MiddleCenter,
            fontSize = 14,
        };
        _sectionLabelStyle.normal.textColor = new Color(0.51f, 0.65f, 0.86f);

        _boxStyle = new GUIStyle(GUI.skin.box)
        {
            padding = new RectOffset
            {
                top = 5,
                bottom = 10,
                left = 10,
                right = 10,
            },
        };

        _titleStyle = new GUIStyle(GUI.skin.label)
        {
            fontStyle = FontStyle.Bold,
            fontSize = 16,
        };
        _titleStyle.normal.textColor = new Color(0.55f, 0.70f, 0.90f);
    }

    // ── Personal section ──────────────────────────────────────────────────

    void RenderPersonalSection()
    {
        GUILayout.Label(Get("vc.settings.personal", "Personal"), _sectionLabelStyle);

        bool showDevices = VoiceChatConfig.DeviceSelectionSupported;

        if (showDevices)
        {
            // Microphone device cycle
            RenderDeviceCycle(
                Get("vc.settings.microphone", "Microphone"),
                VoiceChatConfig.MicrophoneDevice,
                VoiceChatConfig.MicrophoneDevices,
                v =>
                {
                    VoiceChatConfig.SetMicrophoneDevice(v);
                    VoiceChatRoom.Current?.SetMicrophone(v);
                });

            // Speaker device cycle
            RenderDeviceCycle(
                Get("vc.settings.speaker", "Speaker"),
                VoiceChatConfig.SpeakerDevice,
                VoiceChatConfig.SpeakerDevices,
                v =>
                {
                    VoiceChatConfig.SetSpeakerDevice(v);
                    VoiceChatRoom.Current?.SetSpeaker(v);
                });
        }
        else
        {
            // Android / unsupported platform — device selection unavailable
            GUILayout.BeginVertical(_boxStyle);
            GUILayout.Label(Get("vc.settings.noDeviceSupport", "Device selection not supported on this platform."),
                new GUIStyle(GUI.skin.label) { normal = new GUIStyleState { textColor = Color.gray }, wordWrap = true });
            GUILayout.EndVertical();
        }

        // Mic Volume slider
        RenderSlider(Get("vc.settings.micVolume", "Mic Volume"), 0.1f, 2f, VoiceChatConfig.MicVolume, v =>
        {
            VoiceChatConfig.SetMicVolume(v);
            VoiceChatRoom.Current?.SetMicVolume(v);
        });

        // Master Volume slider
        RenderSlider(Get("vc.settings.masterVolume", "Master Volume"), 0.1f, 2f, VoiceChatConfig.MasterVolume, v =>
        {
            VoiceChatConfig.SetMasterVolume(v);
            VoiceChatRoom.Current?.SetMasterVolume(v);
        });
    }

    // ── Room section ──────────────────────────────────────────────────────

    // Static descriptor array — allocated once, not every OnGUI frame
    private static readonly (string label, Func<bool> getter, Action<bool> setter)[] RoomBoolSettings = new (string, Func<bool>, Action<bool>)[]
    {
        (Get("vc.settings.wallsBlockSound", "Walls Block Sound"),          () => VoiceChatConfig.SyncedRoomSettings.WallsBlockSound,       v => VoiceChatConfig.SetHostWallsBlockSound(v)),
        (Get("vc.settings.onlyHearInSight", "Only Hear In Sight"),        () => VoiceChatConfig.SyncedRoomSettings.OnlyHearInSight,       v => VoiceChatConfig.SetHostOnlyHearInSight(v)),
        (Get("vc.settings.impostorHearGhosts", "Impostor Hear Ghosts"),   () => VoiceChatConfig.SyncedRoomSettings.ImpostorHearGhosts,    v => VoiceChatConfig.SetHostImpostorHearGhosts(v)),
        (Get("vc.settings.onlyGhostsCanTalk", "Only Ghosts Can Talk"),    () => VoiceChatConfig.SyncedRoomSettings.OnlyGhostsCanTalk,     v => VoiceChatConfig.SetHostOnlyGhostsCanTalk(v)),
        (Get("vc.settings.hearInVent", "Hear In Vent"),                   () => VoiceChatConfig.SyncedRoomSettings.HearInVent,            v => VoiceChatConfig.SetHostHearInVent(v)),
        (Get("vc.settings.ventPrivateChat", "Vent Private Chat"),         () => VoiceChatConfig.SyncedRoomSettings.VentPrivateChat,       v => VoiceChatConfig.SetHostVentPrivateChat(v)),
        (Get("vc.settings.commsSabotageMutes", "Comms Sabotage Mutes"),   () => VoiceChatConfig.SyncedRoomSettings.CommsSabDisables,      v => VoiceChatConfig.SetHostCommsSabDisables(v)),
        (Get("vc.settings.cameraCanHear", "Camera Can Hear"),             () => VoiceChatConfig.SyncedRoomSettings.CameraCanHear,         v => VoiceChatConfig.SetHostCameraCanHear(v)),
        (Get("vc.settings.impostorPrivateRadio", "Impostor Private Radio"),() => VoiceChatConfig.SyncedRoomSettings.ImpostorPrivateRadio,  v => VoiceChatConfig.SetHostImpostorPrivateRadio(v)),
        (Get("vc.settings.onlyMeetingOrLobby", "Only Meeting / Lobby"),   () => VoiceChatConfig.SyncedRoomSettings.OnlyMeetingOrLobby,    v => VoiceChatConfig.SetHostOnlyMeetingOrLobby(v)),
    };

    void RenderRoomSection(bool isHost)
    {
        GUILayout.Label(Get("vc.settings.room", "Room"), _sectionLabelStyle);

        // Shared change handler for all room settings
        void RoomChanged()
        {
            VoiceChatConfig.ApplyLocalHostSettingsToSynced();
            InterstellarHudState.MarkRoomSettingsDirty();
        }

        // Max Chat Distance
        {
            GUI.enabled = isHost;
            RenderSlider(Get("vc.settings.maxChatDistance", "Max Chat Distance"), 1.5f, 20f,
                isHost ? VoiceChatConfig.HostMaxChatDistance
                       : VoiceChatConfig.SyncedRoomSettings.MaxChatDistance,
                v =>
                {
                    VoiceChatConfig.SetHostMaxChatDistance(v);
                    RoomChanged();
                });
            GUI.enabled = true;
        }

        // Boolean toggles
        foreach (var bs in RoomBoolSettings)
        {
            GUI.enabled = isHost;
            RenderToggle(bs.label, bs.getter(), v =>
            {
                if (isHost)
                {
                    bs.setter(v);
                    RoomChanged();
                }
            });
            GUI.enabled = true;
        }
    }

    // ── Control builders ──────────────────────────────────────────────────

    /// <summary>Renders a ◄ value ► cycle selector inside a box group.</summary>
    void RenderDeviceCycle(string label, string currentValue, List<string> options,
        Action<string> onChange)
    {
        GUILayout.BeginVertical(_boxStyle);
        {
            GUILayout.BeginHorizontal();
            GUILayout.Label(label + ":", GUILayout.Width(130f));

            int idx = Math.Max(0, options.IndexOf(currentValue ?? ""));
            string display = string.IsNullOrEmpty(currentValue)
                ? Get("vc.settings.defaultDevice", "Default")
                : Truncate(currentValue, 20);

            // Only process on the actual click frame (EventType.Used), not held repeats
            if (GUILayout.Button("◄", GUILayout.Width(24f)))
            {
                if (Event.current.type == EventType.Used)
                {
                    idx = (idx - 1 + options.Count) % options.Count;
                    onChange(options[idx]);
                }
            }
            GUILayout.Label(display, GUILayout.Width(160f));
            if (GUILayout.Button("►", GUILayout.Width(24f)))
            {
                if (Event.current.type == EventType.Used)
                {
                    idx = (idx + 1) % options.Count;
                    onChange(options[idx]);
                }
            }
            GUILayout.EndHorizontal();
        }
        GUILayout.EndVertical();
    }

    /// <summary>Renders a labeled HorizontalSlider with value readout inside a box group.</summary>
    void RenderSlider(string label, float min, float max, float value,
        Action<float> onChange)
    {
        GUILayout.BeginVertical(_boxStyle);
        {
            GUILayout.BeginHorizontal();
            GUILayout.Label(label + ":", GUILayout.Width(135f));

            float newVal = GUILayout.HorizontalSlider(value, min, max,
                GUILayout.Width(185f), GUILayout.Height(18f));

            string display = value.ToString("F1");
            GUILayout.Label(display, GUILayout.Width(32f));

            GUILayout.EndHorizontal();

            // Apply change only when slider actually moved
            if (Math.Abs(newVal - value) > 0.001f)
            {
                float clamped = Mathf.Clamp((float)Math.Round(newVal, 2), min, max);
                onChange(clamped);
            }
        }
        GUILayout.EndVertical();
    }

    /// <summary>Renders a single toggle inside a box group.</summary>
    void RenderToggle(string label, bool value, Action<bool> onChange)
    {
        GUILayout.BeginVertical(_boxStyle);
        {
            bool newVal = GUILayout.Toggle(value, " " + label);
            if (newVal != value)
                onChange(newVal);
        }
        GUILayout.EndVertical();
    }

    // ── Helpers ───────────────────────────────────────────────────────────

    static string Truncate(string s, int max)
        => s.Length <= max ? s : s[..(max - 1)] + "…"; // …

    void OnSceneLoaded(Scene _, LoadSceneMode __)
    {
        ShowWindow = false;
    }
}
