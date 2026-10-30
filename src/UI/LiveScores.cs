using System.Collections.Generic;
using System.Linq;
using GlurpOnline.Game;
using GlurpOnline.Net;
using HarmonyLib;
using UnityEngine;

namespace GlurpOnline.UI
{
    internal static class LiveScores
    {
        private const float SendInterval = 0.25f;
        private const float ShowInterval = 0.2f;

        private sealed class Entry
        {
            public EventScore Score;
            public float Reported, ReportedAt;
            public bool Started, Finished;
        }

        private static readonly Dictionary<int, Entry> others = new Dictionary<int, Entry>();
        private static readonly HashSet<EventScore> waiting = new HashSet<EventScore>();
        private static readonly Dictionary<OnscreenScoreboardEntry, EventScore> shown = new Dictionary<OnscreenScoreboardEntry, EventScore>();
        private static int round = -1;
        private static float nextSend, nextShow;

        private static readonly AccessTools.FieldRef<OnscreenScoreboard, EventScore[]> BoardScores =
            AccessTools.FieldRefAccess<OnscreenScoreboard, EventScore[]>("scores");
        private static readonly AccessTools.FieldRef<OnscreenScoreboard, EventScore> BoardPlayer =
            AccessTools.FieldRefAccess<OnscreenScoreboard, EventScore>("playerScore");
        private static readonly System.Reflection.MethodInfo Reorder = AccessTools.Method(typeof(OnscreenScoreboard), "Reorder");

        private static bool Playing =>
            OnlineGame.Active && OnlineGame.AllAtOnce && OnlineGame.TurnBegun && GameManager.currentEvent != null && Session.Current != null;

        public static void Update()
        {
            if (!Playing) return;
            if (round != OnlineGame.TurnRound)
            {
                round = OnlineGame.TurnRound;
                others.Clear();
                waiting.Clear();
                shown.Clear();
            }
            float now = Time.realtimeSinceStartup;
            if (now >= nextSend)
            {
                nextSend = now + SendInterval;
                Send();
            }
            if (others.Count == 0) return;
            UpdateValues();
            if (now >= nextShow)
            {
                nextShow = now + ShowInterval;
                Show();
            }
            foreach (var pair in shown)
            {
                if (pair.Key == null || !pair.Key.gameObject.activeSelf || !others.Values.Any(e => e.Score == pair.Value)) continue;
                pair.Key.timeText.text = waiting.Contains(pair.Value) ? "-"
                    : pair.Value.isPrimaryScoreValid ? pair.Value.score.ToString("F2") : Lean.Localization.LeanLocalization.GetTranslationText("Generic/DNF");
            }
        }

        private static void UpdateValues()
        {
            var em = GameManager.currentEvent;
            if (!(em.api.scoreManager is GlurpAPI.GlurpTimerScoreManager)) return;
            float now = Time.realtimeSinceStartup;
            foreach (var e in others.Values)
            {
                if (!e.Started || e.Finished || !e.Score.isPrimaryScoreValid) continue;
                e.Score.score = em.hasEventStarted && !em.hasEventFinished ? em.currentScore : e.Reported + (now - e.ReportedAt);
            }
        }

        private static void Send()
        {
            var em = GameManager.currentEvent;
            var player = TurnManager.CurrentPlayer();
            var s = Session.Current;
            if (player == null) return;
            bool valid = em.api.scoreManager == null || em.api.scoreManager.isValid;
            var w = new PacketWriter(Msg.LiveScore, s.LocalId);
            w.Write((short)OnlineGame.TurnRound);
            w.Write((short)player.playerNumber);
            w.Write(em.currentScore);
            w.Write((byte)((em.hasEventStarted ? 1 : 0) | (valid ? 2 : 0) | (em.hasEventFinished ? 4 : 0)));
            s.Broadcast(w);
        }

        public static void OnMessage(PacketReader r)
        {
            if (!Playing || r.ReadShort() != OnlineGame.TurnRound) return;
            int number = r.ReadShort();
            float score = r.ReadFloat();
            byte flags = r.ReadByte();
            if (!others.TryGetValue(number, out var entry))
            {
                var player = TurnManager.activePlayers.FirstOrDefault(p => p.playerNumber == number);
                if (player == null) return;
                entry = new Entry { Score = new EventScore(player, 0f) };
                others[number] = entry;
            }
            entry.Started = (flags & 1) != 0;
            entry.Finished = (flags & 4) != 0;
            entry.Reported = score;
            entry.ReportedAt = Time.realtimeSinceStartup;
            entry.Score.score = score;
            entry.Score.isPrimaryScoreValid = (flags & 2) != 0;
            if (entry.Started) waiting.Remove(entry.Score);
            else waiting.Add(entry.Score);
        }

        private static void Show()
        {
            var board = GameManager.singleton != null ? GameManager.singleton.onscreenScoreboard : null;
            var mine = board != null ? BoardPlayer(board) : null;
            if (mine == null || Reorder == null) return;
            var em = GameManager.currentEvent;
            var scores = others.Values.Select(e => e.Score).ToArray();
            var ranked = new List<EventScore>(scores) { new EventScore(mine.player, em.currentScore) };
            GameManager.SortScores(ranked, calculateRank: true);
            BoardScores(board) = scores;
            Reorder.Invoke(board, null);
        }

        public static void AfterEntryConfigured(OnscreenScoreboardEntry entry, EventScore score)
        {
            if (entry == null) return;
            shown[entry] = score;
            if (score != null && waiting.Contains(score)) entry.timeText.text = "-";
        }
    }

    [HarmonyPatch(typeof(OnscreenScoreboardEntry), nameof(OnscreenScoreboardEntry.Configure))]
    internal static class LiveScoreEntryPatch
    {
        private static void Postfix(OnscreenScoreboardEntry __instance, EventScore score) => LiveScores.AfterEntryConfigured(__instance, score);
    }
}
