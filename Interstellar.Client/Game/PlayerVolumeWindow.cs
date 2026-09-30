#pragma warning disable CS8600, CS8602, CS8603, CS8618
using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using Interstellar.UI;

namespace Interstellar.Voice;

/// <summary>
/// Per-player volume host. The standalone F3 window is gone: F3 opens the settings
/// panel on the PLAYER VOLUME rail entry instead, and the panel builds the rows itself
/// through <see cref="BuildRow"/>. What stays here is the data path — the player
/// snapshot, the slider construction (the page's global auto-volume switch makes
/// sliders read-only) and the live meter feed, which the panel drives from its own
/// tick while its rows exist.
/// </summary>
public class PlayerVolumeWindow : MonoBehaviour
{
    public PlayerVolumeWindow(System.IntPtr ptr) : base(ptr) { }

    public static PlayerVolumeWindow? Instance { get; private set; }

    /// <summary>True while the settings panel is showing this category — kept for
    /// VCInputBlockPatch, which counts any of the voice windows as "block the game".</summary>
    public bool ShowWindow { get; private set; }

    private const float VMin = 0f;
    private const float VMax = 2f;
    private const float SpeakingThreshold = 0.01f;

    void Awake() => Instance = this;

    void OnDestroy()
    {
        if (Instance == this) Instance = null;
    }

    void Update()
    {
        // F3: open the settings panel on this category (close it when already there).
        // Standard chord guard: while a rebinding row listens — or until the freshly
        // bound key is released again — the keyboard belongs to that row alone.
        if (!VoiceUiKit.SuppressGlobalHotkeys && VoiceConfig.ChordDown(VoiceChord.PlayerVolume))
            Toggle();
    }

    public void Toggle()
    {
        var win = VoiceSettingsWindow.Instance;
        if (win == null) return;
        if (win.ShowWindow && VoiceSettingsPanel.IsOnCategory(VoiceSettingsPanel.CatVolume))
            win.Close();
        else
            win.OpenCategory(VoiceSettingsPanel.CatVolume);
    }

    /// <summary>Pushed by the settings panel whenever the rail moves.</summary>
    public void SetPanelActive(bool active) => ShowWindow = active;

    // ========================================================
    //  Data path (unchanged) — owned by the settings panel rows
    // ========================================================

    /// <summary>Players in the current voice room, sorted exactly like the old window
    /// listed them. Empty when there is no room; a concurrent mutation of AllClients
    /// (network thread) is reported as "no players yet" and retried on the next tick.</summary>
    internal static List<VCPlayer> SnapshotPlayers()
    {
        try
        {
            var room = VoiceRoom.Current;
            if (room == null) return new List<VCPlayer>();
            return room.AllClients
                .Where(c => c.PlayerId != byte.MaxValue)
                .OrderBy(c => c.PlayerName, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        catch (Exception e)
        {
            InterstellarPlugin.Logger?.LogWarning($"[VC] Player volume snapshot failed: {e.Message}");
            return new List<VCPlayer>();
        }
    }

    /// <summary>Row factory for the settings panel: the per-player slider, value pill
    /// and live meter — parented to the panel's pane at the panel's row height. Auto
    /// volume is the page's global switch now: while <see cref="VoiceConfig.AutoVolume"/>
    /// is on, the slider renders read-only here and <see cref="VCPlayer.SetVolume"/>
    /// refuses the write as well.</summary>
    internal static VoiceUiKit.PlayerVolumeRow BuildRow(
        RectTransform pane, VCPlayer p, string displayName, float paneW, float y, float height)
    {
        string pname = p.PlayerName;
        var pc = FindPlayerControl(p.PlayerId);

        var row = new VoiceUiKit.PlayerVolumeRow(
            () => p.Volume,
            v =>
            {
                p.SetVolume(v);
                VoiceConfig.SetPlayerVolume(pname, v);
            },
            () => VoiceConfig.SetPlayerVolume(pname, p.Volume),
            pc, VMin, VMax,
            () => !VoiceConfig.AutoVolume)
            .Build(pane, displayName, paneW, y, height);
        row.PlayerId = p.PlayerId;
        return row;
    }

    /// <summary>Feeds the live level bars. Called from the settings panel's tick while
    /// the PLAYER VOLUME rows are on screen; rows that died with an earlier rebuild are
    /// already out of that list, so nothing stale is ever touched.</summary>
    internal static void FeedMeters(List<VoiceUiKit.Row> rows)
    {
        VCPlayer[] clients;
        try
        {
            var room = VoiceRoom.Current;
            clients = room == null
                ? Array.Empty<VCPlayer>()
                : room.AllClients.Select(c => c).ToArray();
        }
        catch
        {
            // The network thread can mutate AllClients mid-enumeration: skip this
            // frame's levels and try again on the next tick.
            return;
        }

        for (int i = 0; i < rows.Count; i++)
        {
            if (rows[i] is not VoiceUiKit.PlayerVolumeRow row) continue;

            VCPlayer? player = null;
            for (int j = 0; j < clients.Length; j++)
                if (clients[j].PlayerId == row.PlayerId) { player = clients[j]; break; }

            if (player == null) { row.SetLevel(0f, false); continue; }

            float level = player.Level;
            row.SetLevel(level, level > SpeakingThreshold && player.IsAudible);
        }
    }

    internal static string ResolveDisplayName(VCPlayer p)
    {
        string displayName = "...";
        if (p.IsMapped)
        {
            foreach (var pc in PlayerControl.AllPlayerControls)
            {
                if (pc != null && pc.PlayerId == p.PlayerId)
                {
                    var data = pc.Data;
                    if (data != null && !string.IsNullOrWhiteSpace(data.PlayerName))
                        displayName = data.PlayerName;
                    break;
                }
            }
        }
        if (displayName == "..." && !string.IsNullOrWhiteSpace(p.PlayerName))
            displayName = p.PlayerName;
        return displayName;
    }

    private static PlayerControl FindPlayerControl(byte playerId)
    {
        foreach (var pc in PlayerControl.AllPlayerControls)
            if (pc != null && pc.PlayerId == playerId) return pc;
        return null;
    }
}
