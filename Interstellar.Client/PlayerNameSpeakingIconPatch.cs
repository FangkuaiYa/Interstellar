using HarmonyLib;
using System.Collections.Generic;
using UnityEngine;
using VoiceChatPlugin.VoiceChat;
using Object = UnityEngine.Object;

namespace VoiceChatPlugin;

/// <summary>
/// Displays a microphone icon (Speaking.png) to the left of the name of each
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

            // Don't show icon if the player's name or body is hidden from view
            // (e.g. player is off-screen, in a vent, or invisible via a mod role).
            // Check multiple indicators because different mods use different
            // invisibility mechanisms (Visible flag, alpha fade, renderer toggle).
            bool nameHidden = !pc.cosmetics.nameText.gameObject.activeInHierarchy;
            // Body sprite alpha — Ninja/Fox/Sprinter etc. use Color.Lerp to fade
            // the body to alpha=0 for invisibility (never touch name text alpha).
            float bodyAlpha = pc.cosmetics.currentBodySprite?.BodySprite?.color.a ?? 1f;
            if (nameHidden || pc.inVent || bodyAlpha < 0.01f)
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
                else if (existing.transform.parent != pc.cosmetics.nameText.transform.parent)
                {
                    // Player object changed (e.g. re-spawn) — rebuild.
                    Object.Destroy(existing);
                    IconCache.Remove(id);
                }
                else
                {
                    // Dynamically follow the name text width each frame
                    // so the icon stays to the left even when names change.
                    UpdateIconPosition(existing, pc);
                    continue;
                }
            }

            CreateIcon(pc, id);
        }
    }

    private static void CreateIcon(PlayerControl pc, byte playerId)
    {
        if (_speakingSprite == null) return;

        // Parent to the nameText's parent (sibling of nameText) instead of
        // nameText itself.  This prevents other mods that copy/modify nameText
        // from accidentally cloning the mic icon as well.
        var nameParent = pc.cosmetics.nameText.transform.parent;
        if (nameParent == null) return;

        var go = new GameObject(IconObjectName);
        go.transform.SetParent(nameParent, false);
        go.transform.localScale = Vector3.one * 0.5f;

        // Use the same layer as the name text so shadows/stencil affect both identically.
        go.layer = pc.cosmetics.nameText.gameObject.layer;

        var sr = go.AddComponent<SpriteRenderer>();
        sr.sprite = _speakingSprite;

        // Match the name text's sorting layer and order so the rendering
        // pipeline groups the icon with the name text.
        var nameMr = pc.cosmetics.nameText.GetComponent<MeshRenderer>();
        if (nameMr != null)
        {
            sr.sortingLayerName = nameMr.sortingLayerName;
            sr.sortingLayerID   = nameMr.sortingLayerID;
            sr.sortingOrder     = nameMr.sortingOrder;
        }
        else
        {
            sr.sortingOrder = 10;
        }

        IconCache[playerId] = go;

        // Set initial position based on current text width.
        UpdateIconPosition(go, pc);
    }

    /// <summary>
    /// Positions the icon to the left of the name text, following the
    /// text's rendered width each frame so the icon never overlaps
    /// the name even when it changes (mod role text, long names, etc.).
    /// </summary>
    private static void UpdateIconPosition(GameObject icon, PlayerControl pc)
    {
        var nameText = pc.cosmetics.nameText;
        if (nameText == null) return;

        // Use the rendered text bounds width (local-space) to position
        // the icon just to the left of the text.
        float textHalfWidth = nameText.textBounds.size.x * 0.5f;
        // Fallback if bounds aren't ready yet (first frame after spawn, etc.)
        if (textHalfWidth < 0.01f) textHalfWidth = 0.5f;
        icon.transform.localPosition = new Vector3(-textHalfWidth - 0.25f, 0f, -0.1f);
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
