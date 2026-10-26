using System;

namespace UnityEngine
{
    public struct Vector2
    {
        public float x, y;

        public Vector2(float x, float y) { this.x = x; this.y = y; }

        public float magnitude => (float)Math.Sqrt(x * x + y * y);

        public static float Dot(Vector2 a, Vector2 b) => a.x * b.x + a.y * b.y;
        public static Vector2 operator +(Vector2 a, Vector2 b) => new Vector2(a.x + b.x, a.y + b.y);
        public static Vector2 operator -(Vector2 a, Vector2 b) => new Vector2(a.x - b.x, a.y - b.y);
        public static Vector2 operator *(Vector2 a, float d) => new Vector2(a.x * d, a.y * d);
    }

    public static class Mathf
    {
        public const float Rad2Deg = 57.29578f;
        public const float Deg2Rad = 0.017453292f;

        public static float Abs(float f) => Math.Abs(f);
        public static float Min(float a, float b) => a < b ? a : b;
        public static float Max(float a, float b) => a > b ? a : b;
        public static float Sqrt(float f) => (float)Math.Sqrt(f);
        public static float Atan2(float y, float x) => (float)Math.Atan2(y, x);
        public static float Cos(float f) => (float)Math.Cos(f);
        public static float Sin(float f) => (float)Math.Sin(f);
        public static float Clamp01(float v) => v < 0f ? 0f : v > 1f ? 1f : v;
        public static int RoundToInt(float f) => (int)Math.Round(f);

        public static float DeltaAngle(float current, float target)
        {
            float d = (target - current) % 360f;
            if (d < 0f) d += 360f;
            if (d > 180f) d -= 360f;
            return d;
        }
    }
}
