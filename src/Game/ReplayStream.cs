using System;
using System.Collections.Generic;
using GlurpOnline.Net;
using HarmonyLib;
using UnityEngine;

namespace GlurpOnline.Game
{
    internal static class ReplayAccess
    {
        public static readonly AccessTools.FieldRef<ByteData, List<byte>> Bytes =
            AccessTools.FieldRefAccess<ByteData, List<byte>>("bytes");

        public static readonly AccessTools.FieldRef<ReplayRecorder, List<ReplayTracker>> RecordedTrackers =
            AccessTools.FieldRefAccess<ReplayRecorder, List<ReplayTracker>>("recordOnUpdateTrackers");

        public static ByteData FromBytes(byte[] a, byte[] b)
        {
            var d = new ByteData();
            var list = Bytes(d);
            list.Capacity = (a?.Length ?? 0) + (b?.Length ?? 0);
            if (a != null) list.AddRange(a);
            if (b != null) list.AddRange(b);
            return d;
        }

        public static byte[] Slice(ByteData d, int from)
        {
            var list = Bytes(d);
            int n = list.Count - from;
            if (n <= 0) return new byte[0];
            var result = new byte[n];
            list.CopyTo(from, result, 0, n);
            return result;
        }
    }

    internal sealed class ReplayStreamSender
    {
        private const float SendInterval = 0.1f;

        private readonly int round;
        private readonly int player;
        private int seq;
        private float nextSend;
        private int frameOffset, eventOffset, audioOffset;
        private int frameCount, eventCount, audioCount;
        private int sentTrackerCount = -1;
        public bool Ended { get; private set; }

        public ReplayStreamSender(int round, int player)
        {
            this.round = round;
            this.player = player;
        }

        public void Update()
        {
            if (Ended || Time.realtimeSinceStartup < nextSend) return;
            nextSend = Time.realtimeSinceStartup + SendInterval;
            Flush();
        }

        public void Flush()
        {
            var rec = ReplayManager.recorder;
            if (Ended || rec == null || !rec.isRecording || Session.Current == null) return;

            int newFrames = rec.recordedFrameCount - frameCount;
            int newEvents = rec.recordedEventCount - eventCount;
            int newAudio = rec.recordedAudioEventCount - audioCount;
            var trackers = ReplayAccess.RecordedTrackers(rec);
            bool headerChanged = trackers.Count != sentTrackerCount;
            if (newFrames == 0 && newEvents == 0 && newAudio == 0 && !headerChanged) return;

            var body = new ByteData();
            body.Write(newFrames);
            WriteRaw(body, ReplayAccess.Slice(rec.recordedFrameData, frameOffset));
            body.Write((short)newEvents);
            WriteRaw(body, ReplayAccess.Slice(rec.recordedEventData, eventOffset));
            body.Write((short)newAudio);
            WriteRaw(body, ReplayAccess.Slice(rec.recordedAudioEventData, audioOffset));

            frameOffset = ReplayAccess.Bytes(rec.recordedFrameData).Count;
            eventOffset = ReplayAccess.Bytes(rec.recordedEventData).Count;
            audioOffset = ReplayAccess.Bytes(rec.recordedAudioEventData).Count;
            frameCount = rec.recordedFrameCount;
            eventCount = rec.recordedEventCount;
            audioCount = rec.recordedAudioEventCount;

            var bodyBytes = body.ToArray();
            var compressed = Compression.Deflate(bodyBytes, 0, bodyBytes.Length);

            var w = new PacketWriter(Msg.StreamChunk, Session.Current.LocalId);
            w.Write((short)round);
            w.Write((short)player);
            w.Write(seq++);
            w.Write(headerChanged);
            if (headerChanged)
            {
                w.WriteBytes(BuildHeader(trackers));
                sentTrackerCount = trackers.Count;
            }
            w.WriteBytes(compressed);
            Session.Current.Broadcast(w);
        }

        public void End()
        {
            if (Ended) return;
            Flush();
            Ended = true;
            if (Session.Current == null) return;
            var w = new PacketWriter(Msg.StreamEnd, Session.Current.LocalId);
            w.Write((short)round);
            w.Write((short)player);
            Session.Current.Broadcast(w);
        }

        private static void WriteRaw(ByteData d, byte[] bytes)
        {
            ReplayAccess.Bytes(d).AddRange(bytes);
        }

        private static byte[] BuildHeader(List<ReplayTracker> trackers)
        {
            var cfg = EventRandomiser.currentEvent;
            var current = TurnManager.CurrentPlayer();
            var d = new ByteData();
            d.Write("glurp");
            d.Write(ReplayRecorder.replayVersionNumber);
            d.Write(cfg.sceneName);
            d.Write(cfg is ExternalEventConfiguration);
            d.Write(cfg.variation ?? "");
            d.Write(cfg.mutator ?? "NoMod");
            d.Write(EventRandomiser.currentMutator is ExternalMutator);
            d.Write((short)Mathf.Max(0, Array.IndexOf(GameManager.singleton.playerColors, current.colorScheme)));
            d.Write(current.hatID ?? "NoHat");
            var unique = new List<ReplayTracker>();
            var seen = new HashSet<short>();
            foreach (var t in trackers)
            {
                if ((object)t != null && t.config != null && seen.Add(t.config.sceneID)) unique.Add(t);
            }
            d.Write((short)unique.Count);
            foreach (var t in unique) t.WriteConfigurationToData(d);
            return d.ToArray();
        }
    }

    internal sealed class ReplayStreamReceiver
    {
        private const float StartBuffer = 0.3f;

        private byte[] header;
        private readonly List<ByteData> pending = new List<ByteData>();
        private float firstChunkTime = -1f;
        private int nextSeq;
        public bool Playing { get; private set; }
        public bool Ended { get; set; }
        public bool Broken { get; private set; }

        public void AddChunk(int seq, byte[] newHeader, byte[] compressedBody)
        {
            if (Broken) return;
            if (seq != nextSeq)
            {
                Plugin.Log.LogWarning($"Stream chunk out of order: got {seq}, expected {nextSeq}");
                Broken = true;
                return;
            }
            nextSeq++;
            if (newHeader != null) header = newHeader;
            if (header == null) return;
            var data = ReplayAccess.FromBytes(header, Compression.Inflate(compressedBody));
            if (firstChunkTime < 0f) firstChunkTime = Time.realtimeSinceStartup;
            pending.Add(data);
            Pump();
        }

        public void Update() => Pump();

        private void Pump()
        {
            var player = ReplayManager.player;
            if (player == null || pending.Count == 0 || Broken) return;
            if (!Playing && Time.realtimeSinceStartup - firstChunkTime < StartBuffer && !Ended) return;
            var logger = Debug.unityLogger;
            bool wasEnabled = logger.logEnabled;
            logger.logEnabled = false;
            try
            {
                if (!Playing)
                {
                    player.shouldStopWhenOutOfData = false;
                    player.shouldCatchUp = true;
                    player.loop = false;
                    player.StartPlaying(pending[0], true);
                    pending.RemoveAt(0);
                    Playing = true;
                }
                foreach (var d in pending) player.ReadReplayData(d, false, true);
            }
            catch (Exception e)
            {
                Broken = true;
                Plugin.Log.LogError("Live replay stream failed: " + e);
            }
            finally
            {
                logger.logEnabled = wasEnabled;
            }
            pending.Clear();
        }

        public void Stop()
        {
            var player = ReplayManager.player;
            if (player != null && Playing) player.isPlaying = false;
            Playing = false;
            pending.Clear();
        }
    }
}
