using System.Collections.Generic;
using UnityEngine;

namespace GlurpOnline.UI
{
    internal static partial class Glyphs
    {
        private const int Size = 512;
        private static readonly Dictionary<string, Sprite> cache = new Dictionary<string, Sprite>();

        public static Sprite Online => Get(OnlineDef);
        public static Sprite Host => Get(HostDef);
        public static Sprite Find => Get(FindDef);
        public static Sprite Direct => Get(DirectDef);

        public static Sprite Dot
        {
            get
            {
                if (cache.TryGetValue("Dot", out var s) && s != null) return s;
                const int size = 128;
                var pixels = new Color32[size * size];
                float radius = size / 2f - 1f;
                var centre = new Vector2(size / 2f, size / 2f);
                for (int y = 0; y < size; y++)
                {
                    for (int x = 0; x < size; x++)
                    {
                        float edge = radius - Vector2.Distance(new Vector2(x + 0.5f, y + 0.5f), centre);
                        pixels[y * size + x] = new Color32(255, 255, 255, (byte)(Mathf.Clamp01(edge + 0.5f) * 255f));
                    }
                }
                var tex = new Texture2D(size, size, TextureFormat.RGBA32, true)
                {
                    name = "GlurpOnline_Dot",
                    wrapMode = TextureWrapMode.Clamp,
                    filterMode = FilterMode.Trilinear,
                    mipMapBias = -0.25f,
                };
                tex.SetPixels32(pixels);
                tex.Apply(true, true);
                s = Sprite.Create(tex, new Rect(0, 0, size, size), new Vector2(0.5f, 0.5f), size);
                s.name = tex.name;
                cache["Dot"] = s;
                return s;
            }
        }

        private static Sprite Get(GlyphDef def)
        {
            if (cache.TryGetValue(def.Name, out var s) && s != null) return s;
            var alpha = Rasterize(def, Size);
            var pixels = new Color32[Size * Size];
            for (int i = 0; i < pixels.Length; i++) pixels[i] = new Color32(255, 255, 255, alpha[i]);
            var tex = new Texture2D(Size, Size, TextureFormat.RGBA32, true)
            {
                name = "GlurpOnline_" + def.Name,
                wrapMode = TextureWrapMode.Clamp,
                filterMode = FilterMode.Trilinear,
                mipMapBias = -0.25f,
            };
            tex.SetPixels32(pixels);
            tex.Apply(true, true);
            s = Sprite.Create(tex, new Rect(0, 0, Size, Size), new Vector2(0.5f, 0.5f), 100f);
            s.name = tex.name;
            cache[def.Name] = s;
            return s;
        }
    }
}
