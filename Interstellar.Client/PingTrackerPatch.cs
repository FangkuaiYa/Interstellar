using HarmonyLib;
using TMPro;
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

    private static readonly Dictionary<byte, SpeakerSlot> _slots = new();

    static void Postfix(PingTracker __instance)
    {
        if (__instance?.text == null) return;

        if (InterstellarHudState.IsSpeakerMuted)
        {
            if (_barRoot != null) _barRoot.SetActive(false);
            return;
        }

        EnsureBar(__instance);
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

    private static void EnsureBar(PingTracker template)
    {
        if (_barRoot != null && _barRoot) return;

        _barRoot = new GameObject("VC_SpeakingBar");
        _barRoot.transform.SetParent(template.transform.parent, false);

        _barAspect = _barRoot.AddComponent<AspectPosition>();
        _barAspect.Alignment = AspectPosition.EdgeAlignments.Top;
        _barAspect.DistanceFromEdge = new Vector3(0f, 0.35f, 0f);
        _barAspect.AdjustPosition();

        _barRoot.SetActive(false);
    }

    private static void AddSlot(byte playerId)
    {
        if (_barRoot == null) return;

        var slot = new SpeakerSlot();
        PlayerControl? pc = FindPlayer(playerId);
        Color playerColor = GetPaletteColor(pc);

        // Colored circle icon
        var circleGO = new GameObject("Circle");
        circleGO.transform.SetParent(_barRoot.transform, false);
        var sr = circleGO.AddComponent<SpriteRenderer>();
        sr.sprite = CreateCircleSprite();
        sr.color = playerColor;
        sr.sortingOrder = 10;
        circleGO.transform.localScale = Vector3.one * 0.35f;
        slot.CircleGO = circleGO;

        // Player name label — white
        string name = pc?.Data?.PlayerName ?? "?";
        var labelGO = new GameObject("Label");
        labelGO.transform.SetParent(_barRoot.transform, false);
        var tmp = labelGO.AddComponent<TextMeshPro>();
        tmp.text = name;
        tmp.fontSize = 1.1f;
        tmp.alignment = TextAlignmentOptions.Center;
        tmp.enableWordWrapping = false;
        tmp.sortingOrder = 11;
        tmp.color = Color.white;
        tmp.rectTransform.sizeDelta = new Vector2(1.6f, 0.5f);
        slot.LabelTMP = tmp;

        _slots[playerId] = slot;
    }

    private static void RemoveSlot(byte id)
    {
        if (_slots.TryGetValue(id, out var slot))
        {
            if (slot.CircleGO != null) Object.Destroy(slot.CircleGO);
            if (slot.LabelTMP != null) Object.Destroy(slot.LabelTMP.gameObject);
            _slots.Remove(id);
        }
    }

    private static void LayoutSlots()
    {
        float slotWidth = 0.7f;
        float totalWidth = _slots.Count * slotWidth;
        float startX = -totalWidth * 0.5f + slotWidth * 0.5f;

        int i = 0;
        foreach (var kv in _slots)
        {
            float x = startX + i * slotWidth;
            if (kv.Value.CircleGO != null)
                kv.Value.CircleGO.transform.localPosition = new Vector3(x, 0.05f, 0f);
            if (kv.Value.LabelTMP != null)
                kv.Value.LabelTMP.transform.localPosition = new Vector3(x, -0.28f, 0f);
            i++;
        }
    }

    [HarmonyPatch(typeof(HudManager), nameof(HudManager.Start))]
    private static class HudStartPatch
    {
        private static void Postfix()
        {
            foreach (var kv in _slots)
            {
                if (kv.Value.CircleGO != null) Object.Destroy(kv.Value.CircleGO);
                if (kv.Value.LabelTMP != null) Object.Destroy(kv.Value.LabelTMP.gameObject);
            }
            _slots.Clear();

            if (_barRoot != null) { Object.Destroy(_barRoot); _barRoot = null; }
            _barAspect = null;
        }
    }

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
        public GameObject? CircleGO;
        public TextMeshPro? LabelTMP;
    }
}
