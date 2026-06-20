using HarmonyLib;
using System.Collections.Generic;
using UnityEngine;
using VoiceChatPlugin.VoiceChat;
using Object = UnityEngine.Object;

namespace VoiceChatPlugin;

/// <summary>
/// Displays a microphone icon (Speaking.png) above the name of each
/// player who is currently speaking.  Replaces the old top-of-screen
/// speaking bar that was in PingTrackerPatch.
/// </summary>
[HarmonyPatch(typeof(HudManager), nameof(HudManager.Update))]
public static class PlayerNameSpeakingIconPatch
{
    private const float SpeakingThreshold = 0.01f;
    private const string IconObjectName = "VC_SpeakingIcon";

    /// <summary>Per-player icon GameObject, keyed by PlayerId.</summary>
    private static readonly Dictionary<byte, GameObject> IconCache = new();

    /// <summary>Loaded once on first use.</summary>
    private static Sprite? _speakingSprite;

    static void Postfix()
    {
        // Speaker muted → hide everything
        if (InterstellarHudState.IsSpeakerMuted)
        {
            ClearAllIcons();
            return;
        }

        var room = VoiceChatRoom.Current;
        if (room == null)
        {
            ClearAllIcons();
            return;
        }

        if (_speakingSprite == null)
            _speakingSprite = InterstellarHudState.LoadSpriteFromResources(
                "VoiceChatPlugin.Resources.Speaking.png", 100f);

        // ----- work out who is speaking -----
        var speakingIds = new HashSet<byte>();

        foreach (var c in room.AllClients)
            if (c.PlayerId != byte.MaxValue && c.Level > SpeakingThreshold && c.IsAudible)
                speakingIds.Add(c.PlayerId);

        // Don't show self-speaking indicator when locally muted
        if (PlayerControl.LocalPlayer != null
            && room.LocalMicLevel > SpeakingThreshold
            && !room.Mute)
            speakingIds.Add(PlayerControl.LocalPlayer.PlayerId);

        // ----- remove icons for silent players -----
        var toRemove = new List<byte>();
        foreach (var kv in IconCache)
            if (!speakingIds.Contains(kv.Key))
                toRemove.Add(kv.Key);
        foreach (var id in toRemove)
            RemoveIcon(id);

        // ----- add / keep icons for speaking players -----
        foreach (byte id in speakingIds)
        {
            PlayerControl? pc = FindPlayerById(id);
            if (pc?.cosmetics.nameText == null) continue;

            // Don't show icon if the name object is inactive
            // (e.g. player is off-screen or in a vent).
            if (!pc.cosmetics.nameText.gameObject.activeInHierarchy)
            {
                if (IconCache.ContainsKey(id))
                    RemoveIcon(id);
                continue;
            }

            // Already showing, or the GO was destroyed externally
            if (IconCache.TryGetValue(id, out var existing))
            {
                if (existing == null)
                {
                    IconCache.Remove(id);
                }
                else if (existing.transform.parent != pc.cosmetics.nameText.transform)
                {
                    // Player object changed (e.g. re-spawn) — rebuild.
                    Object.Destroy(existing);
                    IconCache.Remove(id);
                }
                else
                {
                    continue; // icon already present and correct
                }
            }

            CreateIcon(pc, id);
        }
    }

    private static void CreateIcon(PlayerControl pc, byte playerId)
    {
        if (_speakingSprite == null) return;

        var go = new GameObject(IconObjectName);
        go.transform.SetParent(pc.cosmetics.nameText.transform, false);
        go.transform.localPosition = new Vector3(0f, 0.3f, -0.1f);
        go.transform.localScale = Vector3.one * 0.5f;

        // Copy the parent's layer so the icon renders in the same pass.
        go.layer = pc.cosmetics.nameText.gameObject.layer;

        var sr = go.AddComponent<SpriteRenderer>();
        sr.sprite = _speakingSprite;

        // Copy sorting layer and order from the parent nameText's MeshRenderer
        // so the microphone icon respects walls/shadows the same way the name does.
        var parentRenderer = pc.cosmetics.nameText.GetComponent<MeshRenderer>();
        if (parentRenderer != null)
        {
            sr.sortingLayerName = parentRenderer.sortingLayerName;
            sr.sortingLayerID = parentRenderer.sortingLayerID;
            sr.sortingOrder = parentRenderer.sortingOrder + 1;
        }
        else
        {
            sr.sortingOrder = 10;
        }

        IconCache[playerId] = go;
    }

    private static void RemoveIcon(byte playerId)
    {
        if (IconCache.TryGetValue(playerId, out var go))
        {
            if (go != null) Object.Destroy(go);
            IconCache.Remove(playerId);
        }
    }

    private static void ClearAllIcons()
    {
        foreach (var kv in IconCache)
        {
            if (kv.Value != null) Object.Destroy(kv.Value);
        }
        IconCache.Clear();
    }

    private static PlayerControl? FindPlayerById(byte id)
    {
        foreach (var pc in PlayerControl.AllPlayerControls)
            if (pc != null && pc.PlayerId == id)
                return pc;
        return null;
    }

    [HarmonyPatch(typeof(HudManager), nameof(HudManager.Start))]
    private static class HudStartCleanup
    {
        private static void Postfix() => ClearAllIcons();
    }
}
