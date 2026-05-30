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
public static class VoiceVolumeMenu
{
    private static GameObject? _window;
    private static ToggleButtonBehaviour? _btnPrefab;
    private static float _scrollOffset;

    private const float WindowW = 5.6f;
    private const float WindowH = 4.6f;
    private const float RowH = 0.68f;
    private const float SliderW = 1.80f;
    private const float IconScale = 0.38f;
    private const float VMin = 0f;
    private const float VMax = 2f;

    public static void Toggle()
    {
        if (!_window)
        {
            _window = null; // clear dead ref
            Build();
        }
        else
        {
            bool next = !_window.activeSelf;
            _window.SetActive(next);
            if (next) Refresh();
        }
    }

    public static void Close()
    {
        if (_window) _window.SetActive(false);
    }

    private static void Build()
    {
        if (!HudManager.Instance) return;

        _btnPrefab ??= FindButtonPrefab();
        if (!_btnPrefab) return;

        _window = new GameObject("VC_VolumeMenu");
        _window.transform.SetParent(HudManager.Instance.transform, false);
        _window.transform.localPosition = new Vector3(0f, 0f, -870f);

        var bgGO = new GameObject("BG");
        bgGO.transform.SetParent(_window.transform, false);
        var bgSr = bgGO.AddComponent<SpriteRenderer>();
        bgSr.sprite = Create1x1Sprite(new Color32(10, 13, 22, 240));
        bgSr.drawMode = SpriteDrawMode.Sliced;
        bgSr.size = new Vector2(WindowW, WindowH);
        bgSr.sortingOrder = 32766;

        var titleGO = new GameObject("Title");
        titleGO.transform.SetParent(_window.transform, false);
        titleGO.transform.localPosition = new Vector3(0f, WindowH * 0.5f - 0.35f, -0.1f);
        var titleTmp = titleGO.AddComponent<TextMeshPro>();
        titleTmp.text = "<b>" + VoiceChatLocalization.Tr("playerVolumes") + "</b>";
        titleTmp.fontSize = 1.8f;
        titleTmp.alignment = TextAlignmentOptions.Center;
        titleTmp.sortingOrder = 32767;
        titleTmp.color = new Color32(175, 215, 255, 255);
        titleTmp.rectTransform.sizeDelta = new Vector2(WindowW - 0.4f, 0.5f);

        CreateSmallTextButton("✕", new Vector3(WindowW * 0.5f - 0.3f, WindowH * 0.5f - 0.28f, -0.2f),
            () => { if (_window) _window.SetActive(false); });

        CreateSmallTextButton("▲", new Vector3(WindowW * 0.5f - 0.28f, 0.5f, -0.2f),
            () => { _scrollOffset = Mathf.Max(0f, _scrollOffset - RowH); Refresh(); });
        CreateSmallTextButton("▼", new Vector3(WindowW * 0.5f - 0.28f, -0.5f, -0.2f),
            () => { _scrollOffset += RowH; Refresh(); });

        var contentGO = new GameObject("Content");
        contentGO.transform.SetParent(_window.transform, false);
        contentGO.transform.localPosition = new Vector3(0f, WindowH * 0.5f - 0.8f, -0.1f);

        var maskGO = new GameObject("Mask");
        maskGO.transform.SetParent(_window.transform, false);
        maskGO.transform.localPosition = new Vector3(0f, 0f, -0.05f);
        var mask = maskGO.AddComponent<SpriteMask>();
        mask.sprite = Create1x1Sprite(Color.white);
        maskGO.transform.localScale = new Vector3(WindowW - 0.2f, WindowH - 1.0f, 1f);

        _window.SetActive(true);
        Refresh();
    }

    private static void Refresh()
    {
        if (!_window) return;

        var content = _window.transform.Find("Content");
        if (!content) return;

        var players = CollectPlayers();

        const float visibleH = WindowH - 1.0f;
        int maxRows = Mathf.FloorToInt(visibleH / RowH);
        int startIdx = Mathf.FloorToInt(_scrollOffset / RowH);
        startIdx = Mathf.Clamp(startIdx, 0, Mathf.Max(0, players.Count - maxRows));
        _scrollOffset = startIdx * RowH;

        // Pool: destroy excess rows, keep existing ones, create new if needed
        int needed = Math.Min(players.Count - startIdx, maxRows);
        if (needed < 0) needed = 0;

        while (content.childCount > needed)
            Object.Destroy(content.GetChild(content.childCount - 1).gameObject);

        for (int i = 0; i < needed; i++)
        {
            var player = players[startIdx + i];
            Transform row;
            if (i < content.childCount)
                row = content.GetChild(i);
            else
                row = CreateRow(content, i).transform;
            UpdateRow(row, player, i);
        }

        if (players.Count == 0 && content.childCount == 0)
        {
            var hint = new GameObject("NoPlayers");
            hint.transform.SetParent(content, false);
            hint.transform.localPosition = new Vector3(0f, -0.2f, 0f);
            var tmp = hint.AddComponent<TextMeshPro>();
            tmp.text = VoiceChatLocalization.Tr("noPlayersInRoom");
            tmp.fontSize = 1.4f; tmp.alignment = TextAlignmentOptions.Center;
            tmp.sortingOrder = 32767;
            tmp.color = new Color32(140, 160, 200, 200);
            tmp.rectTransform.sizeDelta = new Vector2(WindowW - 0.6f, 0.6f);
        }
    }

    private static GameObject CreateRow(Transform parent, int index)
    {
        var rowGO = new GameObject($"Row_{index}");
        rowGO.transform.SetParent(parent, false);

        // Icon placeholder (filled by UpdateRow)
        var circleGO = new GameObject("Icon");
        circleGO.transform.SetParent(rowGO.transform, false);
        circleGO.transform.localPosition = new Vector3(-2.4f, 0.08f, -0.1f);
        circleGO.transform.localScale = Vector3.one * (IconScale * 0.6f);
        var sr = circleGO.AddComponent<SpriteRenderer>();
        sr.sprite = GetCircleSprite();
        sr.sortingOrder = 32767;
        sr.maskInteraction = SpriteMaskInteraction.VisibleInsideMask;
        rowGO.name = "Row"; // placeholder until UpdateRow renames

        // Name label
        var nameGO = new GameObject("Name");
        nameGO.transform.SetParent(rowGO.transform, false);
        nameGO.transform.localPosition = new Vector3(-1.50f, 0f, -0.1f);
        var nameTmp = nameGO.AddComponent<TextMeshPro>();
        nameTmp.fontSize = 1.25f; nameTmp.alignment = TextAlignmentOptions.Left;
        nameTmp.sortingOrder = 32767; nameTmp.color = Color.white;
        nameTmp.enableWordWrapping = false;
        nameTmp.rectTransform.sizeDelta = new Vector2(1.5f, 0.4f);

        // Volume label
        var volGO = new GameObject("VolLabel");
        volGO.transform.SetParent(rowGO.transform, false);
        volGO.transform.localPosition = new Vector3(1.75f, 0f, -0.1f);
        var volTmp = volGO.AddComponent<TextMeshPro>();
        volTmp.sortingOrder = 32767; volTmp.fontSize = 1.2f;
        volTmp.alignment = TextAlignmentOptions.Left; volTmp.color = Color.white;
        volTmp.enableWordWrapping = false;
        volTmp.rectTransform.sizeDelta = new Vector2(0.7f, 0.4f);

        // Slider track
        var trackGO = new GameObject("Track");
        trackGO.transform.SetParent(rowGO.transform, false);
        trackGO.transform.localPosition = new Vector3(0.35f, 0f, -0.1f);
        var trackSr = trackGO.AddComponent<SpriteRenderer>();
        trackSr.sprite = Create1x1Sprite(new Color32(55, 65, 100, 200));
        trackSr.drawMode = SpriteDrawMode.Sliced; trackSr.size = new Vector2(SliderW, 0.10f);
        trackSr.sortingOrder = 32766;
        trackSr.maskInteraction = SpriteMaskInteraction.VisibleInsideMask;

        // Fill
        var fillGO = new GameObject("Fill");
        fillGO.transform.SetParent(trackGO.transform, false);
        var fillSr = fillGO.AddComponent<SpriteRenderer>();
        fillSr.sprite = Create1x1Sprite(new Color32(80, 160, 235, 220));
        fillSr.drawMode = SpriteDrawMode.Sliced; fillSr.sortingOrder = 32767;
        fillSr.maskInteraction = SpriteMaskInteraction.VisibleInsideMask;

        // Knob
        var knobGO = new GameObject("Knob");
        knobGO.transform.SetParent(trackGO.transform, false);
        var knobSr = knobGO.AddComponent<SpriteRenderer>();
        knobSr.sprite = GetCircleSprite(); knobSr.color = Color.white;
        knobSr.sortingOrder = 32767;
        knobSr.maskInteraction = SpriteMaskInteraction.VisibleInsideMask;
        knobGO.transform.localScale = Vector3.one * 0.16f;

        // Divider
        var divGO = new GameObject("Div");
        divGO.transform.SetParent(rowGO.transform, false);
        divGO.transform.localPosition = new Vector3(0f, -RowH * 0.5f + 0.04f, -0.08f);
        var divSr = divGO.AddComponent<SpriteRenderer>();
        divSr.sprite = Create1x1Sprite(new Color32(50, 60, 90, 120));
        divSr.drawMode = SpriteDrawMode.Sliced;
        divSr.size = new Vector2(WindowW - 0.4f, 0.012f);
        divSr.sortingOrder = 32766;
        divSr.maskInteraction = SpriteMaskInteraction.VisibleInsideMask;

        return rowGO;
    }

    private static void UpdateRow(Transform row, PlayerEntry entry, int index)
    {
        row.name = $"Row_{entry.Name}";
        row.localPosition = new Vector3(0f, -index * RowH, 0f);

        // Icon color
        var icon = row.Find("Icon");
        if (icon) { var sr = icon.GetComponent<SpriteRenderer>(); if (sr) sr.color = entry.Color; }

        // Name
        var nameTmp = row.Find("Name")?.GetComponent<TextMeshPro>();
        if (nameTmp) nameTmp.text = entry.Name.Length > 14 ? entry.Name[..12] + "…" : entry.Name;

        // Volume label
        var volTmp = row.Find("VolLabel")?.GetComponent<TextMeshPro>();
        float currentVol = 1f;
        if (VoiceChatRoom.Current != null)
            foreach (var c in VoiceChatRoom.Current.AllClients)
                if (c.PlayerName == entry.Name || c.PlayerId == entry.PlayerId)
                { currentVol = c.Volume; break; }

        if (volTmp)
            volTmp.text = $"<color=#ffdd88>{Mathf.RoundToInt(currentVol * 100f)}%</color>";

        // Slider
        var track = row.Find("Track");
        var fill = track?.Find("Fill");
        var knob = track?.Find("Knob");
        var fillSr = fill?.GetComponent<SpriteRenderer>();
        if (track && fillSr && knob)
        {
            float t = Mathf.InverseLerp(VMin, VMax, currentVol);
            float kX = (t - 0.5f) * SliderW;
            knob.localPosition = new Vector3(kX, 0f, -0.12f);
            fillSr.size = new Vector2(t * SliderW, 0.10f);
            fill.localPosition = new Vector3((t * SliderW - SliderW) * 0.5f, 0f, -0.11f);
        }

        // Click-to-set slider
        var pb = track?.GetComponent<PassiveButton>();
        if (!pb)
        {
            var col = track?.gameObject.AddComponent<BoxCollider2D>();
            if (col) col.size = new Vector2(SliderW, 0.40f);
            pb = track?.gameObject.AddComponent<PassiveButton>();
            if (pb)
            {
                pb.OnClick = new ButtonClickedEvent();
                pb.OnMouseOut = new UnityEvent();
                pb.OnMouseOver = new UnityEvent();
            }
        }
        if (pb && pb.OnClick.GetPersistentEventCount() == 0)
        {
            pb.OnClick.AddListener((Action)(() =>
            {
                var cam = Camera.main;
                if (!cam || !track) return;
                var mWorld = cam.ScreenToWorldPoint(Input.mousePosition);
                var mLocal = track.InverseTransformPoint(mWorld);
                float t = Mathf.InverseLerp(-SliderW * 0.5f, SliderW * 0.5f, mLocal.x);
                ApplyVolume(entry, Mathf.Lerp(VMin, VMax, t));
            }));
        }

        // Drag updater
        var upd = track?.GetComponent<PlayerSliderDragUpdater>();
        if (!upd) upd = track?.gameObject.AddComponent<PlayerSliderDragUpdater>();
        if (upd)
        {
            upd.Init(track!.gameObject, SliderW, VMin, VMax,
                v => ApplyVolume(entry, v),
                () => { foreach (var c in VoiceChatRoom.Current?.AllClients ?? Array.Empty<VCPlayer>()) if (c.PlayerName == entry.Name || c.PlayerId == entry.PlayerId) return c.Volume; return 1f; });
        }
    }

    private static void ApplyVolume(PlayerEntry entry, float v)
    {
        v = Mathf.Clamp(v, VMin, VMax);
        if (VoiceChatRoom.Current != null)
            foreach (var c in VoiceChatRoom.Current.AllClients)
                if (c.PlayerName == entry.Name || c.PlayerId == entry.PlayerId)
                { c.SetVolume(v); break; }
        Refresh(); // update all labels
    }

    // ── HUD lifecycle ────────────────────────────────────────────────────

    [HarmonyPatch(typeof(HudManager), nameof(HudManager.Start))]
    [HarmonyPostfix]
    static void HudStart(HudManager __instance)
    {
        if (_window) { Object.Destroy(_window); }
        _window = null;
        _btnPrefab = null;
        _scrollOffset = 0f;
    }

    // ── Helpers ──────────────────────────────────────────────────────────

    private record PlayerEntry(byte PlayerId, string Name, Color Color);

    private static List<PlayerEntry> CollectPlayers()
    {
        var list = new List<PlayerEntry>();
        if (!AmongUsClient.Instance) return list;

        var seen = new HashSet<byte>();
        if (VoiceChatRoom.Current != null)
            foreach (var c in VoiceChatRoom.Current.AllClients)
            {
                if (c.PlayerId == byte.MaxValue || !seen.Add(c.PlayerId)) continue;
                var pc = FindPlayer(c.PlayerId);
                list.Add(new PlayerEntry(c.PlayerId, c.PlayerName, GetPaletteColor(pc)));
            }

        foreach (var pc in PlayerControl.AllPlayerControls)
        {
            if (!pc || pc.Data == null || pc == PlayerControl.LocalPlayer) continue;
            if (!seen.Add(pc.PlayerId)) continue;
            list.Add(new PlayerEntry(pc.PlayerId, pc.Data.PlayerName, GetPaletteColor(pc)));
        }
        return list;
    }

    private static PlayerControl? FindPlayer(byte id)
    {
        foreach (var pc in PlayerControl.AllPlayerControls)
            if (pc && pc.PlayerId == id) return pc;
        return null;
    }

    private static Color GetPaletteColor(PlayerControl? pc)
    {
        if (pc?.Data == null) return new Color(0.18f, 0.80f, 0.44f, 1f);
        int cid = pc.Data.DefaultOutfit.ColorId;
        if (cid >= 0 && cid < Palette.PlayerColors.Length) return Palette.PlayerColors[cid];
        return Color.white;
    }

    private static ToggleButtonBehaviour? FindButtonPrefab()
    {
        var optMenu = Object.FindObjectOfType<OptionsMenuBehaviour>();
        if (!optMenu || !optMenu.CensorChatButton) return null;
        var prefab = Object.Instantiate(optMenu.CensorChatButton);
        Object.DontDestroyOnLoad(prefab);
        prefab.name = "VC_VolumeMenu_BtnPrefab";
        prefab.gameObject.SetActive(false);
        return prefab;
    }

    private static GameObject CreateSmallTextButton(string label, Vector3 pos, Action onClick)
    {
        var go = new GameObject($"Btn_{label}");
        go.transform.SetParent(_window!.transform, false);
        go.transform.localPosition = pos;

        var sr = go.AddComponent<SpriteRenderer>();
        sr.sprite = Create1x1Sprite(new Color32(52, 64, 98, 220));
        sr.drawMode = SpriteDrawMode.Sliced;
        sr.size = new Vector2(0.34f, 0.34f);
        sr.sortingOrder = 32767;

        var textGO = new GameObject("T");
        textGO.transform.SetParent(go.transform, false);
        textGO.transform.localPosition = new Vector3(0f, 0f, -0.1f);
        var tmp = textGO.AddComponent<TextMeshPro>();
        tmp.text = label; tmp.fontSize = 1.4f;
        tmp.alignment = TextAlignmentOptions.Center;
        tmp.sortingOrder = 32767; tmp.color = Color.white;
        tmp.rectTransform.sizeDelta = new Vector2(0.34f, 0.34f);

        var col = go.AddComponent<BoxCollider2D>();
        col.size = new Vector2(0.34f, 0.34f);
        var pb = go.AddComponent<PassiveButton>();
        pb.OnClick = new ButtonClickedEvent();
        pb.OnClick.AddListener((Action)(() => onClick()));
        pb.OnMouseOut = new UnityEvent();
        pb.OnMouseOver = new UnityEvent();
        pb.OnMouseOver.AddListener((Action)(() => sr.color = new Color32(75, 90, 130, 255)));
        pb.OnMouseOut.AddListener((Action)(() => sr.color = Color.white));
        return go;
    }

    private static Sprite? _circleSprite;
    private static Sprite GetCircleSprite()
    {
        if (_circleSprite) return _circleSprite;
        const int S = 64;
        var tex = new Texture2D(S, S, TextureFormat.RGBA32, false);
        float r = S * 0.5f;
        for (int y = 0; y < S; y++)
            for (int x = 0; x < S; x++)
            {
                float dx = x - r + 0.5f, dy = y - r + 0.5f;
                float a = Mathf.Clamp01((r - Mathf.Sqrt(dx * dx + dy * dy)) * 2f);
                tex.SetPixel(x, y, new Color(1, 1, 1, a));
            }
        tex.Apply();
        _circleSprite = Sprite.Create(tex, new Rect(0, 0, S, S), new Vector2(0.5f, 0.5f), S);
        return _circleSprite;
    }

    private static Sprite Create1x1Sprite(Color32 c)
    {
        var tex = new Texture2D(1, 1, TextureFormat.RGBA32, false);
        tex.SetPixel(0, 0, c); tex.Apply();
        return Sprite.Create(tex, new Rect(0, 0, 1, 1), new Vector2(0.5f, 0.5f));
    }
}

public class PlayerSliderDragUpdater : MonoBehaviour
{
    private float _min, _max, _trackW;
    private bool _dragging;
    private Action<float>? _onChange;
    private Func<float>? _getCurrent;
    private float _lastApplied;

    public void Init(GameObject track, float trackW, float min, float max,
        Action<float> onChange, Func<float> getCurrent)
    {
        _trackW = trackW; _min = min; _max = max;
        _onChange = onChange; _getCurrent = getCurrent;
    }

    void OnMouseDown() => _dragging = true;
    void OnMouseUp() => _dragging = false;

    void Update()
    {
        if (!_dragging) return;
        var cam = Camera.main;
        if (!cam) return;
        var mWorld = cam.ScreenToWorldPoint(Input.mousePosition);
        var mLocal = transform.InverseTransformPoint(mWorld);
        float t = Mathf.InverseLerp(-_trackW * 0.5f, _trackW * 0.5f, mLocal.x);
        float v = Mathf.Lerp(_min, _max, t);
        v = (float)Math.Round(v, 2);
        if (Math.Abs(v - _lastApplied) > 0.01f)
        {
            _lastApplied = v;
            _onChange?.Invoke(v);
        }
    }
}
