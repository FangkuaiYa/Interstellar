using HarmonyLib;
using TMPro;
using System.Linq;
using System.Collections.Generic;
using VoiceChatPlugin.VoiceChat;
using UnityEngine;
using Object = UnityEngine.Object;

namespace VoiceChatPlugin;

[HarmonyPatch(typeof(PingTracker), nameof(PingTracker.Update))]
public static class PingTrackerPatch
{
    private const float SpeakingThreshold = 0.01f;

    private static GameObject? _barRoot;
    private static AspectPosition? _barAspect;

    // GMIA-style: pre-create PoolablePlayer icons at IntroCutscene.OnDestroy, reuse via SetActive
    private static readonly Dictionary<byte, PoolablePlayer> _iconPool = new();
    private static readonly Dictionary<byte, SpeakerSlot> _slots = new();
    private static bool _iconsReady;

    // Pre-create all player icons when the intro cutscene ends (matches GMIA IntroPatch.cs:40-48)
    [HarmonyPatch(typeof(IntroCutscene), nameof(IntroCutscene.OnDestroy))]
    private static class IntroCutsceneOnDestroyPatch
    {
        public static void Prefix(IntroCutscene __instance)
        {
            if (PlayerControl.LocalPlayer == null || HudManager.Instance == null) return;
            if (_iconsReady) return;

            // Ensure the speaking bar exists
            if (_barRoot == null || !_barRoot)
            {
                _barRoot = new GameObject("VC_SpeakingBar");
                _barRoot.transform.SetParent(HudManager.Instance.transform, false);
                _barRoot.transform.localPosition = new Vector3(0f, 0f, -100f);

                _barAspect = _barRoot.AddComponent<AspectPosition>();
                _barAspect.Alignment = AspectPosition.EdgeAlignments.Top;
                _barAspect.DistanceFromEdge = new Vector3(0f, 0.25f, 0f);
                _barAspect.AdjustPosition();
                _barRoot.SetActive(false);
            }

            // Create a PoolablePlayer per player — same pattern as GMIA
            foreach (PlayerControl p in PlayerControl.AllPlayerControls)
            {
                NetworkedPlayerInfo data = p.Data;
                if (data == null) continue;

                // Matches GMIA IntroPatch.cs:42-47 exactly
                PoolablePlayer player = Object.Instantiate(
                    __instance.PlayerPrefab,
                    _barRoot.transform);

                player.name = $"VC_PlayerIcon_{p.PlayerId}";

                p.SetPlayerMaterialColors(player.cosmetics.currentBodySprite.BodySprite);
                player.SetSkin(data.DefaultOutfit.SkinId, data.DefaultOutfit.ColorId);
                player.cosmetics.SetHat(data.DefaultOutfit.HatId, data.DefaultOutfit.ColorId);
                player.cosmetics.nameText.text = data.PlayerName;
                player.SetFlipX(true);

                player.ToggleName(false);
                player.TogglePet(false);
                player.cosmetics.SetBodyCosmeticsVisible(true);
                // Smaller than GMIA's default 0.4f
                player.transform.localScale = Vector3.one * 0.22f;
                player.gameObject.SetActive(false);

                _iconPool[p.PlayerId] = player;
            }

            _iconsReady = true;
        }
    }

    // Per-frame speaker check
    static void Postfix(PingTracker __instance)
    {
        if (__instance?.text == null) return;

        if (InterstellarHudState.IsSpeakerMuted)
        {
            if (_barRoot != null) _barRoot.SetActive(false);
            return;
        }

        if (_barRoot == null) return;

        var room = VoiceChatRoom.Current;

        var speakingIds = new HashSet<byte>();
        if (room != null)
        {
            foreach (var c in room.AllClients)
                if (c.PlayerId != byte.MaxValue && c.Level > SpeakingThreshold)
                    speakingIds.Add(c.PlayerId);

            byte localId = PlayerControl.LocalPlayer
                ? PlayerControl.LocalPlayer.PlayerId : byte.MaxValue;
            if (PlayerControl.LocalPlayer && room.LocalMicLevel > SpeakingThreshold
                && localId != byte.MaxValue)
                speakingIds.Add(localId);
        }

        var toRemove = new List<byte>();
        foreach (var kv in _slots)
            if (!speakingIds.Contains(kv.Key)) toRemove.Add(kv.Key);
        foreach (var id in toRemove) RemoveSlot(id);

        foreach (byte id in speakingIds)
        {
            if (!_slots.ContainsKey(id))
                AddSlot(id);
        }

        LayoutSlots();
        _barRoot.SetActive(speakingIds.Count > 0);
    }

    // AddSlot: prefer pre-created PoolablePlayer, fall back to meeting clone or colored circle
    private static void AddSlot(byte playerId)
    {
        if (_barRoot == null) return;

        var slot = new SpeakerSlot();
        PlayerControl? pc = FindPlayer(playerId);
        bool gotIcon = false;

        // During meetings: clone existing PlayerIcon from PlayerVoteArea
        if (MeetingHud.Instance != null)
        {
            foreach (var state in MeetingHud.Instance.playerStates)
            {
                if (state == null || state.TargetPlayerId != playerId) continue;
                if (state.PlayerIcon == null) break;

                var clone = Object.Instantiate(state.PlayerIcon.gameObject, _barRoot.transform);
                clone.SetActive(true);
                clone.transform.localScale = Vector3.one * 0.45f;
                foreach (var sr in clone.GetComponentsInChildren<SpriteRenderer>())
                    sr.maskInteraction = SpriteMaskInteraction.None;

                slot.IconGO = clone;
                gotIcon = true;
                break;
            }
        }

        // Non-meeting: use pre-created GMIA-style PoolablePlayer
        if (!gotIcon && _iconPool.TryGetValue(playerId, out var pooled) && pooled != null)
        {
            pooled.gameObject.SetActive(true);
            slot.IconGO = pooled.gameObject;
            gotIcon = true;
        }

        // Last resort: colored circle (player not in the pre-created pool yet)
        if (!gotIcon)
        {
            var circleGO = new GameObject("Circle");
            circleGO.transform.SetParent(_barRoot.transform, false);
            var sr = circleGO.AddComponent<SpriteRenderer>();
            sr.sprite = CreateCircleSprite();
            sr.color = GetPaletteColor(pc);
            sr.sortingOrder = 10;
            circleGO.transform.localScale = Vector3.one * 0.28f;
            slot.IconGO = circleGO;
        }

        // Separate name label
        string name = pc?.Data?.PlayerName ?? "?";
        var labelGO = new GameObject("Label");
        labelGO.transform.SetParent(_barRoot.transform, false);
        var tmp = labelGO.AddComponent<TextMeshPro>();
        tmp.text = name;
        tmp.fontSize = 1.3f;
        tmp.alignment = TextAlignmentOptions.Center;
        tmp.enableWordWrapping = false;
        tmp.sortingOrder = 11;
        tmp.color = Color.white;
        tmp.rectTransform.sizeDelta = new Vector2(1.8f, 0.6f);
        slot.LabelTMP = tmp;

        _slots[playerId] = slot;
    }

    // RemoveSlot: hide the PoolablePlayer (keep in pool), destroy label
    private static void RemoveSlot(byte id)
    {
        if (_slots.TryGetValue(id, out var slot))
        {
            // Hide the cached PoolablePlayer (keep in pool for reuse)
            if (_iconPool.TryGetValue(id, out var pooled) && pooled != null)
                pooled.gameObject.SetActive(false);

            // Only destroy non-pooled objects (meeting clones, colored circles)
            if (slot.IconGO != null && !_iconPool.ContainsValue(slot.IconGO.GetComponent<PoolablePlayer>()))
                Object.Destroy(slot.IconGO);

            if (slot.LabelTMP != null) Object.Destroy(slot.LabelTMP.gameObject);
            _slots.Remove(id);
        }
    }

    // Layout
    private static void LayoutSlots()
    {
        float slotWidth = 0.75f;
        float totalWidth = _slots.Count * slotWidth;
        float startX = -totalWidth * 0.5f + slotWidth * 0.5f;

        int i = 0;
        foreach (var kv in _slots)
        {
            float x = startX + i * slotWidth;
            if (kv.Value.IconGO != null)
                kv.Value.IconGO.transform.localPosition = new Vector3(x, 0.05f, i * -0.01f);
            if (kv.Value.LabelTMP != null)
                kv.Value.LabelTMP.transform.localPosition = new Vector3(x, -0.35f, 0f);
            i++;
        }
    }

    // Cleanup on game end
    [HarmonyPatch(typeof(HudManager), nameof(HudManager.Start))]
    private static class HudStartPatch
    {
        private static void Postfix()
        {
            foreach (var kv in _slots)
            {
                if (kv.Value.IconGO != null && !_iconPool.ContainsValue(kv.Value.IconGO.GetComponent<PoolablePlayer>()))
                    Object.Destroy(kv.Value.IconGO);
                if (kv.Value.LabelTMP != null) Object.Destroy(kv.Value.LabelTMP.gameObject);
            }
            _slots.Clear();

            foreach (var kv in _iconPool)
            {
                if (kv.Value != null && kv.Value.gameObject != null)
                    Object.Destroy(kv.Value.gameObject);
            }
            _iconPool.Clear();
            _iconsReady = false;

            if (_barRoot != null) { Object.Destroy(_barRoot); _barRoot = null; }
            _barAspect = null;
        }
    }

    // Utility methods
    private static PlayerControl? FindPlayer(byte id)
    {
        foreach (var pc in PlayerControl.AllPlayerControls)
            if (pc != null && pc.PlayerId == id) return pc;
        return null;
    }

    private static Color GetPaletteColor(PlayerControl? pc)
    {
        if (pc?.Data == null) return new Color(0.18f, 0.80f, 0.44f, 1f);
        int cid = pc.Data.DefaultOutfit.ColorId;
        if (cid >= 0 && cid < Palette.PlayerColors.Length)
            return Palette.PlayerColors[cid];
        return Color.white;
    }

    private static Sprite? _circleSprite;
    private static Sprite CreateCircleSprite()
    {
        if (_circleSprite != null) return _circleSprite;
        int size = 64;
        var tex = new Texture2D(size, size, TextureFormat.RGBA32, false);
        float r = size * 0.5f;
        for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
            {
                float dx = x - r + 0.5f, dy = y - r + 0.5f;
                float dist = Mathf.Sqrt(dx * dx + dy * dy);
                float alpha = Mathf.Clamp01((r - dist) * 2f);
                tex.SetPixel(x, y, new Color(1, 1, 1, alpha));
            }
        tex.Apply();
        _circleSprite = Sprite.Create(
            tex, new Rect(0, 0, size, size), new Vector2(0.5f, 0.5f), size);
        return _circleSprite;
    }

    private class SpeakerSlot
    {
        public GameObject? IconGO;
        public TextMeshPro? LabelTMP;
    }
}
