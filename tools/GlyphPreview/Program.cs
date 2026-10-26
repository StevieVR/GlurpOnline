using System;
using System.IO;
using System.IO.Compression;
using GlurpOnline.UI;

namespace GlyphPreview
{
    internal static class Program
    {
        private static readonly byte[][] Tints =
        {
            new byte[] { 255, 51, 102 },
            new byte[] { 0, 255, 204 },
            new byte[] { 110, 110, 212 },
            new byte[] { 255, 51, 102 },
        };

        private static int Main(string[] args)
        {
            string path = args.Length > 0 ? args[0] : "glyphs.png";
            const int big = 256, small = 110, zoom = 3, pad = 24;
            int cell = Math.Max(big, small * zoom) + pad;
            int w = cell * Glyphs.All.Length + pad, h = big + small * zoom + pad * 3;
            var rgb = new byte[w * h * 3];
            for (int i = 0; i < rgb.Length; i++) rgb[i] = 242;

            for (int g = 0; g < Glyphs.All.Length; g++)
            {
                var alpha = Glyphs.Rasterize(Glyphs.All[g], 512);
                int x0 = pad + g * cell;
                Blit(rgb, w, Downsample(alpha, 512, big), big, 1, x0, pad, Tints[g]);
                Blit(rgb, w, Downsample(alpha, 512, small), small, zoom, x0, pad * 2 + big, Tints[g]);
            }
            File.WriteAllBytes(path, Png(rgb, w, h));
            Console.WriteLine($"wrote {path}");
            return 0;
        }

        private static float[] Downsample(byte[] src, int size, int target)
        {
            var dst = new float[target * target];
            float scale = (float)size / target;
            for (int y = 0; y < target; y++)
            {
                for (int x = 0; x < target; x++)
                {
                    int sx0 = (int)(x * scale), sx1 = Math.Max(sx0 + 1, (int)((x + 1) * scale));
                    int sy0 = (int)(y * scale), sy1 = Math.Max(sy0 + 1, (int)((y + 1) * scale));
                    float sum = 0f;
                    for (int sy = sy0; sy < sy1; sy++)
                        for (int sx = sx0; sx < sx1; sx++)
                            sum += src[sy * size + sx];
                    dst[y * target + x] = sum / ((sx1 - sx0) * (sy1 - sy0) * 255f);
                }
            }
            return dst;
        }

        private static void Blit(byte[] rgb, int w, float[] alpha, int size, int zoom, int x0, int y0, byte[] tint)
        {
            for (int y = 0; y < size * zoom; y++)
            {
                for (int x = 0; x < size * zoom; x++)
                {
                    float a = alpha[(size - 1 - y / zoom) * size + x / zoom];
                    int o = ((y0 + y) * w + x0 + x) * 3;
                    for (int c = 0; c < 3; c++) rgb[o + c] = (byte)(rgb[o + c] * (1f - a) + tint[c] * a);
                }
            }
        }

        private static byte[] Png(byte[] rgb, int w, int h)
        {
            var raw = new byte[(w * 3 + 1) * h];
            for (int y = 0; y < h; y++) Buffer.BlockCopy(rgb, y * w * 3, raw, y * (w * 3 + 1) + 1, w * 3);
            byte[] idat;
            using (var ms = new MemoryStream())
            {
                using (var z = new ZLibStream(ms, CompressionLevel.Optimal, true)) z.Write(raw, 0, raw.Length);
                idat = ms.ToArray();
            }
            using var png = new MemoryStream();
            png.Write(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 });
            var ihdr = new byte[13];
            WriteBE(ihdr, 0, w);
            WriteBE(ihdr, 4, h);
            ihdr[8] = 8; ihdr[9] = 2;
            Chunk(png, "IHDR", ihdr);
            Chunk(png, "IDAT", idat);
            Chunk(png, "IEND", new byte[0]);
            return png.ToArray();
        }

        private static void Chunk(Stream s, string type, byte[] data)
        {
            var len = new byte[4];
            WriteBE(len, 0, data.Length);
            s.Write(len);
            var body = new byte[4 + data.Length];
            for (int i = 0; i < 4; i++) body[i] = (byte)type[i];
            Buffer.BlockCopy(data, 0, body, 4, data.Length);
            s.Write(body);
            var crc = new byte[4];
            WriteBE(crc, 0, (int)Crc(body));
            s.Write(crc);
        }

        private static void WriteBE(byte[] b, int o, int v)
        {
            b[o] = (byte)(v >> 24); b[o + 1] = (byte)(v >> 16); b[o + 2] = (byte)(v >> 8); b[o + 3] = (byte)v;
        }

        private static uint Crc(byte[] data)
        {
            uint c = 0xFFFFFFFF;
            foreach (byte b in data)
            {
                c ^= b;
                for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320 ^ (c >> 1) : c >> 1;
            }
            return c ^ 0xFFFFFFFF;
        }
    }
}
