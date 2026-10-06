// IconFactory.cs — procedurally rendered, anti-aliased white icon sprites for the spatial UI.
// Drawn at 4x supersampling into an alpha mask, then downsampled — crisp at headset resolution
// without shipping binary art. Icons are monochrome (tinted by Image.color).

using System.Collections.Generic;
using UnityEngine;

public static class IconFactory
{
    private const int OUT = 96;              // output sprite size
    private const int SS  = 4;               // supersample factor
    private const int RES = OUT * SS;        // raster resolution

    private static readonly Dictionary<string, Sprite> Cache = new();

    // ─── Public icons ─────────────────────────────────────────────────────────

    public static Sprite BallStick()  => Get("ballstick", m =>
    {
        Line(m, V(0.28f, 0.62f), V(0.72f, 0.38f), 0.045f);
        DiskOutlined(m, V(0.28f, 0.62f), 0.17f);
        DiskOutlined(m, V(0.72f, 0.38f), 0.13f);
    });

    public static Sprite SpaceFill()  => Get("spacefill", m =>
    {
        Disk(m, V(0.38f, 0.55f), 0.26f);
        Disk(m, V(0.66f, 0.42f), 0.21f);
        Disk(m, V(0.58f, 0.68f), 0.16f);
    });

    public static Sprite Stick()      => Get("stick", m =>
    {
        Line(m, V(0.22f, 0.70f), V(0.50f, 0.50f), 0.05f);
        Line(m, V(0.50f, 0.50f), V(0.78f, 0.62f), 0.05f);
        Line(m, V(0.50f, 0.50f), V(0.58f, 0.24f), 0.05f);
    });

    public static Sprite Wireframe()  => Get("wireframe", m =>
    {
        // hexagon outline with node dots
        var pts = new Vector2[6];
        for (int i = 0; i < 6; i++)
        {
            float a = Mathf.PI / 3f * i + Mathf.PI / 6f;
            pts[i] = V(0.5f + 0.30f * Mathf.Cos(a), 0.5f + 0.30f * Mathf.Sin(a));
        }
        for (int i = 0; i < 6; i++) Line(m, pts[i], pts[(i + 1) % 6], 0.022f);
        foreach (var p in pts) Disk(m, p, 0.045f);
    });

    public static Sprite Polyhedral() => Get("polyhedral", m =>
    {
        // tetrahedron: front face solid-ish edges + apex
        Vector2 a = V(0.5f, 0.20f), b = V(0.20f, 0.74f), c = V(0.80f, 0.74f), d = V(0.60f, 0.52f);
        Line(m, a, b, 0.03f); Line(m, b, c, 0.03f); Line(m, c, a, 0.03f);
        Line(m, a, d, 0.022f); Line(m, b, d, 0.022f); Line(m, c, d, 0.022f);
    });

    public static Sprite Measure()    => Get("measure", m =>
    {
        Line(m, V(0.20f, 0.76f), V(0.80f, 0.28f), 0.035f);
        // caliper end ticks
        Line(m, V(0.15f, 0.64f), V(0.28f, 0.85f), 0.035f);
        Line(m, V(0.72f, 0.17f), V(0.86f, 0.38f), 0.035f);
        Disk(m, V(0.20f, 0.76f), 0.05f);
        Disk(m, V(0.80f, 0.28f), 0.05f);
    });

    public static Sprite Bonds()      => Get("bonds", m =>
    {
        Disk(m, V(0.22f, 0.50f), 0.11f);
        Disk(m, V(0.78f, 0.50f), 0.11f);
        Line(m, V(0.33f, 0.50f), V(0.46f, 0.50f), 0.04f);
        Line(m, V(0.54f, 0.50f), V(0.67f, 0.50f), 0.04f);
    });

    public static Sprite Planes()     => Get("planes", m =>
    {
        // slanted parallelogram plane inside cube hint
        Quad(m, V(0.22f, 0.62f), V(0.55f, 0.78f), V(0.80f, 0.42f), V(0.47f, 0.26f));
        Line(m, V(0.16f, 0.20f), V(0.16f, 0.80f), 0.02f);
        Line(m, V(0.16f, 0.80f), V(0.86f, 0.80f), 0.02f);
    });

    public static Sprite Pressure()   => Get("pressure", m =>
    {
        // inward arrows onto a core
        Disk(m, V(0.5f, 0.5f), 0.10f);
        Arrow(m, V(0.5f, 0.12f), V(0.5f, 0.34f));
        Arrow(m, V(0.5f, 0.88f), V(0.5f, 0.66f));
        Arrow(m, V(0.12f, 0.5f), V(0.34f, 0.5f));
        Arrow(m, V(0.88f, 0.5f), V(0.66f, 0.5f));
    });

    public static Sprite Library()    => Get("library", m =>
    {
        // three tilted book spines
        RectRot(m, V(0.28f, 0.52f), 0.10f, 0.46f, 0f);
        RectRot(m, V(0.46f, 0.52f), 0.10f, 0.46f, 0f);
        RectRot(m, V(0.68f, 0.54f), 0.10f, 0.46f, 12f);
    });

    public static Sprite CellBox()    => Get("cellbox", m =>
    {
        // isometric cube outline
        Vector2 A = V(0.30f, 0.30f), B = V(0.66f, 0.24f), C = V(0.78f, 0.42f), D = V(0.42f, 0.48f);
        Vector2 A2 = V(0.30f, 0.62f), B2 = V(0.66f, 0.56f), C2 = V(0.78f, 0.74f), D2 = V(0.42f, 0.80f);
        Line(m, A, B, 0.022f); Line(m, B, C, 0.022f); Line(m, C, D, 0.022f); Line(m, D, A, 0.022f);
        Line(m, A2, B2, 0.022f); Line(m, B2, C2, 0.022f); Line(m, C2, D2, 0.022f); Line(m, D2, A2, 0.022f);
        Line(m, A, A2, 0.022f); Line(m, B, B2, 0.022f); Line(m, C, C2, 0.022f); Line(m, D, D2, 0.022f);
    });

    public static Sprite ChevronLeft()  => Get("chev_left", m =>
    {
        Line(m, V(0.60f, 0.22f), V(0.36f, 0.50f), 0.055f);
        Line(m, V(0.36f, 0.50f), V(0.60f, 0.78f), 0.055f);
    });

    public static Sprite ChevronRight() => Get("chev_right", m =>
    {
        Line(m, V(0.40f, 0.22f), V(0.64f, 0.50f), 0.055f);
        Line(m, V(0.64f, 0.50f), V(0.40f, 0.78f), 0.055f);
    });

    public static Sprite Close()      => Get("close", m =>
    {
        Line(m, V(0.28f, 0.28f), V(0.72f, 0.72f), 0.05f);
        Line(m, V(0.72f, 0.28f), V(0.28f, 0.72f), 0.05f);
    });

    public static Sprite Power()      => Get("power", m =>
    {
        Ring(m, V(0.5f, 0.54f), 0.26f, 0.035f, 130f, 410f);
        Line(m, V(0.5f, 0.20f), V(0.5f, 0.50f), 0.045f);
    });

    // ─── Rasteriser ───────────────────────────────────────────────────────────

    private static Vector2 V(float x, float y) => new Vector2(x, y);

    private static Sprite Get(string key, System.Action<float[]> draw)
    {
        if (Cache.TryGetValue(key, out var s) && s != null) return s;
        var mask = new float[RES * RES];
        draw(mask);
        var sprite = ToSprite(mask);
        Cache[key] = sprite;
        return sprite;
    }

    private static void Plot(float[] m, int x, int y, float a)
    {
        if (x < 0 || y < 0 || x >= RES || y >= RES) return;
        int i = y * RES + x;
        if (a > m[i]) m[i] = a;
    }

    private static void Disk(float[] m, Vector2 c, float r)
    {
        float cr = c.x * RES, cy = c.y * RES, rr = r * RES;
        int x0 = Mathf.Max(0, (int)(cr - rr) - 2), x1 = Mathf.Min(RES - 1, (int)(cr + rr) + 2);
        int y0 = Mathf.Max(0, (int)(cy - rr) - 2), y1 = Mathf.Min(RES - 1, (int)(cy + rr) + 2);
        for (int y = y0; y <= y1; y++)
        for (int x = x0; x <= x1; x++)
        {
            float d = Mathf.Sqrt((x - cr) * (x - cr) + (y - cy) * (y - cy));
            Plot(m, x, y, Mathf.Clamp01(rr - d + 0.5f));
        }
    }

    // Disk with a subtle inner cut so overlapping balls read as separate spheres.
    private static void DiskOutlined(float[] m, Vector2 c, float r)
    {
        Ring(m, c, r, 0.025f, 0f, 360f);
        Disk(m, c, r * 0.62f);
    }

    private static void Ring(float[] m, Vector2 c, float r, float w, float degFrom, float degTo)
    {
        float cr = c.x * RES, cy = c.y * RES, rr = r * RES, ww = w * RES;
        int pad = (int)(rr + ww) + 2;
        int x0 = Mathf.Max(0, (int)cr - pad), x1 = Mathf.Min(RES - 1, (int)cr + pad);
        int y0 = Mathf.Max(0, (int)cy - pad), y1 = Mathf.Min(RES - 1, (int)cy + pad);
        for (int y = y0; y <= y1; y++)
        for (int x = x0; x <= x1; x++)
        {
            float dx = x - cr, dy = y - cy;
            float d = Mathf.Abs(Mathf.Sqrt(dx * dx + dy * dy) - rr);
            float ang = Mathf.Atan2(dy, dx) * Mathf.Rad2Deg; if (ang < 0) ang += 360f;
            bool inArc = degTo <= 360f ? (ang >= degFrom && ang <= degTo)
                                       : (ang >= degFrom || ang <= degTo - 360f);
            if (!inArc) continue;
            Plot(m, x, y, Mathf.Clamp01(ww - d + 0.5f));
        }
    }

    private static void Line(float[] m, Vector2 a, Vector2 b, float w)
    {
        Vector2 A = a * RES, B = b * RES;
        float ww = w * RES;
        Vector2 ab = B - A; float len2 = ab.sqrMagnitude;
        int x0 = Mathf.Max(0, (int)Mathf.Min(A.x, B.x) - (int)ww - 2);
        int x1 = Mathf.Min(RES - 1, (int)Mathf.Max(A.x, B.x) + (int)ww + 2);
        int y0 = Mathf.Max(0, (int)Mathf.Min(A.y, B.y) - (int)ww - 2);
        int y1 = Mathf.Min(RES - 1, (int)Mathf.Max(A.y, B.y) + (int)ww + 2);
        for (int y = y0; y <= y1; y++)
        for (int x = x0; x <= x1; x++)
        {
            var p = new Vector2(x, y);
            float t = len2 > 1e-5f ? Mathf.Clamp01(Vector2.Dot(p - A, ab) / len2) : 0f;
            float d = (p - (A + ab * t)).magnitude;
            Plot(m, x, y, Mathf.Clamp01(ww - d + 0.5f));
        }
    }

    private static void Arrow(float[] m, Vector2 from, Vector2 to)
    {
        Line(m, from, to, 0.028f);
        Vector2 dir = (to - from).normalized;
        Vector2 perp = new Vector2(-dir.y, dir.x);
        Line(m, to, to - dir * 0.09f + perp * 0.06f, 0.028f);
        Line(m, to, to - dir * 0.09f - perp * 0.06f, 0.028f);
    }

    private static void Quad(float[] m, Vector2 a, Vector2 b, Vector2 c, Vector2 d)
    {
        // filled quad via two triangles (scanline-free: barycentric point tests over bbox)
        FillTri(m, a, b, c); FillTri(m, a, c, d);
    }

    private static void FillTri(float[] m, Vector2 a, Vector2 b, Vector2 c)
    {
        Vector2 A = a * RES, B = b * RES, C = c * RES;
        int x0 = Mathf.Max(0, (int)Mathf.Min(A.x, Mathf.Min(B.x, C.x)) - 1);
        int x1 = Mathf.Min(RES - 1, (int)Mathf.Max(A.x, Mathf.Max(B.x, C.x)) + 1);
        int y0 = Mathf.Max(0, (int)Mathf.Min(A.y, Mathf.Min(B.y, C.y)) - 1);
        int y1 = Mathf.Min(RES - 1, (int)Mathf.Max(A.y, Mathf.Max(B.y, C.y)) + 1);
        float Edge(Vector2 p, Vector2 q, Vector2 r) => (q.x - p.x) * (r.y - p.y) - (q.y - p.y) * (r.x - p.x);
        float area = Edge(A, B, C); if (Mathf.Abs(area) < 1e-4f) return;
        for (int y = y0; y <= y1; y++)
        for (int x = x0; x <= x1; x++)
        {
            var p = new Vector2(x, y);
            float w0 = Edge(A, B, p) / area, w1 = Edge(B, C, p) / area, w2 = Edge(C, A, p) / area;
            if (w0 >= 0 && w1 >= 0 && w2 >= 0) Plot(m, x, y, 0.55f); // translucent fill
        }
    }

    private static void RectRot(float[] m, Vector2 c, float w, float h, float deg)
    {
        float rad = deg * Mathf.Deg2Rad;
        Vector2 ax = new Vector2(Mathf.Cos(rad), Mathf.Sin(rad)) * (w * 0.5f);
        Vector2 ay = new Vector2(-Mathf.Sin(rad), Mathf.Cos(rad)) * (h * 0.5f);
        Quad(m, c - ax - ay, c + ax - ay, c + ax + ay, c - ax + ay);
        // full-strength outline
        Line(m, c - ax - ay, c + ax - ay, 0.02f); Line(m, c + ax - ay, c + ax + ay, 0.02f);
        Line(m, c + ax + ay, c - ax + ay, 0.02f); Line(m, c - ax + ay, c - ax - ay, 0.02f);
    }

    private static Sprite ToSprite(float[] mask)
    {
        var tex = new Texture2D(OUT, OUT, TextureFormat.RGBA32, false)
        { wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Bilinear };
        var px = new Color[OUT * OUT];
        for (int y = 0; y < OUT; y++)
        for (int x = 0; x < OUT; x++)
        {
            float sum = 0f;
            for (int sy = 0; sy < SS; sy++)
            for (int sx = 0; sx < SS; sx++)
                sum += mask[(y * SS + sy) * RES + (x * SS + sx)];
            // sprites drawn y-up; texture rows y-up already consistent
            px[y * OUT + x] = new Color(1, 1, 1, sum / (SS * SS));
        }
        tex.SetPixels(px); tex.Apply();
        return Sprite.Create(tex, new Rect(0, 0, OUT, OUT), new Vector2(0.5f, 0.5f), 100f);
    }
}
