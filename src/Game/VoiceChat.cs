using System;
using System.Collections.Generic;
using System.Linq;
using GlurpOnline.Net;
using Steamworks;
using UnityEngine;

namespace GlurpOnline.Game
{
    internal static class VoiceChat
    {
        private const int SampleRate = 24000;
        private const float SendInterval = 0.04f;
        private const float MinBuffered = 0.08f, MaxStartBuffered = 0.35f, StartBufferedStep = 0.04f;
        private const float SteadyFor = 8f;
        private const float MaxBuffered = 0.4f;
        private const float LoudEnough = 0.015f;
        private const float SpeakingHold = 0.3f;
        private const float NearDistance = 5f;
        private const float FarDistance = 30f;
        private const float PanDistance = 12f;
        private const float MaxPan = 0.7f;

        private const byte CodecSteam = 0;

        private sealed class Speaker
        {
            public ulong Id;
            public GameObject Object;
            public AudioSource Source;
            public readonly float[] Ring = new float[SampleRate];
            public int Start, Count;
            public float Between;
            public bool Playing;
            public float Volume = 1f, Pan;
            public float StartBuffered = 0.12f;
            public double RanDryAt = -10, JudgedDryAt = -10, SteadySince;
            public int Underruns;
            public float LastVoice = -10f;
        }

        private sealed class VoiceOutput : MonoBehaviour
        {
            public Speaker Speaker;
            public int OutputRate;

            private void OnAudioFilterRead(float[] data, int channels)
            {
                var sp = Speaker;
                if (sp == null || OutputRate <= 0) return;
                float step = (float)SampleRate / OutputRate;
                lock (sp)
                {
                    float left = sp.Volume * Mathf.Min(1f, 1f - sp.Pan), right = sp.Volume * Mathf.Min(1f, 1f + sp.Pan);
                    if (!sp.Playing && sp.Count >= (int)(sp.StartBuffered * SampleRate)) sp.Playing = true;
                    int size = sp.Ring.Length;
                    for (int i = 0; i < data.Length; i += channels)
                    {
                        float v = 0f;
                        if (sp.Playing)
                        {
                            if (sp.Count < 2)
                            {
                                sp.Playing = false;
                                sp.RanDryAt = Clock.Elapsed.TotalSeconds;
                            }
                            else
                            {
                                v = Mathf.Lerp(sp.Ring[sp.Start], sp.Ring[(sp.Start + 1) % size], sp.Between);
                                sp.Between += step;
                                while (sp.Between >= 1f && sp.Count > 1)
                                {
                                    sp.Between -= 1f;
                                    sp.Start = (sp.Start + 1) % size;
                                    sp.Count--;
                                }
                            }
                        }
                        data[i] = v * left;
                        if (channels > 1) data[i + 1] = v * right;
                        for (int c = 2; c < channels; c++) data[i + c] = v * sp.Volume;
                    }
                }
            }
        }

        private static readonly Dictionary<ulong, Speaker> speakers = new Dictionary<ulong, Speaker>();
        private static readonly System.Diagnostics.Stopwatch Clock = System.Diagnostics.Stopwatch.StartNew();
        private static GameObject root;
        private static bool recording;
        private static float nextSend, localSpokeAt = -10f;
        private static EVoiceResult lastResult = EVoiceResult.k_EVoiceResultOK;
        private static readonly byte[] captured = new byte[16 * 1024];
        private static readonly byte[] decoded = new byte[SampleRate * 2];
        private static readonly float[] samples = new float[SampleRate];

        private static bool Allowed
        {
            get
            {
                var s = Session.Current;
                return s != null && s.Phase != SessionPhase.Connecting && s.Settings.Voice && Session.SteamAvailable;
            }
        }

        public static void Init()
        {
            Session.GameMessage += OnGameMessage;
            Session.SessionClosed += _ => Shutdown();
        }

        public static bool IsSpeaking(ulong id)
        {
            if (!Allowed) return false;
            float now = Time.realtimeSinceStartup;
            if (id == Session.Current.LocalId) return now - localSpokeAt < SpeakingHold;
            return speakers.TryGetValue(id, out var sp) && now - sp.LastVoice < SpeakingHold;
        }

        public static void Update()
        {
            var s = Session.Current;
            bool allowed = Allowed;
            bool wanted = allowed && s.Members.Count > 1 && !Plugin.MuteMic.Value;
            if (wanted != recording) SetRecording(wanted);
            float now = Time.realtimeSinceStartup;
            if (recording && now >= nextSend
               )
            {
                nextSend = now + SendInterval;
                Capture(s, now);
            }
            foreach (var id in speakers.Keys.ToList())
            {
                if (!allowed || !s.Members.Any(m => m.Id == id)) Remove(id);
            }
            foreach (var sp in speakers.Values) Place(sp);
        }

        private static void Shutdown()
        {
            if (recording) SetRecording(false);
            foreach (var id in speakers.Keys.ToList()) Remove(id);
        }

        private static void SetRecording(bool on)
        {
            recording = on;
            try
            {
                if (on) SteamUser.StartVoiceRecording();
                else SteamUser.StopVoiceRecording();
                Plugin.Log.LogInfo(on ? "Voice chat on" : "Voice chat off");
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning("Voice chat unavailable: " + e.Message);
            }
        }

        private static void Capture(Session s, float now)
        {
            var result = SteamUser.GetAvailableVoice(out uint available);
            if (result == EVoiceResult.k_EVoiceResultOK && available > 0)
                result = SteamUser.GetVoice(true, captured, (uint)captured.Length, out available);
            if (result != lastResult)
            {
                if (result != EVoiceResult.k_EVoiceResultOK && result != EVoiceResult.k_EVoiceResultNoData) Plugin.Log.LogInfo("Voice recording: " + result);
                lastResult = result;
            }
            if (result != EVoiceResult.k_EVoiceResultOK || available == 0) return;
            if (Loud(Decode(captured, (int)available))) localSpokeAt = now;
            var w = new PacketWriter(Msg.Voice, s.LocalId);
            w.Write(CodecSteam);
            w.WriteBytes(captured, 0, (int)available);
            s.Broadcast(w);
        }

        private static int Decode(byte[] data, int length)
        {
            if (SteamUser.DecompressVoice(data, (uint)length, decoded, (uint)decoded.Length, out uint written, SampleRate) != EVoiceResult.k_EVoiceResultOK) return 0;
            int n = (int)written / 2;
            for (int i = 0; i < n; i++) samples[i] = (short)(decoded[2 * i] | (decoded[2 * i + 1] << 8)) / 32768f;
            return n;
        }

        private static bool Loud(int n)
        {
            if (n == 0) return false;
            double sum = 0;
            for (int i = 0; i < n; i++) sum += samples[i] * samples[i];
            return Math.Sqrt(sum / n) > LoudEnough;
        }

        private static void OnGameMessage(Msg type, ulong origin, PacketReader r)
        {
            if (type != Msg.Voice || !Allowed || origin == Session.Current.LocalId) return;
            byte codec = r.ReadByte();
            var data = r.ReadBytes();
            int n = 0;
            if (codec == CodecSteam)
            {
                n = Decode(data, data.Length);
            }
            if (n == 0) return;
            var sp = SpeakerFor(origin);
            if (Loud(n)) sp.LastVoice = Time.realtimeSinceStartup;
            lock (sp)
            {
                AdaptBuffer(sp, Clock.Elapsed.TotalSeconds);
                int size = sp.Ring.Length;
                for (int i = 0; i < n; i++)
                {
                    if (sp.Count == size)
                    {
                        sp.Start = (sp.Start + 1) % size;
                        sp.Count--;
                    }
                    sp.Ring[(sp.Start + sp.Count) % size] = samples[i];
                    sp.Count++;
                }
                int max = (int)(MaxBuffered * SampleRate);
                if (sp.Count > max)
                {
                    sp.Start = (sp.Start + sp.Count - max) % size;
                    sp.Count = max;
                }
            }
        }

        private static void AdaptBuffer(Speaker sp, double now)
        {
            if (sp.RanDryAt > sp.JudgedDryAt)
            {
                sp.JudgedDryAt = sp.RanDryAt;
                if (now - sp.RanDryAt < 0.25)
                {
                    sp.StartBuffered = Mathf.Min(sp.StartBuffered + StartBufferedStep, MaxStartBuffered);
                    sp.Underruns++;
                    sp.SteadySince = now;
                    return;
                }
            }
            if (now - sp.SteadySince > SteadyFor)
            {
                sp.StartBuffered = Mathf.Max(sp.StartBuffered - StartBufferedStep / 2f, MinBuffered);
                sp.SteadySince = now;
            }
        }

        private static Speaker SpeakerFor(ulong id)
        {
            if (speakers.TryGetValue(id, out var sp)) return sp;
            if (root == null)
            {
                root = new GameObject("Glurp Online Voices");
                UnityEngine.Object.DontDestroyOnLoad(root);
                root.hideFlags = HideFlags.HideAndDontSave;
            }
            sp = new Speaker { Id = id, Object = new GameObject("Voice " + id), SteadySince = Clock.Elapsed.TotalSeconds };
            sp.Object.transform.SetParent(root.transform, false);
            int rate = AudioSettings.outputSampleRate;
            sp.Source = sp.Object.AddComponent<AudioSource>();
            sp.Source.clip = AudioClip.Create("Glurp Online Voice", rate, 1, rate, false);
            sp.Source.loop = true;
            sp.Source.spatialBlend = 0f;
            sp.Source.ignoreListenerPause = true;
            sp.Source.bypassReverbZones = true;
            sp.Source.priority = 0;
            var output = sp.Object.AddComponent<VoiceOutput>();
            output.OutputRate = rate;
            output.Speaker = sp;
            sp.Source.Play();
            speakers[id] = sp;
            return sp;
        }

        private static void Remove(ulong id)
        {
            if (!speakers.TryGetValue(id, out var sp)) return;
            speakers.Remove(id);
            if (sp.Source != null && sp.Source.clip != null) UnityEngine.Object.Destroy(sp.Source.clip);
            if (sp.Object != null) UnityEngine.Object.Destroy(sp.Object);
        }

        private static void Place(Speaker sp)
        {
            float volume = 1f, pan = 0f;
            if (Listener(out var mine) && GhostSync.TryGetPosition(sp.Id, out var theirs))
            {
                float t = Mathf.InverseLerp(NearDistance, FarDistance, Vector2.Distance(mine, theirs));
                volume = (1f - t) * (1f - t);
                pan = Mathf.Clamp((theirs.x - mine.x) / PanDistance, -1f, 1f) * MaxPan;
            }
            volume *= Mathf.Clamp01(Plugin.VoiceVolume.Value);
            float step = Time.unscaledDeltaTime * 3f;
            lock (sp)
            {
                sp.Volume = Mathf.MoveTowards(sp.Volume, volume, step);
                sp.Pan = Mathf.MoveTowards(sp.Pan, pan, step);
            }
        }

        private static bool Listener(out Vector2 position)
        {
            position = default;
            var menu = GameMenu.singleton;
            var guy = GameManager.currentEvent != null ? GameManager.currentEvent.guy : null;
            if (!OnlineGame.Active || !OnlineGame.AllAtOnce || !OnlineGame.TurnBegun || guy == null || guy.bodyBody == null) return false;
            if (menu == null || (menu.state != MenuScreen.PlayMode && menu.state != MenuScreen.Pause)) return false;
            position = guy.bodyBody.position;
            return true;
        }
    }
}
