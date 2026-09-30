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

    /// <summary>Canvas scale: the identity body drawn this many pixels tall, cosmetics
    /// placed at their live player-local offsets. ~3× headroom over the 50 px row avatar.</summary>
    private const float AvatarBodyPx = 150f;
    private const int MaxAvatarPx = 384;
    private const float AvatarRetrySeconds = 0.75f;

    private sealed class AvatarEntry
    {
        internal int ColorId;
        internal string Hat = "";
        internal string Skin = "";
        internal string Visor = "";
        internal bool Dead;
        internal Sprite? Sprite;
        internal bool OwnsSprite;
        internal bool Resolved;
        internal float RetryAt;
    }

    private static readonly Dictionary<byte, AvatarEntry> Avatars = new();

    private sealed class AvatarSpec
    {
        internal Sprite Sprite = null!;
        internal SpriteRenderer Renderer = null!;
        internal Transform Leaf = null!;
        internal int Order;
        internal Rect Local;
        internal bool FlipX;
        internal bool FlipY;
        internal Color32 Tint = new(255, 255, 255, 255);
    }

    /// <summary>Stable identity glow color — the palette swatch with the same green
    /// fallback the rest of the voice UI uses.</summary>
    public static Color ColorFor(PlayerControl? pc)
    {
        int id = ColorIdFor(pc);
        if (id < 0) return new Color(0.18f, 0.80f, 0.44f, 1f);
        return (Color)Palette.PlayerColors[Math.Clamp(id, 0, Palette.PlayerColors.Length - 1)];
    }

    /// <summary>The identity crewmate for this player, or null when there is no live
    /// player (caller falls back to a plain rounded pill).</summary>
    public static Sprite? SpriteFor(PlayerControl? pc)
    {
        int id = ColorIdFor(pc);
        if (id < 0) return null;
        id = Math.Clamp(id, 0, Palette.PlayerColors.Length - 1);
        if (Cache.TryGetValue(id, out var hit) && hit != null) return hit;
        var sprite = Build(id);
        if (sprite != null) Cache[id] = sprite;
        return sprite;
    }

    /// <summary>The player's speaking-bar style avatar — the identity crewmate wearing
    /// their live hat/skin/visor (the reference speaker display, composed into one sprite
    /// for this UI). Falls back to the plain identity crewmate while cosmetics are still
    /// loading, when the player is dead (stable identity), or when there is no live body.
    /// Cheap to call every frame: a resolved entry is a dictionary hit.</summary>
    public static Sprite? AvatarFor(PlayerControl? pc)
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
                return old.Sprite ?? SpriteFor(pc);

            var sprite = ComposeAvatar(pc, colorId, hat, skin, visor, out bool resolved);
            bool owns = sprite != null;
            if (sprite == null) sprite = SpriteFor(pc);
            if (old != null && old.OwnsSprite) DestroySprite(old.Sprite);
            Avatars[pid] = new AvatarEntry
            {
                ColorId = colorId, Hat = hat, Skin = skin, Visor = visor, Dead = dead,
                Sprite = sprite, OwnsSprite = owns, Resolved = resolved,
                RetryAt = Time.unscaledTime + AvatarRetrySeconds
            };
            return sprite;
        }
        catch
        {
            return SpriteFor(pc);
        }
    }

    /// <summary>DefaultOutfit.ColorId — the stable identity, never a live disguise.</summary>
    private static int ColorIdFor(PlayerControl? pc)
    {
        if (pc?.Data == null) return -1;
        try { return pc.Data.DefaultOutfit.ColorId; }
        catch { return -1; }
    }

    /// <summary>Composes the identity crewmate with the player's live cosmetic layers at
    /// their exact player-local offsets (reference speaking-avatar geometry): hat back,
    /// body, skin, hat front, visor. Idle frames keep walk-animated cosmetics still.
    /// Null when there is no live body to anchor on (or the player is dead — then
    /// resolved=true, the stable identity is the final answer); resolved=false asks the
    /// caller to retry shortly, cosmetics or the body may still be loading.</summary>
    private static Sprite? ComposeAvatar(PlayerControl pc, int colorId,
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

            var pctr = pc.transform;
            float pScaleX = Mathf.Abs(pctr.lossyScale.x);
            float pScaleY = Mathf.Abs(pctr.lossyScale.y);
            if (pScaleX < 1e-5f || pScaleY < 1e-5f) return null;

            var bodySprite = body.sprite;
            float bodyPpu = bodySprite.pixelsPerUnit > 0f ? bodySprite.pixelsPerUnit : 100f;
            float bodyH = bodySprite.rect.height / bodyPpu * Mathf.Abs(body.transform.lossyScale.y) / pScaleY;
            if (bodyH < 1e-4f) return null;

            var template = BuildPixels(colorId, out int tw, out int th);
            if (template == null) return null;
            float bodyW = bodyH * ((float)tw / th);

            bool pending = false;
            var specs = new List<AvatarSpec>(4);

            void Add(Sprite? art, SpriteRenderer? sr, Transform? leaf, int order)
            {
                if (art == null || sr == null || leaf == null) return;
                try { if (!sr.enabled) return; } catch { return; }
                specs.Add(new AvatarSpec { Sprite = art, Renderer = sr, Leaf = leaf, Order = order });
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
                        if (back != null)
                            try { Add(backIdle != null ? backIdle : back.sprite, back, back.transform, 0); } catch { }
                        if (front != null)
                            try { Add(frontIdle != null ? frontIdle : front.sprite, front, front.transform, 3); } catch { }
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
                            try { Add(idle != null ? idle : sr.sprite, sr, sr.transform, 2); } catch { }
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
                            try { Add(idle != null ? idle : sr.sprite, sr, sr.transform, 4); } catch { }
                        }
                    }
                }
            }
            catch { }

            // ── geometry, all in player-local units (y-up, canonical facing) ──
            Vector3 bodyLocal = pctr.InverseTransformPoint(body.transform.position);
            float bodyLeft = bodyLocal.x - bodyW * 0.5f;
            float bodyBottom = bodyLocal.y - bodyH * 0.5f;

            float minX = bodyLeft, maxX = bodyLeft + bodyW;
            float minY = bodyBottom, maxY = bodyBottom + bodyH;
            bool pcFlipX = pctr.lossyScale.x < 0f;
            bool pcFlipY = pctr.lossyScale.y < 0f;

            for (int i = 0; i < specs.Count; i++)
            {
                var sp = specs[i];
                var art = sp.Sprite;
                Vector3 center;
                try { center = pctr.InverseTransformPoint(sp.Leaf.position); }
                catch { center = bodyLocal; }
                float lSX = Mathf.Abs(sp.Leaf.lossyScale.x) / pScaleX;
                float lSY = Mathf.Abs(sp.Leaf.lossyScale.y) / pScaleY;
                float ppu = art.pixelsPerUnit > 0f ? art.pixelsPerUnit : 100f;
                float w = art.rect.width / ppu * lSX;
                float h = art.rect.height / ppu * lSY;

                // InverseTransformPoint already canonicalized the player's facing — a layer
                // only needs mirroring when IT (or its flip flag) is authored that way.
                bool flipX = (sp.Leaf.lossyScale.x < 0f) != pcFlipX;
                try { if (sp.Renderer.flipX) flipX = !flipX; } catch { }
                bool flipY = (sp.Leaf.lossyScale.y < 0f) != pcFlipY;
                try { if (sp.Renderer.flipY) flipY = !flipY; } catch { }
                try { sp.Tint = sp.Renderer.color; } catch { }

                float offX = (art.rect.width * 0.5f - art.pivot.x) / ppu * lSX;
                float offY = (art.rect.height * 0.5f - art.pivot.y) / ppu * lSY;
                if (flipX) offX = -offX;
                if (flipY) offY = -offY;

                float cx = center.x + offX;
                float cy = center.y + offY;
                sp.Local = new Rect(cx - w * 0.5f, cy - h * 0.5f, w, h);
                sp.FlipX = flipX;
                sp.FlipY = flipY;

                if (sp.Local.xMin < minX) minX = sp.Local.xMin;
                if (sp.Local.xMax > maxX) maxX = sp.Local.xMax;
                if (sp.Local.yMin < minY) minY = sp.Local.yMin;
                if (sp.Local.yMax > maxY) maxY = sp.Local.yMax;
            }

            // ── canvas ──
            float spanX = Mathf.Max(maxX - minX, 1e-4f);
            float spanY = Mathf.Max(maxY - minY, 1e-4f);
            float ppuPx = AvatarBodyPx / bodyH;
            float maxPpu = MaxAvatarPx / Mathf.Max(spanX, spanY);
            if (ppuPx > maxPpu) ppuPx = maxPpu;

            int cw = Mathf.CeilToInt(spanX * ppuPx) + 4;
            int ch = Mathf.CeilToInt(spanY * ppuPx) + 4;
            float ox = minX - 2f / ppuPx;
            float oy = minY - 2f / ppuPx;

            var canvas = new Color32[cw * ch];

            // Identity body, then cosmetics back-to-front.
            int bx = Mathf.RoundToInt((bodyLeft - ox) * ppuPx);
            int by = Mathf.RoundToInt((bodyBottom - oy) * ppuPx);
            int bw = Mathf.Max(1, Mathf.RoundToInt(bodyW * ppuPx));
            int bh = Mathf.Max(1, Mathf.RoundToInt(bodyH * ppuPx));
            BlendNearest(canvas, cw, ch, bx, by, template, tw, th, bw, bh,
                false, false, new Color32(255, 255, 255, 255));

            specs.Sort((a, b) => a.Order.CompareTo(b.Order));
            for (int i = 0; i < specs.Count; i++)
            {
                var sp = specs[i];
                int lx = Mathf.RoundToInt((sp.Local.x - ox) * ppuPx);
                int ly = Mathf.RoundToInt((sp.Local.y - oy) * ppuPx);
                int lw = Mathf.Max(1, Mathf.RoundToInt((sp.Local.xMax - ox) * ppuPx) - lx);
                int lh = Mathf.Max(1, Mathf.RoundToInt((sp.Local.yMax - oy) * ppuPx) - ly);
                var pixels = ReadSpritePixels(sp.Sprite, lw, lh);
                if (pixels == null) continue;
                BlendNearest(canvas, cw, ch, lx, ly, pixels, lw, lh, lw, lh,
                    sp.FlipX, sp.FlipY, sp.Tint);
            }

            var tex = new Texture2D(cw, ch, TextureFormat.RGBA32, true)
            {
                filterMode = FilterMode.Bilinear,
                wrapMode = TextureWrapMode.Clamp,
                hideFlags = HideFlags.HideAndDontSave
            };
            tex.SetPixels32(canvas);
            tex.Apply(false, true);
            var sprite = Sprite.Create(tex, new Rect(0, 0, cw, ch), new Vector2(0.5f, 0.5f), 100f);
            sprite.hideFlags |= HideFlags.HideAndDontSave;
            resolved = !pending;
            return sprite;
        }
        catch
        {
            resolved = false;
            return null;
        }
    }

    /// <summary>GPU-resamples a sprite's texture region into a plain pixel buffer — game
    /// sprites are atlas-packed without CPU read access, but a RenderTexture blit can
    /// crop and scale them.</summary>
    private static Color32[]? ReadSpritePixels(Sprite sprite, int w, int h)
    {
        RenderTexture? rt = null;
        Texture2D? tmp = null;
        RenderTexture? prev = null;
        try
        {
            var tex = sprite.texture;
            if (tex == null || w < 1 || h < 1) return null;
            var r = sprite.rect;
            Vector2 scale = new(r.width / tex.width, r.height / tex.height);
            Vector2 offset = new(r.x / tex.width, r.y / tex.height);
            rt = RenderTexture.GetTemporary(w, h, 0, RenderTextureFormat.ARGB32);
            Graphics.Blit(tex, rt, scale, offset);
            prev = RenderTexture.active;
            RenderTexture.active = rt;
            tmp = new Texture2D(w, h, TextureFormat.RGBA32, false) { hideFlags = HideFlags.HideAndDontSave };
            tmp.ReadPixels(new Rect(0, 0, w, h), 0, 0);
            tmp.Apply(false, false);
            return tmp.GetPixels32();
        }
        catch
        {
            return null;
        }
        finally
        {
            RenderTexture.active = prev;
            if (rt != null) RenderTexture.ReleaseTemporary(rt);
            if (tmp != null) Object.Destroy(tmp);
        }
    }

    /// <summary>Alpha-blends a source buffer into the canvas at a pixel rect. The buffer
    /// either matches the rect exactly (cosmetics) or is point-sampled into it (template).
    /// Flips mirror within the rect, which — combined with the pivot-negated rect offset —
    /// mirrors the layer about its pivot, exactly like the world-space original.</summary>
    private static void BlendNearest(Color32[] dst, int dstW, int dstH, int ox, int oy,
        Color32[] src, int srcW, int srcH, int bufW, int bufH, bool flipX, bool flipY, Color32 tint)
    {
        for (int y = 0; y < bufH; y++)
        {
            int dy = oy + y;
            if (dy < 0 || dy >= dstH) continue;
            int sy = y * srcH / bufH;
            if (sy >= srcH) sy = srcH - 1;
            if (flipY) sy = srcH - 1 - sy;
            for (int x = 0; x < bufW; x++)
            {
                int dx = ox + x;
                if (dx < 0 || dx >= dstW) continue;
                int sx = x * srcW / bufW;
                if (sx >= srcW) sx = srcW - 1;
                if (flipX) sx = srcW - 1 - sx;
                var s = src[sy * srcW + sx];
                if (s.a == 0) continue;
                int tr = s.r * tint.r / 255;
                int tg = s.g * tint.g / 255;
                int tb = s.b * tint.b / 255;
                int ta = s.a * tint.a / 255;
                if (ta == 0) continue;
                int di = dy * dstW + dx;
                var d = dst[di];
                float af = ta * (1f / 255f);
                float ia = 1f - af;
                dst[di] = new Color32(
                    (byte)(tr * af + d.r * ia + 0.5f),
                    (byte)(tg * af + d.g * ia + 0.5f),
                    (byte)(tb * af + d.b * ia + 0.5f),
                    (byte)(ta + d.a * ia + 0.5f));
            }
        }
    }

    private static void DestroySprite(Sprite? sprite)
    {
        if (sprite == null) return;
        try
        {
            var tex = sprite.texture;
            Object.Destroy(sprite);
            if (tex != null) Object.Destroy(tex);
        }
        catch { }
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

    /// <summary>The recolored template pixels — shared by the plain identity sprite and
    /// the composed avatar's body layer.</summary>
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
