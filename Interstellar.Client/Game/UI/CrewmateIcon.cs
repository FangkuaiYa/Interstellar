using System;
using System.Collections.Generic;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Interstellar.UI;

/// <summary>
/// The reference volume-row identity icon (Perfect-Comms GetStableIdentityBodySpriteFor /
/// CreateBaseSprite): the embedded base crewmate template recolored to the player's
/// identity color with the palette shadow tone and the visor highlight, cached per color.
/// The sprite carries its own color — rows draw it white instead of tinting a flat
/// silhouette, which is what made the old live-body icon look wrong.
/// </summary>
internal static class CrewmateIcon
{
    private const float BasePixelsPerUnit = 32f;
    private const string TemplatePngBase64 = "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";
    private static readonly Dictionary<int, Sprite> Cache = new();
    private static Color32[]? _template;
    private static int _templateW, _templateH;

    // ── composed outfit avatar (identity body + the player's live cosmetics) ──

    private const string EmptyHatId = "hat_NoHat";
    private const string EmptySkinId = "skin_None";
    private const string EmptyVisorId = "visor_EmptyVisor";

    private const float AvatarRetrySeconds = 0.75f;

    private const int BodyOrder = 1;

    public readonly struct AvatarLayer
    {
        internal AvatarLayer(Sprite sprite, Rect local, bool flipX, bool flipY, Color32 tint, int order)
        {
            Sprite = sprite;
            Local = local;
            FlipX = flipX;
            FlipY = flipY;
            Tint = tint;
            Order = order;
        }

        internal readonly Sprite Sprite;
        internal readonly Rect Local;
        internal readonly bool FlipX;
        internal readonly bool FlipY;
        internal readonly Color32 Tint;
        internal readonly int Order;
    }

    private sealed class AvatarEntry
    {
        internal int ColorId;
        internal string Hat = "";
        internal string Skin = "";
        internal string Visor = "";
        internal bool Dead;
        internal List<AvatarLayer>? Layers;
        internal bool Resolved;
        internal float RetryAt;
    }

    private static readonly Dictionary<byte, AvatarEntry> Avatars = new();

    private sealed class AvatarSpec
    {
        internal Sprite Sprite = null!;
        internal SpriteRenderer Renderer = null!;
        internal Transform Leaf = null!;
        internal Transform? Root;
        internal Vector3 Anchor;
        internal int Order;
        internal Rect Local;
        internal bool FlipX;
        internal bool FlipY;
        internal Color32 Tint = new(255, 255, 255, 255);
    }

    public static Color ColorFor(PlayerControl? pc)
    {
        int id = ColorIdFor(pc);
        if (id < 0) return new Color(0.18f, 0.80f, 0.44f, 1f);
        return (Color)Palette.PlayerColors[Math.Clamp(id, 0, Palette.PlayerColors.Length - 1)];
    }

    public static Sprite? SpriteFor(PlayerControl? pc)
    {
        int id = ColorIdFor(pc);
        if (id < 0) return null;
        return BodySpriteFor(id);
    }

    private static Sprite? BodySpriteFor(int colorId)
    {
        colorId = Math.Clamp(colorId, 0, Palette.PlayerColors.Length - 1);
        if (Cache.TryGetValue(colorId, out var hit) && hit != null) return hit;
        var sprite = Build(colorId);
        if (sprite != null) Cache[colorId] = sprite;
        return sprite;
    }

    public static List<AvatarLayer>? AvatarLayersFor(PlayerControl? pc)
    {
        try
        {
            if (pc == null || pc.Data == null) return null;

            int colorId;
            string hat, skin, visor;
            try
            {
                var o = pc.CurrentOutfit ?? pc.Data.DefaultOutfit;
                colorId = o.ColorId;
                hat = o.HatId ?? "";
                skin = o.SkinId ?? "";
                visor = o.VisorId ?? "";
            }
            catch
            {
                var o = pc.Data.DefaultOutfit;
                colorId = o.ColorId;
                hat = o.HatId ?? "";
                skin = o.SkinId ?? "";
                visor = o.VisorId ?? "";
            }
            colorId = Math.Clamp(colorId, 0, Palette.PlayerColors.Length - 1);
            bool dead = false;
            try { dead = pc.Data.IsDead; } catch { }

            byte pid = pc.PlayerId;
            AvatarEntry? old = Avatars.TryGetValue(pid, out var hit) ? hit : null;
            bool same = old != null && old.ColorId == colorId && old.Hat == hat
                && old.Skin == skin && old.Visor == visor && old.Dead == dead;
            if (same && (old!.Resolved || Time.unscaledTime < old.RetryAt))
                return old.Layers;

            var layers = CollectAvatarLayers(pc, colorId, hat, skin, visor, out bool resolved);
            if (old != null && SameLayers(old.Layers, layers))
            {
                old.Resolved = resolved;
                old.RetryAt = Time.unscaledTime + AvatarRetrySeconds;
                return old.Layers;
            }
            Avatars[pid] = new AvatarEntry
            {
                ColorId = colorId, Hat = hat, Skin = skin, Visor = visor, Dead = dead,
                Layers = layers, Resolved = resolved,
                RetryAt = Time.unscaledTime + AvatarRetrySeconds
            };
            return layers;
        }
        catch
        {
            return null;
        }
    }

    private static bool SameLayers(List<AvatarLayer>? a, List<AvatarLayer>? b)
    {
        if (ReferenceEquals(a, b)) return true;
        if (a == null || b == null || a.Count != b.Count) return false;
        for (int i = 0; i < a.Count; i++)
        {
            var x = a[i];
            var y = b[i];
            if (!ReferenceEquals(x.Sprite, y.Sprite) || x.Local != y.Local
                || x.FlipX != y.FlipX || x.FlipY != y.FlipY || x.Order != y.Order)
                return false;
            if (x.Tint.r != y.Tint.r || x.Tint.g != y.Tint.g
                || x.Tint.b != y.Tint.b || x.Tint.a != y.Tint.a)
                return false;
        }
        return true;
    }

    private static int ColorIdFor(PlayerControl? pc)
    {
        if (pc?.Data == null) return -1;
        try { return pc.Data.DefaultOutfit.ColorId; }
        catch { return -1; }
    }

    private static List<AvatarLayer>? CollectAvatarLayers(PlayerControl pc, int colorId,
        string hatId, string skinId, string visorId, out bool resolved)
    {
        resolved = false;
        try
        {
            if (pc.Data != null && pc.Data.IsDead) { resolved = true; return null; }
            var c = pc.cosmetics;
            if (c == null) return null;

            SpriteRenderer? body = null;
            try
            {
                var cur = c.currentBodySprite;
                if (cur != null) body = cur.BodySprite;
                if (body == null)
                {
                    var normal = c.normalBodySprite;
                    if (normal != null) body = normal.BodySprite;
                }
            }
            catch { body = null; }
            if (body == null || body.sprite == null) return null;

            if (!EnsureTemplate()) return null;

            const float canonicalBodyScale = 0.68f;
            float bodyW = _templateW / BasePixelsPerUnit * canonicalBodyScale;
            float bodyH = _templateH / BasePixelsPerUnit * canonicalBodyScale;
            var hatVisorAnchor = new Vector3(-0.04f, 0.575f, 0f);
            try
            {
                var normalOffset = c.normalBodySprite;
                hatVisorAnchor += normalOffset != null
                    ? normalOffset.normalCosmeticOffset
                    : c.NormalCosmeticOffset;
            }
            catch { }
            var skinAnchor = Vector3.zero;

            bool pending = false;
            var specs = new List<AvatarSpec>(4);

            void Add(Sprite? art, SpriteRenderer? sr, Transform? leaf, Transform? root,
                Vector3 anchor, int order)
            {
                if (art == null || sr == null || leaf == null) return;
                try { if (!sr.enabled) return; } catch { return; }
                specs.Add(new AvatarSpec
                {
                    Sprite = art, Renderer = sr, Leaf = leaf, Root = root,
                    Anchor = anchor, Order = order
                });
            }

            try
            {
                bool declared = !string.IsNullOrEmpty(hatId) && hatId != EmptyHatId;
                if (declared)
                {
                    bool loaded = false;
                    try { loaded = c.hat != null && c.hat.IsLoaded; } catch { }
                    if (!loaded) pending = true;
                    else
                    {
                        Sprite? frontIdle = null, backIdle = null;
                        SpriteRenderer? front = null, back = null;
                        try
                        {
                            var v = c.hat != null ? c.hat.viewAsset.GetAsset() : null;
                            if (v != null) { frontIdle = v.MainImage; backIdle = v.BackImage; }
                        }
                        catch { }
                        try { front = c.hat != null ? c.hat.FrontLayer : null; } catch { }
                        try { back = c.hat != null ? c.hat.BackLayer : null; } catch { }
                        var hatRoot = c.hat != null ? c.hat.transform : null;
                        if (back != null)
                            try { Add(backIdle != null ? backIdle : back.sprite, back, back.transform, hatRoot, hatVisorAnchor, 0); } catch { }
                        if (front != null)
                            try { Add(frontIdle != null ? frontIdle : front.sprite, front, front.transform, hatRoot, hatVisorAnchor, 3); } catch { }
                    }
                }
            }
            catch { }

            try
            {
                bool declared = !string.IsNullOrEmpty(skinId) && skinId != EmptySkinId;
                if (declared)
                {
                    bool loaded = false;
                    try { loaded = c.skin != null && c.skin.IsLoaded; } catch { }
                    if (!loaded) pending = true;
                    else
                    {
                        SpriteRenderer? sr = null;
                        try { sr = c.skin != null ? c.skin.layer : null; } catch { }
                        if (sr != null)
                        {
                            Sprite? idle = null;
                            try
                            {
                                var v = c.skin != null ? c.skin.skin : null;
                                if (v != null) idle = v.IdleFrame;
                            }
                            catch { }
                            try { Add(idle != null ? idle : sr.sprite, sr, sr.transform, c.skin != null ? c.skin.transform : null, skinAnchor, 2); } catch { }
                        }
                    }
                }
            }
            catch { }

            try
            {
                bool declared = !string.IsNullOrEmpty(visorId) && visorId != EmptyVisorId;
                if (declared)
                {
                    bool loaded = false;
                    try { loaded = c.visor != null && c.visor.IsLoaded; } catch { }
                    if (!loaded) pending = true;
                    else
                    {
                        SpriteRenderer? sr = null;
                        try { sr = c.visor != null ? c.visor.Image : null; } catch { }
                        if (sr != null)
                        {
                            Sprite? idle = null;
                            try
                            {
                                var v = c.visor != null ? c.visor.viewAsset.GetAsset() : null;
                                if (v != null) idle = v.IdleFrame;
                            }
                            catch { }
                            try { Add(idle != null ? idle : sr.sprite, sr, sr.transform, c.visor != null ? c.visor.transform : null, hatVisorAnchor, 4); } catch { }
                        }
                    }
                }
            }
            catch { }

            // ── geometry, all in the canonical frame (player space, y-up) ──
            float minX = -bodyW * 0.5f, maxX = bodyW * 0.5f;
            float minY = -bodyH * 0.5f, maxY = bodyH * 0.5f;

            for (int i = 0; i < specs.Count; i++)
            {
                var sp = specs[i];
                var art = sp.Sprite;

                Vector3 pos;
                float accX, accY;
                try
                {
                    var root = sp.Root;
                    Vector3 scl = root != null ? root.localScale : Vector3.one;
                    scl.x = Mathf.Abs(scl.x);
                    pos = sp.Anchor;
                    var rot = Quaternion.identity;
                    if (root != null && root != sp.Leaf)
                    {
                        var nodes = new List<(Vector3 p, Quaternion r, Vector3 s)>(4);
                        Transform? t = sp.Leaf;
                        for (; t != null && t != root; t = t.parent)
                            nodes.Add((t.localPosition, t.localRotation, t.localScale));
                        if (t == root)
                        {
                            for (int n = nodes.Count - 1; n >= 0; n--)
                            {
                                var nd = nodes[n];
                                pos += rot * Vector3.Scale(nd.p, scl);
                                rot *= nd.r;
                                scl = Vector3.Scale(scl, nd.s);
                            }
                        }
                        // Leaf not under the root: degrade to anchor-only, as the
                        // reference does when the path cannot be captured.
                    }
                    accX = scl.x;
                    accY = scl.y;
                }
                catch { continue; }

                // Renderer flipX is dropped on purpose — the icon always faces
                // canonically right, the reference does the same. flipY and authored
                // negative chain scales still mirror the art.
                bool flipX = accX < 0f;
                bool flipY = accY < 0f;
                try { if (sp.Renderer.flipY) flipY = !flipY; } catch { }
                try { sp.Tint = sp.Renderer.color; } catch { }

                float ppu = art.pixelsPerUnit > 0f ? art.pixelsPerUnit : 100f;
                float w = art.rect.width / ppu * Mathf.Abs(accX);
                float h = art.rect.height / ppu * Mathf.Abs(accY);

                // Signed pivot offset: a negatively scaled chain mirrors the quad about
                // the leaf origin; renderer flip flags mirror content in place only.
                float offX = (art.rect.width * 0.5f - art.pivot.x) / ppu * accX;
                float offY = (art.rect.height * 0.5f - art.pivot.y) / ppu * accY;

                float cx = pos.x + offX;
                float cy = pos.y + offY;
                sp.Local = new Rect(cx - w * 0.5f, cy - h * 0.5f, w, h);
                sp.FlipX = flipX;
                sp.FlipY = flipY;

                if (sp.Local.xMin < minX) minX = sp.Local.xMin;
                if (sp.Local.xMax > maxX) maxX = sp.Local.xMax;
                if (sp.Local.yMin < minY) minY = sp.Local.yMin;
                if (sp.Local.yMax > maxY) maxY = sp.Local.yMax;
            }

            var identityBody = BodySpriteFor(colorId);
            if (identityBody == null) return null;
            var bodyRect = new Rect(-bodyW * 0.5f, -bodyH * 0.5f, bodyW, bodyH);

            specs.Sort((a, b) => a.Order.CompareTo(b.Order));
            var layers = new List<AvatarLayer>(specs.Count + 1);
            var bodyLayer = new AvatarLayer(identityBody, bodyRect, false, false,
                new Color32(255, 255, 255, 255), BodyOrder);
            bool bodyAdded = false;
            for (int i = 0; i < specs.Count; i++)
            {
                var sp = specs[i];
                if (!bodyAdded && sp.Order > BodyOrder)
                {
                    layers.Add(bodyLayer);
                    bodyAdded = true;
                }
                layers.Add(new AvatarLayer(sp.Sprite, sp.Local, sp.FlipX, sp.FlipY, sp.Tint, sp.Order));
            }
            if (!bodyAdded) layers.Add(bodyLayer);

            resolved = !pending;
            return layers;
        }
        catch
        {
            resolved = false;
            return null;
        }
    }

    private static Sprite? Build(int colorId)
    {
        var pixels = BuildPixels(colorId, out int w, out int h);
        if (pixels == null) return null;

        var tex = new Texture2D(w, h, TextureFormat.RGBA32, false)
        {
            filterMode = FilterMode.Bilinear,
            wrapMode = TextureWrapMode.Clamp,
            hideFlags = HideFlags.HideAndDontSave
        };
        tex.SetPixels32(pixels);
        tex.Apply(false, false);

        var sprite = Sprite.Create(tex, new Rect(0, 0, w, h),
            new Vector2(0.5f, 0.5f), BasePixelsPerUnit);
        sprite.hideFlags |= HideFlags.HideAndDontSave;
        return sprite;
    }

    private static Color32[]? BuildPixels(int colorId, out int w, out int h)
    {
        w = 0;
        h = 0;
        if (!EnsureTemplate()) return null;
        colorId = Math.Clamp(colorId, 0, Palette.PlayerColors.Length - 1);

        var main = (Color32)(Color)Palette.PlayerColors[colorId];
        var shadow = (Color32)(Color)Palette.ShadowColors[colorId];
        var highlight = new Color32(0x9a, 0xca, 0xd5, 0xff);

        var pixels = new Color32[_template!.Length];
        for (int i = 0; i < _template.Length; i++)
            pixels[i] = RecolorPixel(_template[i], main, shadow, highlight);

        w = _templateW;
        h = _templateH;
        return pixels;
    }

    private static bool EnsureTemplate()
    {
        if (_template != null) return true;
        try
        {
            var tex = new Texture2D(2, 2, TextureFormat.RGBA32, false) { wrapMode = TextureWrapMode.Clamp };
            if (!tex.LoadImage(Convert.FromBase64String(TemplatePngBase64), false)) return false;
            _templateW = tex.width;
            _templateH = tex.height;
            _template = tex.GetPixels32();
            Object.Destroy(tex);
            return _template.Length > 0;
        }
        catch
        {
            _template = null;
            return false;
        }
    }

    // ── pixel recolor (verbatim port of the reference renderer) ─────────────

    private static Color32 RecolorPixel(Color32 pixel, Color32 color, Color32 shadow, Color32 highlight)
    {
        if (pixel.a == 0) return pixel;

        var (hue, saturation) = RgbToHueSaturation(pixel.r, pixel.g, pixel.b);
        if (saturation <= 0.4f
            || (!IsHueNear(hue, 240f, 30f) && !IsHueNear(hue, 0f, 100f) && !IsHueNear(hue, 120f, 40f)))
            return pixel;

        var mixed = MixRgb(new Color32(0, 0, 0, 255), shadow, pixel.b / 255f);
        mixed = MixRgb(mixed, color, pixel.r / 255f);
        mixed = MixRgb(mixed, highlight, pixel.g / 255f);
        return new Color32(mixed.r, mixed.g, mixed.b, pixel.a);
    }

    private static (float Hue, float Saturation) RgbToHueSaturation(byte red, byte green, byte blue)
    {
        float r = red / 255f;
        float g = green / 255f;
        float b = blue / 255f;
        float max = Mathf.Max(r, Mathf.Max(g, b));
        float min = Mathf.Min(r, Mathf.Min(g, b));
        float delta = max - min;

        float hue = 0f;
        if (delta != 0f)
        {
            if (max == r) hue = 60f * PositiveModulo((g - b) / delta, 6f);
            else if (max == g) hue = 60f * (((b - r) / delta) + 2f);
            else hue = 60f * (((r - g) / delta) + 4f);
        }

        float saturation = max == 0f ? 0f : delta / max;
        return (hue, saturation);
    }

    private static bool IsHueNear(float value, float target, float maxDifference)
        => 180f - Mathf.Abs(Mathf.Abs(value - target) - 180f) < maxDifference;

    private static float PositiveModulo(float value, float modulo)
        => ((value % modulo) + modulo) % modulo;

    private static Color32 MixRgb(Color32 first, Color32 second, float amount)
    {
        amount = Mathf.Clamp01(amount);
        return new Color32(
            (byte)Mathf.RoundToInt(first.r * (1f - amount) + second.r * amount),
            (byte)Mathf.RoundToInt(first.g * (1f - amount) + second.g * amount),
            (byte)Mathf.RoundToInt(first.b * (1f - amount) + second.b * amount),
            255);
    }
}
