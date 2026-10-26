using System;
using UnityEngine;

namespace GlurpOnline.UI
{
    internal static partial class Glyphs
    {
        internal sealed class GlyphDef
        {
            public string Name;
            public Func<Vector2, float> Shape;
            public float Lean;
            public float Fit;
        }

        private const float MedalLean = 0.17f;

        internal static readonly GlyphDef OnlineDef = new GlyphDef { Name = "online", Shape = Signal, Lean = 0f, Fit = 0.96f };
        internal static readonly GlyphDef HostDef = new GlyphDef { Name = "host", Shape = Crown, Lean = MedalLean, Fit = 0.86f };
        internal static readonly GlyphDef FindDef = new GlyphDef { Name = "find", Shape = Magnifier, Lean = MedalLean, Fit = 0.86f };
        internal static readonly GlyphDef DirectDef = new GlyphDef { Name = "direct", Shape = Plug, Lean = MedalLean, Fit = 0.86f };
        internal static readonly GlyphDef[] All = { OnlineDef, HostDef, FindDef, DirectDef };

        internal static byte[] Rasterize(GlyphDef def, int size)
        {
            var alpha = new byte[size * size];
            float aa = 1.25f / size;
            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    float px = (x + 0.5f) / size, py = (y + 0.5f) / size;
                    var q = new Vector2((px - 0.5f - def.Lean * (py - 0.5f)) / def.Fit + 0.5f, (py - 0.5f) / def.Fit + 0.5f);
                    float d = def.Shape(q) * def.Fit;
                    alpha[y * size + x] = (byte)Mathf.RoundToInt(Mathf.Clamp01(0.5f - d / aa) * 255f);
                }
            }
            return alpha;
        }

        private static float Signal(Vector2 p)
        {
            const float half = 0.08f, dot = 0.115f, gap = 0.125f;
            const float r1 = dot + gap + half;
            const float r2 = r1 + half + gap + half;
            var c = new Vector2(0.5f, 0.5f - (r2 + half - dot) / 2f);
            float d = Circle(p, c, dot);
            d = Mathf.Min(d, Arc(p, c, r1, half, 90f, 42f));
            d = Mathf.Min(d, Arc(p, c, r2, half, 90f, 42f));
            return d;
        }

        private static float Crown(Vector2 p)
        {
            const float gap = 0.045f;
            float band = RoundedBox(p, new Vector2(0.5f, 0.19f), new Vector2(0.38f, 0.085f), 0.05f);
            var outline = new[]
            {
                new Vector2(0.17f, 0.33f), new Vector2(0.83f, 0.33f), new Vector2(0.87f, 0.7f), new Vector2(0.68f, 0.56f),
                new Vector2(0.5f, 0.77f), new Vector2(0.32f, 0.56f), new Vector2(0.13f, 0.7f),
            };
            float body = Polygon(p, outline) - 0.04f;
            body = Mathf.Max(body, -(band - gap));
            body = Mathf.Max(body, -(Star(p - new Vector2(0.5f, 0.5f), 0.125f, 0.48f) - 0.008f));
            float tips = Mathf.Min(Circle(p, new Vector2(0.12f, 0.79f), 0.08f),
                Mathf.Min(Circle(p, new Vector2(0.5f, 0.87f), 0.085f), Circle(p, new Vector2(0.88f, 0.79f), 0.08f)));
            return Mathf.Min(Mathf.Min(band, body), tips);
        }

        private static float Magnifier(Vector2 p)
        {
            var c = new Vector2(0.41f, 0.59f);
            const float outer = 0.325f, inner = 0.19f, gap = 0.045f;
            float lens = Mathf.Max(Circle(p, c, outer), -Circle(p, c, inner));
            float glint = Arc(p, c, 0.115f, 0.037f, 135f, 38f);
            float handle = Segment(p, new Vector2(0.68f, 0.32f), new Vector2(0.86f, 0.14f), 0.105f);
            handle = Mathf.Max(handle, -(Circle(p, c, outer) - gap));
            return Mathf.Min(Mathf.Min(lens, glint), handle);
        }

        private static float Plug(Vector2 p)
        {
            const float gap = 0.045f;
            float head = RoundedBox(p, new Vector2(0.5f, 0.5f), new Vector2(0.32f, 0.2f), 0.12f);
            float prongs = Mathf.Min(
                RoundedBox(p, new Vector2(0.365f, 0.83f), new Vector2(0.07f, 0.15f), 0.06f),
                RoundedBox(p, new Vector2(0.635f, 0.83f), new Vector2(0.07f, 0.15f), 0.06f));
            float grips = Mathf.Min(
                Segment(p, new Vector2(0.36f, 0.545f), new Vector2(0.64f, 0.545f), 0.034f),
                Segment(p, new Vector2(0.36f, 0.445f), new Vector2(0.64f, 0.445f), 0.034f));
            head = Mathf.Max(head, -grips);
            float cable = Mathf.Min(
                Segment(p, new Vector2(0.5f, 0.3f), new Vector2(0.5f, 0.2f), 0.075f),
                Arc(p, new Vector2(0.67f, 0.2f), 0.17f, 0.075f, 225f, 45f));
            cable = Mathf.Max(cable, -(head - gap));
            return Mathf.Min(Mathf.Min(head, prongs), cable);
        }

        private static float Circle(Vector2 p, Vector2 c, float r) => (p - c).magnitude - r;

        private static float RoundedBox(Vector2 p, Vector2 c, Vector2 half, float r)
        {
            var q = new Vector2(Mathf.Abs(p.x - c.x) - half.x + r, Mathf.Abs(p.y - c.y) - half.y + r);
            return new Vector2(Mathf.Max(q.x, 0f), Mathf.Max(q.y, 0f)).magnitude + Mathf.Min(Mathf.Max(q.x, q.y), 0f) - r;
        }

        private static float Star(Vector2 p, float r, float innerRatio)
        {
            var k1 = new Vector2(0.809016994f, -0.587785252f);
            var k2 = new Vector2(-k1.x, k1.y);
            p.x = Mathf.Abs(p.x);
            p = p - k1 * (2f * Mathf.Max(Vector2.Dot(k1, p), 0f));
            p = p - k2 * (2f * Mathf.Max(Vector2.Dot(k2, p), 0f));
            p.x = Mathf.Abs(p.x);
            p.y -= r;
            var ba = new Vector2(-k1.y, k1.x) * innerRatio - new Vector2(0f, 1f);
            float h = Mathf.Max(0f, Mathf.Min(r, Vector2.Dot(p, ba) / Vector2.Dot(ba, ba)));
            float sign = p.y * ba.x - p.x * ba.y;
            return (p - ba * h).magnitude * (sign < 0f ? -1f : 1f);
        }

        private static float SegmentDistance(Vector2 p, Vector2 a, Vector2 b)
        {
            var pa = p - a;
            var ba = b - a;
            float h = Mathf.Clamp01(Vector2.Dot(pa, ba) / Vector2.Dot(ba, ba));
            return (pa - ba * h).magnitude;
        }

        private static float Segment(Vector2 p, Vector2 a, Vector2 b, float radius) => SegmentDistance(p, a, b) - radius;

        private static float Arc(Vector2 p, Vector2 c, float radius, float halfWidth, float centerDeg, float halfDeg)
        {
            var v = p - c;
            float angle = Mathf.Atan2(v.y, v.x) * Mathf.Rad2Deg;
            if (Mathf.Abs(Mathf.DeltaAngle(angle, centerDeg)) <= halfDeg) return Mathf.Abs(v.magnitude - radius) - halfWidth;
            float a0 = (centerDeg - halfDeg) * Mathf.Deg2Rad, a1 = (centerDeg + halfDeg) * Mathf.Deg2Rad;
            var e0 = c + new Vector2(Mathf.Cos(a0), Mathf.Sin(a0)) * radius;
            var e1 = c + new Vector2(Mathf.Cos(a1), Mathf.Sin(a1)) * radius;
            return Mathf.Min((p - e0).magnitude, (p - e1).magnitude) - halfWidth;
        }

        private static float Polygon(Vector2 p, Vector2[] v)
        {
            float d = Vector2.Dot(p - v[0], p - v[0]);
            float s = 1f;
            for (int i = 0, j = v.Length - 1; i < v.Length; j = i, i++)
            {
                var e = v[j] - v[i];
                var w = p - v[i];
                var b = w - e * Mathf.Clamp01(Vector2.Dot(w, e) / Vector2.Dot(e, e));
                d = Mathf.Min(d, Vector2.Dot(b, b));
                bool c1 = p.y >= v[i].y, c2 = p.y < v[j].y, c3 = e.x * w.y > e.y * w.x;
                if ((c1 && c2 && c3) || (!c1 && !c2 && !c3)) s = -s;
            }
            return s * Mathf.Sqrt(d);
        }
    }
}
