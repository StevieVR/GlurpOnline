using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;
using UnityEngine;

namespace GlurpOnline.Game
{
    internal sealed class MicCapture
    {
        public const int Rate = 16000;

        private readonly string device;
        private readonly AudioClip clip;
        private readonly int clipRate;
        private int readPos;
        private float[] chunk = new float[0];
        private float last, resampleAt;

        private MicCapture(string device, AudioClip clip, int clipRate)
        {
            this.device = device;
            this.clip = clip;
            this.clipRate = clipRate;
        }

        public static string[] Devices()
        {
            try { return Microphone.devices; }
            catch { return new string[0]; }
        }

        public static MicCapture Open(string device)
        {
            try
            {
                Microphone.GetDeviceCaps(device, out int min, out int max);
                int rate = min == 0 && max == 0 ? Rate : Mathf.Clamp(Rate, min, max);
                var clip = Microphone.Start(device, true, 1, rate);
                if (clip == null) return null;
                Plugin.Log.LogInfo($"Voice: recording from {device} at {rate} Hz");
                return new MicCapture(device, clip, rate);
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning($"Voice: couldn't open the microphone {device}: {e.Message}");
                return null;
            }
        }

        public void Close()
        {
            try { Microphone.End(device); }
            catch { }
        }

        public void Read(List<float> into)
        {
            int length = clip.samples;
            int pos = Microphone.GetPosition(device);
            int count = (pos - readPos + length) % length;
            if (count <= 0) return;
            if (chunk.Length != count) chunk = new float[count];
            int first = Math.Min(count, length - readPos);
            var part = new float[first];
            clip.GetData(part, readPos);
            Array.Copy(part, chunk, first);
            if (count > first)
            {
                var rest = new float[count - first];
                clip.GetData(rest, 0);
                Array.Copy(rest, 0, chunk, first, rest.Length);
            }
            readPos = pos;
            if (clipRate == Rate)
            {
                into.AddRange(chunk);
                return;
            }
            float step = (float)clipRate / Rate;
            float t = resampleAt;
            while (t < count)
            {
                int i = (int)t;
                float a = i == 0 ? last : chunk[i - 1], b = chunk[i];
                into.Add(Mathf.Lerp(a, b, t - i));
                t += step;
            }
            resampleAt = t - count;
            last = chunk[count - 1];
        }
    }

    internal static class Adpcm
    {
        private static readonly int[] IndexTable = { -1, -1, -1, -1, 2, 4, 6, 8, -1, -1, -1, -1, 2, 4, 6, 8 };

        private static readonly int[] StepTable =
        {
            7, 8, 9, 10, 11, 12, 13, 14, 16, 17, 19, 21, 23, 25, 28, 31, 34, 37, 41, 45, 50, 55, 60, 66, 73, 80, 88, 97, 107, 118,
            130, 143, 157, 173, 190, 209, 230, 253, 279, 307, 337, 371, 408, 449, 494, 544, 598, 658, 724, 796, 876, 963, 1060,
            1166, 1282, 1411, 1552, 1707, 1878, 2066, 2272, 2499, 2749, 3024, 3327, 3660, 4026, 4428, 4871, 5358, 5894, 6484,
            7132, 7845, 8630, 9493, 10442, 11487, 12635, 13899, 15289, 16818, 18500, 20350, 22385, 24623, 27086, 29794, 32767,
        };

        public sealed class Encoder
        {
            private int predictor, index;

            public byte[] Encode(List<float> samples, int start, int count)
            {
                var data = new byte[5 + (count + 1) / 2];
                data[0] = (byte)predictor;
                data[1] = (byte)(predictor >> 8);
                data[2] = (byte)index;
                data[3] = (byte)count;
                data[4] = (byte)(count >> 8);
                for (int i = 0; i < count; i++)
                {
                    int sample = Mathf.Clamp((int)(samples[start + i] * 32767f), -32768, 32767);
                    int diff = sample - predictor;
                    int code = 0;
                    if (diff < 0)
                    {
                        code = 8;
                        diff = -diff;
                    }
                    int step = StepTable[index];
                    int change = step >> 3;
                    if (diff >= step) { code |= 4; diff -= step; change += step; }
                    step >>= 1;
                    if (diff >= step) { code |= 2; diff -= step; change += step; }
                    step >>= 1;
                    if (diff >= step) { code |= 1; change += step; }
                    predictor = Mathf.Clamp(predictor + ((code & 8) != 0 ? -change : change), -32768, 32767);
                    index = Mathf.Clamp(index + IndexTable[code], 0, 88);
                    if ((i & 1) == 0) data[5 + i / 2] = (byte)code;
                    else data[5 + i / 2] |= (byte)(code << 4);
                }
                return data;
            }
        }

        public static int Decode(byte[] data, float[] into)
        {
            if (data.Length < 5) return 0;
            int predictor = (short)(data[0] | (data[1] << 8));
            int index = Mathf.Clamp(data[2], 0, 88);
            int count = Math.Min(data[3] | (data[4] << 8), Math.Min(into.Length, (data.Length - 5) * 2));
            for (int i = 0; i < count; i++)
            {
                int code = (data[5 + i / 2] >> ((i & 1) * 4)) & 15;
                int step = StepTable[index];
                int change = step >> 3;
                if ((code & 4) != 0) change += step;
                if ((code & 2) != 0) change += step >> 1;
                if ((code & 1) != 0) change += step >> 2;
                predictor = Mathf.Clamp(predictor + ((code & 8) != 0 ? -change : change), -32768, 32767);
                index = Mathf.Clamp(index + IndexTable[code], 0, 88);
                into[i] = predictor / 32768f;
            }
            return count;
        }
    }

    internal sealed class WaveOutput
    {
        private const int Buffers = 4;
        private const int FramesPerBuffer = 960;
        private const uint WhdrDone = 1;

        [StructLayout(LayoutKind.Sequential)]
        private struct WaveFormat
        {
            public ushort Tag, Channels;
            public uint SamplesPerSec, AvgBytesPerSec;
            public ushort BlockAlign, BitsPerSample, ExtraSize;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct Caps
        {
            public ushort Mid, Pid;
            public uint DriverVersion;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string Name;
            public uint Formats;
            public ushort Channels, Reserved;
            public uint Support;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct Header
        {
            public IntPtr Data;
            public uint BufferLength, BytesRecorded;
            public IntPtr User;
            public uint Flags, Loops;
            public IntPtr Next, Reserved;
        }

        [DllImport("winmm.dll")] private static extern int waveOutGetNumDevs();
        [DllImport("winmm.dll", EntryPoint = "waveOutGetDevCapsW", CharSet = CharSet.Unicode)] private static extern int waveOutGetDevCaps(UIntPtr device, ref Caps caps, int size);
        [DllImport("winmm.dll")] private static extern int waveOutOpen(out IntPtr handle, UIntPtr device, ref WaveFormat format, IntPtr callback, IntPtr instance, uint flags);
        [DllImport("winmm.dll")] private static extern int waveOutPrepareHeader(IntPtr handle, IntPtr header, int size);
        [DllImport("winmm.dll")] private static extern int waveOutUnprepareHeader(IntPtr handle, IntPtr header, int size);
        [DllImport("winmm.dll")] private static extern int waveOutWrite(IntPtr handle, IntPtr header, int size);
        [DllImport("winmm.dll")] private static extern int waveOutReset(IntPtr handle);
        [DllImport("winmm.dll")] private static extern int waveOutClose(IntPtr handle);

        private static readonly int HeaderSize = Marshal.SizeOf(typeof(Header));
        private static readonly int FlagsOffset = (int)Marshal.OffsetOf(typeof(Header), "Flags");

        private readonly IntPtr handle;
        private readonly IntPtr[] headers = new IntPtr[Buffers];
        private readonly IntPtr[] data = new IntPtr[Buffers];
        private readonly int rate;
        private readonly Action<float[], int, int> render;
        private readonly Thread thread;
        private volatile bool running = true;

        public static List<string> Devices()
        {
            var names = new List<string>();
            try
            {
                int n = waveOutGetNumDevs();
                for (int i = 0; i < n; i++)
                {
                    var caps = new Caps();
                    if (waveOutGetDevCaps(new UIntPtr((uint)i), ref caps, Marshal.SizeOf(typeof(Caps))) == 0) names.Add(caps.Name);
                }
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning("Voice: can't list output devices: " + e.Message);
            }
            return names;
        }

        public static WaveOutput Open(string name, int rate, Action<float[], int, int> render)
        {
            int index = Devices().IndexOf(name);
            if (index < 0)
            {
                Plugin.Log.LogWarning($"Voice: output device {name} isn't there; using the game's sound");
                return null;
            }
            try
            {
                var format = new WaveFormat { Tag = 1, Channels = 2, SamplesPerSec = (uint)rate, BitsPerSample = 16, BlockAlign = 4, AvgBytesPerSec = (uint)rate * 4 };
                int result = waveOutOpen(out var handle, new UIntPtr((uint)index), ref format, IntPtr.Zero, IntPtr.Zero, 0);
                if (result != 0)
                {
                    Plugin.Log.LogWarning($"Voice: couldn't open {name} (error {result}); using the game's sound");
                    return null;
                }
                Plugin.Log.LogInfo($"Voice: playing on {name}");
                return new WaveOutput(handle, rate, render);
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning($"Voice: couldn't open {name}: {e.Message}");
                return null;
            }
        }

        private WaveOutput(IntPtr handle, int rate, Action<float[], int, int> render)
        {
            this.handle = handle;
            this.rate = rate;
            this.render = render;
            for (int i = 0; i < Buffers; i++)
            {
                data[i] = Marshal.AllocHGlobal(FramesPerBuffer * 4);
                headers[i] = Marshal.AllocHGlobal(HeaderSize);
                Marshal.StructureToPtr(new Header { Data = data[i], BufferLength = FramesPerBuffer * 4 }, headers[i], false);
                waveOutPrepareHeader(handle, headers[i], HeaderSize);
            }
            thread = new Thread(Run) { IsBackground = true, Name = "Glurp Online voice output" };
            thread.Start();
        }

        private void Run()
        {
            var mix = new float[FramesPerBuffer * 2];
            var pcm = new short[FramesPerBuffer * 2];
            var queued = new bool[Buffers];
            try
            {
                while (running)
                {
                    for (int i = 0; i < Buffers; i++)
                    {
                        if (queued[i] && (Marshal.ReadInt32(headers[i], FlagsOffset) & WhdrDone) == 0) continue;
                        Array.Clear(mix, 0, mix.Length);
                        render(mix, FramesPerBuffer, rate);
                        for (int j = 0; j < mix.Length; j++) pcm[j] = (short)Mathf.Clamp(mix[j] * 32767f, -32768f, 32767f);
                        Marshal.Copy(pcm, 0, data[i], pcm.Length);
                        waveOutWrite(handle, headers[i], HeaderSize);
                        queued[i] = true;
                    }
                    Thread.Sleep(4);
                }
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning("Voice: output device stopped: " + e.Message);
            }
        }

        public void Close()
        {
            running = false;
            thread.Join(500);
            waveOutReset(handle);
            for (int i = 0; i < Buffers; i++)
            {
                waveOutUnprepareHeader(handle, headers[i], HeaderSize);
                Marshal.FreeHGlobal(headers[i]);
                Marshal.FreeHGlobal(data[i]);
            }
            waveOutClose(handle);
        }
    }
}
