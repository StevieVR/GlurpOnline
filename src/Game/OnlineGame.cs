using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using GlurpAPI;
using GlurpOnline.Net;
using GlurpOnline.UI;
using HarmonyLib;
using UnityEngine;

namespace GlurpOnline.Game
{
    internal static class OnlineGame
    {
        private const byte EvCountdown = 200;
        private const byte EvGo = 201;
        private const byte EvFinishText = 202;
        private const byte EvTimeUp = 203;
        private const byte EvOutOfBounds = 204;

        private sealed class TurnResultData
        {
            public float Score, Secondary;
            public bool PrimaryValid, SecondaryValid;
        }

        private enum FlowType { Continue, MoreRounds, BackToLobby }

        private sealed class FlowCommand
        {
            public FlowType Type;
            public MenuScreen FromState;
            public int PlaylistIndex;
            public EventSpec Spec;
            public int Rounds;
            public float QueuedAt;
        }

        private struct Buffered
        {
            public Msg Type;
            public ulong Origin;
            public long Key;
            public byte[] Payload;
        }

        public static bool Active { get; private set; }
        public static bool BackToLobbyPending;
        public static string PendingPopup;

        public static List<RosterEntry> Roster = new List<RosterEntry>();
        public static readonly HashSet<ulong> DroppedPeers = new HashSet<ulong>();

        public static int TurnRound = -1;
        public static int TurnPlayer = -1;
        public static bool IsLocalTurn;
        public static bool SceneLoaded;
        public static bool TurnBegun;
        public static bool TurnFinished;
        private static float sceneLoadedTime;
        public static bool IsSpectating => Active && !IsLocalTurn;

        public static EventSpec PendingEventSpec;
        public static bool ApplyingNetworkCommand;
        public static bool AllowRemoteFinish;

        public static bool AllAtOnce;
        public static bool SeeEachOther;
        public static int GameSeed;

        public static int RoundSeed(int round) => unchecked(GameSeed * 31 + round * 7919);
        public static bool RoundOverShown;
        private static readonly Dictionary<long, TurnResultData> roundResults = new Dictionary<long, TurnResultData>();
        private static bool simWaiting;
        private static bool simMerged;
        private static readonly AccessTools.FieldRef<EventManager, float> MainFloorY =
            AccessTools.FieldRefAccess<EventManager, float>("mainFloorY");

        private static ReplayStreamSender sender;
        private static ReplayStreamReceiver receiver;
        private static TurnResultData remoteResult;
        private static bool remoteFinishSeen;
        private static float remoteResultTime;
        private static bool goRecorded;
        private static readonly Dictionary<long, HashSet<ulong>> readyPeers = new Dictionary<long, HashSet<ulong>>();
        private static readonly HashSet<long> finishedTurns = new HashSet<long>();
        private static readonly List<Buffered> early = new List<Buffered>();
        private static readonly Queue<FlowCommand> flow = new Queue<FlowCommand>();

        private static readonly AccessTools.FieldRef<EventManager, bool> PrimaryValid =
            AccessTools.FieldRefAccess<EventManager, bool>("isCurrentPrimaryScoreValid");
        private static readonly AccessTools.FieldRef<EventManager, bool> SecondaryValid =
            AccessTools.FieldRefAccess<EventManager, bool>("isCurrentSecondaryScoreValid");
        private static readonly AccessTools.FieldRef<EventManager, GameObject> StartLineArrow =
            AccessTools.FieldRefAccess<EventManager, GameObject>("startLineArrow");
        private static readonly AccessTools.FieldRef<EventManager, Animator> StartLineAnimator =
            AccessTools.FieldRefAccess<EventManager, Animator>("startLineAnimator");
        private static readonly AccessTools.FieldRef<EventManager, GlurpStartingLine> StartLine =
            AccessTools.FieldRefAccess<EventManager, GlurpStartingLine>("startLine");
        private static readonly AccessTools.FieldRef<EventManager, MusicTrack> EventMusic =
            AccessTools.FieldRefAccess<EventManager, MusicTrack>("musicTrack");
        private static readonly AccessTools.FieldRef<EventManager, bool> HasRunOutOfTime =
            AccessTools.FieldRefAccess<EventManager, bool>("hasRunOutOfTime");
        private static readonly System.Reflection.MethodInfo ScoreboardContinue =
            AccessTools.Method(typeof(GameMenu), "ScoreboardContinue");
        private static readonly AccessTools.FieldRef<GameMenu, MenuScreen> PrePauseState =
            AccessTools.FieldRefAccess<GameMenu, MenuScreen>("prePauseMenuState");

        public static void Init()
        {
            Session.GameMessage += OnGameMessage;
            Session.SessionClosed += OnSessionClosed;
            UnityEngine.SceneManagement.SceneManager.sceneLoaded += (scene, mode) =>
            {
                if (Active) UnityEngine.Random.InitState(RoundSeed(EventRandomiser.playlistIndex));
            };
        }

        private static long Key(int round, int player) => ((long)round << 16) | (uint)(player & 0xffff);
        private static long CurrentKey => Key(TurnRound, TurnPlayer);

        public static RosterEntry Entry(int playerNumber) => Roster.FirstOrDefault(e => e.PlayerNumber == playerNumber);

        public static bool OwnerDropped(int playerNumber)
        {
            var e = Entry(playerNumber);
            return e != null && DroppedPeers.Contains(e.Owner);
        }

        public static void HostStartGame()
        {
            var s = Session.Current;
            if (s == null || !s.IsHost || Active || GameManager.isPlaying) return;
            var roster = BuildRoster(s);
            int rounds = Mathf.Clamp(s.Settings.Rounds, 1, 99);
            int difficulty = s.Settings.Difficulty;
            int mode = s.Settings.Mode;
            bool ghosts = s.Settings.SeeEachOther;
            int seed = new System.Random().Next();
            s.SetPhase(SessionPhase.InGame);
            if (!StartLocal(roster, rounds, difficulty, mode, ghosts, seed, null)) return;

            var w = new PacketWriter(Msg.StartGame, s.LocalId);
            RosterIO.Write(w, roster);
            w.Write((byte)rounds);
            w.Write((byte)difficulty);
            w.Write((byte)mode);
            w.Write(ghosts);
            w.Write(seed);
            EventSpec.From(EventRandomiser.currentEvent).Write(w);
            s.Broadcast(w);
        }

        private static List<RosterEntry> BuildRoster(Session s)
        {
            var list = new List<RosterEntry>();
            foreach (var m in s.Members)
            {
                int count = Mathf.Clamp(m.LocalPlayers, 1, 4);
                for (int k = 0; k < count; k++)
                {
                    list.Add(new RosterEntry
                    {
                        Owner = m.Id,
                        Name = CleanName(m.Name, k),
                        ColorIndex = k == 0 ? m.ColorIndex : -1,
                        HatId = k == 0 ? m.HatId : null,
                    });
                }
            }
            var rng = new System.Random();
            for (int i = list.Count - 1; i > 0; i--)
            {
                int j = rng.Next(i + 1);
                var t = list[i]; list[i] = list[j]; list[j] = t;
            }
            int colors = GameManager.singleton.playerColors.Length;
            var used = new HashSet<int>();
            foreach (var e in list)
            {
                if (e.ColorIndex < 0 || e.ColorIndex >= colors || !used.Add(e.ColorIndex)) e.ColorIndex = -1;
            }
            foreach (var e in list)
            {
                if (e.ColorIndex >= 0) continue;
                var free = Enumerable.Range(0, colors).Where(c => !used.Contains(c)).ToList();
                e.ColorIndex = free.Count > 0 ? free[rng.Next(free.Count)] : rng.Next(colors);
                used.Add(e.ColorIndex);
            }
            for (int i = 0; i < list.Count; i++) list[i].PlayerNumber = i;
            return list;
        }

        public static string CleanName(string name, int localIndex = 0)
        {
            name = (name ?? "Player").Replace("<", "").Replace(">", "").Trim();
            if (name.Length == 0) name = "Player";
            if (name.Length > 16) name = name.Substring(0, 16);
            return localIndex == 0 ? name : $"{name} {localIndex + 1}";
        }

        private static bool StartLocal(List<RosterEntry> roster, int rounds, int difficulty, int mode, bool ghosts, int seed, EventSpec spec)
        {
            var gm = GameManager.singleton;
            var s = Session.Current;
            if (gm == null || s == null) return false;

            OnlineUI.OnGameStarting();
            Active = true;
            AllAtOnce = mode == 1;
            SeeEachOther = ghosts;
            GameSeed = seed;
            Roster = roster;
            DroppedPeers.Clear();
            flow.Clear();
            readyPeers.Clear();
            finishedTurns.Clear();
            early.Clear();
            roundResults.Clear();
            TurnRound = TurnPlayer = -1;
            ResetTurn();
            Application.runInBackground = true;

            Time.timeScale = gm.defaultTimeScale;
            Physics2D.gravity = gm.defaultGravity;
            GameManager.isInChallengeMode = false;
            GameManager.isInPracticeMode = false;
            GameManager.isPlayingReplay = false;
            GameManager.hasReplayBeenSkipped = false;

            TurnManager.activePlayers.Clear();
            foreach (var e in roster)
            {
                TurnManager.activePlayers.Add(new GameManager.Player
                {
                    playerNumber = e.PlayerNumber,
                    controllerID = e.PlayerNumber,
                    teamName = e.Name,
                    colorScheme = gm.playerColors[Mathf.Clamp(e.ColorIndex, 0, gm.playerColors.Length - 1)],
                    hatID = string.IsNullOrEmpty(e.HatId) ? null : e.HatId,
                    hasBeenNamed = true,
                    isLocal = e.Owner == s.LocalId,
                });
            }
            if (AllAtOnce) ReorderLocalFirst();
            TurnManager.numberOfRoundsInGame = rounds;
            GameManager.screenMode = GameManager.ScreenMode.Hotseat;
            TurnManager.GameStarted(randomiseOrder: false);
            MusicManager.StopPlaying();

            PendingEventSpec = spec;
            try
            {
                EventRandomiser.GameStarted(rounds, difficulty == 0, difficulty == 2);
            }
            finally
            {
                PendingEventSpec = null;
            }
            if (EventRandomiser.currentEvent == null || EventRandomiser.currentEvent.data == null)
            {
                Fail("Couldn't load the first event. Do you have the same events installed as the host?");
                return false;
            }
            MainMenuIdleReplayManager.enableIdleReplays = false;
            gm.StartCoroutine(GameManager.LoadSceneWithTransition(EventRandomiser.currentEvent));
            Plugin.Log.LogInfo($"Online game started: {roster.Count} players, {rounds} rounds, {(AllAtOnce ? "all at once" : "taking turns")}, first event {EventSpec.From(EventRandomiser.currentEvent)}");
            return true;
        }

        private static bool IsLocalPlayer(GameManager.Player p)
        {
            var e = Entry(p.playerNumber);
            return e != null && Session.Current != null && e.Owner == Session.Current.LocalId;
        }

        private static void ReorderLocalFirst()
        {
            var players = TurnManager.activePlayers;
            var reordered = players.Where(IsLocalPlayer).Concat(players.Where(p => !IsLocalPlayer(p))).ToList();
            players.Clear();
            players.AddRange(reordered);
        }

        public static EventConfiguration ResolveEvent(EventSpec spec)
        {
            var e = EventRandomiser.GetEventByID(spec.Id);
            if (e == null) return null;
            e.variation = spec.Variation;
            e.mutator = spec.Mutator;
            e.secondVariation = spec.SecondVariation;
            return e;
        }

        public static bool ApplySpec(EventSpec spec, int index)
        {
            var e = ResolveEvent(spec);
            if (e == null)
            {
                Fail($"The host picked an event you don't have ({spec.Id}).");
                return false;
            }
            var playlist = EventRandomiser.playlist;
            while (playlist.Count <= index) playlist.Add(e);
            playlist[index] = e;
            EventRandomiser.playlistIndex = index;
            EventRandomiser.currentEvent = e;
            EventRandomiser.currentMutator = BrandManager.GetMutatorByID(e.mutator);
            if (EventRandomiser.currentMutator != null && EventRandomiser.currentMutator.id == "NoMod") EventRandomiser.currentMutator = null;
            return true;
        }

        private static void ResetTurn()
        {
            sender = null;
            receiver?.Stop();
            receiver = null;
            SceneLoaded = false;
            TurnBegun = false;
            TurnFinished = false;
            remoteResult = null;
            remoteFinishSeen = false;
            goRecorded = false;
            RoundOverShown = false;
            simWaiting = false;
            simMerged = false;
        }

        public static void BeforeEventSceneLoad()
        {
            if (!Active) return;
            ResetTurn();
            GhostSync.OnSceneLoading();
            var cur = TurnManager.CurrentPlayer();
            if (cur == null) return;
            TurnRound = EventRandomiser.playlistIndex;
            TurnPlayer = cur.playerNumber;
            var entry = Entry(TurnPlayer);
            IsLocalTurn = entry != null && Session.Current != null && entry.Owner == Session.Current.LocalId;
            GameManager.isPlayingReplay = !IsLocalTurn;
            GameManager.hasReplayBeenSkipped = false;
            GameManager.replayType = GameManager.ReplayType.InGame;
            ReplayPlayer.hatID = cur.hatID;
            if (!IsLocalTurn) receiver = new ReplayStreamReceiver();
            InGameUI.OnTurnSceneLoading();
            Plugin.Log.LogInfo($"Turn r{TurnRound} p{TurnPlayer} ({cur.teamName}) local={IsLocalTurn}");
        }

        public static void OnEventSceneLoaded()
        {
            if (!Active) return;
            SceneLoaded = true;
            sceneLoadedTime = Time.realtimeSinceStartup;
            var s = Session.Current;
            if (s == null || AllAtOnce) return;
            MarkReady(CurrentKey, s.LocalId);
            var w = new PacketWriter(Msg.SceneReady, s.LocalId);
            w.Write((short)TurnRound);
            w.Write((short)TurnPlayer);
            s.Broadcast(w);

            if (!IsLocalTurn)
            {
                InGameUI.ShowSpectatorReady(TurnManager.CurrentPlayer());
                var mine = early.Where(b => b.Key == CurrentKey).ToList();
                early.RemoveAll(b => b.Key == CurrentKey);
                foreach (var b in mine) HandleTurnMessage(b.Type, b.Origin, new PacketReader(b.Payload, 0, b.Payload.Length));
            }
        }

        private static void MarkReady(long key, ulong peer)
        {
            if (!readyPeers.TryGetValue(key, out var set)) readyPeers[key] = set = new HashSet<ulong>();
            set.Add(peer);
        }

        public static bool AllPeersReady()
        {
            var s = Session.Current;
            if (s == null || AllAtOnce) return true;
            if (Time.realtimeSinceStartup - sceneLoadedTime > 20f) return true;
            readyPeers.TryGetValue(CurrentKey, out var set);
            foreach (var m in s.Members)
            {
                if (DroppedPeers.Contains(m.Id)) continue;
                if (set == null || !set.Contains(m.Id)) return false;
            }
            return true;
        }

        public static string WaitingForNames()
        {
            var s = Session.Current;
            if (s == null) return "";
            readyPeers.TryGetValue(CurrentKey, out var set);
            var names = s.Members.Where(m => !DroppedPeers.Contains(m.Id) && (set == null || !set.Contains(m.Id))).Select(m => m.Name).ToList();
            return string.Join(", ", names.ToArray());
        }

        public static void OnLocalTurnStarted()
        {
            if (!Active || !IsLocalTurn || TurnBegun) return;
            var s = Session.Current;
            TurnBegun = true;
            if (!AllAtOnce)
            {
                var rec = ReplayManager.recorder;
                if (rec != null && !rec.isRecording) rec.StartRecording();
                sender = new ReplayStreamSender(TurnRound, TurnPlayer);
            }

            var p = TurnManager.CurrentPlayer();
            int colorIndex = Array.IndexOf(GameManager.singleton.playerColors, p.colorScheme);
            var entry = Entry(p.playerNumber);
            if (entry != null)
            {
                entry.ColorIndex = colorIndex;
                entry.HatId = p.hatID;
                if (s?.Self != null && entry.Name == CleanName(s.Self.Name))
                {
                    Plugin.LastColorIndex.Value = colorIndex;
                    Plugin.LastHatId.Value = p.hatID ?? "";
                }
            }
            if (s == null || AllAtOnce) return;
            var w = new PacketWriter(Msg.TurnBegin, s.LocalId);
            w.Write((short)TurnRound);
            w.Write((short)TurnPlayer);
            w.Write((short)colorIndex);
            w.Write(p.hatID);
            s.Broadcast(w);
        }

        public static bool OnLocalTurnFinished(EventManager em)
        {
            if (!Active || !IsLocalTurn) return true;
            if (!TurnFinished)
            {
                TurnFinished = true;
                finishedTurns.Add(CurrentKey);
                sender?.Flush();
                GhostSync.OnLocalTurnFinished();
                var result = new TurnResultData
                {
                    Score = em.currentScore,
                    Secondary = em.currentSecondaryScore,
                    PrimaryValid = PrimaryValid(em),
                    SecondaryValid = SecondaryValid(em),
                };
                roundResults[CurrentKey] = result;
                var s = Session.Current;
                if (s != null)
                {
                    var w = new PacketWriter(Msg.TurnResult, s.LocalId);
                    w.Write((short)TurnRound);
                    w.Write((short)TurnPlayer);
                    w.Write(result.Score);
                    w.Write(result.Secondary);
                    w.Write(result.PrimaryValid);
                    w.Write(result.SecondaryValid);
                    s.Broadcast(w);
                }
            }
            if (!AllAtOnce || simMerged) return true;
            if (simWaiting) return false;
            if (!IsLastLocalTurnOfRound()) return true;
            simWaiting = true;
            Plugin.Instance.StartCoroutine(WaitForRoundResults(em));
            return false;
        }

        private static bool IsLastLocalTurnOfRound()
        {
            var players = TurnManager.activePlayers;
            for (int i = TurnManager.currentPlayerIndex + 1; i < players.Count; i++)
            {
                if (IsLocalPlayer(players[i])) return false;
            }
            return true;
        }

        private static List<GameManager.Player> PlayersStillToReport()
        {
            var players = TurnManager.activePlayers;
            var missing = new List<GameManager.Player>();
            for (int i = TurnManager.currentPlayerIndex + 1; i < players.Count; i++)
            {
                var p = players[i];
                if (!roundResults.ContainsKey(Key(TurnRound, p.playerNumber)) && !OwnerDropped(p.playerNumber)) missing.Add(p);
            }
            return missing;
        }

        private static IEnumerator WaitForRoundResults(EventManager em)
        {
            int round = TurnRound;
            var missing = PlayersStillToReport();
            while (missing.Count > 0)
            {
                InGameUI.ShowStatusBar("Waiting for " + string.Join(", ", missing.Select(p => p.teamName).ToArray()));
                yield return null;
                if (!Active || em == null || TurnRound != round) yield break;
                missing = PlayersStillToReport();
            }
            InGameUI.HideStatusBar();

            var players = TurnManager.activePlayers;
            var scores = GameManager.CurrentResult().scores;
            for (int i = TurnManager.currentPlayerIndex + 1; i < players.Count; i++)
            {
                var p = players[i];
                if (!roundResults.TryGetValue(Key(round, p.playerNumber), out var r)) r = new TurnResultData();
                scores.Add(MakeScore(em, p, r));
            }
            var current = players[TurnManager.currentPlayerIndex];
            players.RemoveAt(TurnManager.currentPlayerIndex);
            players.Add(current);
            TurnManager.currentPlayerIndex = players.Count - 1;
            simMerged = true;
            em.EventFinished();
        }

        private static EventScore MakeScore(EventManager em, GameManager.Player player, TurnResultData r)
        {
            var score = new EventScore(player, r.Score, r.PrimaryValid, r.SecondaryValid, r.Secondary);
            if (em.api.scoreManager is GlurpHorizontalDistanceScoreManager primary)
            {
                score.scoreMarkerPosition = new Vector2(primary.isScoreNegative ? -r.Score : r.Score, MainFloorY(em));
            }
            else if (em.api.secondaryScoreManager is GlurpHorizontalDistanceScoreManager secondary)
            {
                score.scoreMarkerPosition = new Vector2(secondary.isScoreNegative ? -r.Secondary : r.Secondary, MainFloorY(em));
            }
            return score;
        }

        public static void OnRoundOver()
        {
            if (Active) RoundOverShown = true;
        }

        public static void OnLocalRecordingStopping()
        {
            if (Active && IsLocalTurn) sender?.End();
        }

        public static void RecordCustom(byte code)
        {
            if (!Active || !IsLocalTurn) return;
            var rec = ReplayManager.recorder;
            var tracker = GameManager.singleton?.replayTracker;
            if (rec == null || !rec.isRecording || tracker == null) return;
            ReplayRecorder.RecordEvent(tracker.ReplayID(), code);
        }

        public static void RecordCountdown() => RecordCustom(EvCountdown);
        public static void RecordFinishText() => RecordCustom(EvFinishText);
        public static void RecordTimeUp() => RecordCustom(EvTimeUp);
        public static void RecordOutOfBounds() => RecordCustom(EvOutOfBounds);

        public static bool OnSpectatorReplayEvent(ReplayEvent ev)
        {
            if (!IsSpectating) return true;
            if (ev.eventType == ReplayEvent.Type.EventFinished)
            {
                remoteFinishSeen = true;
                return true;
            }
            byte code = (byte)ev.eventType;
            if (code < EvCountdown) return true;
            var em = GameManager.currentEvent;
            var gm = GameManager.singleton;
            switch (code)
            {
                case EvCountdown:
                    gm.countdownAnimator.enabled = true;
                    gm.countdownAnimator.ResetTrigger("Cancel");
                    gm.countdownAnimator.SetTrigger("Countdown");
                    if (em != null)
                    {
                        var arrow = StartLineArrow(em);
                        if (arrow != null) arrow.SetActive(false);
                        var anim = StartLineAnimator(em);
                        var squidge = anim != null ? anim.transform.Find("Squidge") : null;
                        squidge?.GetComponent<StartBoxSquidgeExpander>()?.StartExpanding();
                    }
                    foreach (var b in UnityEngine.Object.FindObjectsOfType<Billboard>()) b.Launch();
                    break;
                case EvGo:
                    if (em != null)
                    {
                        var anim = StartLineAnimator(em);
                        if (anim != null) anim.SetTrigger("Vanish");
                        var line = StartLine(em);
                        if (line != null) Plugin.Instance.StartCoroutine(HideLater(line.gameObject, 0.5f));
                        if (TurnManager.isFirstTurnOfRound && !MusicManager.isPlaying) MusicManager.PlayTrack(EventMusic(em));
                        HasRunOutOfTime(em) = true;
                        em.hasEventStarted = true;
                    }
                    foreach (var b in UnityEngine.Object.FindObjectsOfType<Billboard>()) b.MoveToBillboardPosition();
                    break;
                case EvFinishText:
                    gm.ShowFinishText();
                    break;
                case EvTimeUp:
                    GameManager.OnTimeUp();
                    break;
                case EvOutOfBounds:
                    GameManager.OnOutOfBounds();
                    break;
            }
            return false;
        }

        private static IEnumerator HideLater(GameObject go, float delay)
        {
            yield return new WaitForSeconds(delay);
            if (go != null) go.SetActive(false);
        }

        private static void ApplyRemoteFinish()
        {
            if (TurnFinished || remoteResult == null) return;
            var em = GameManager.currentEvent;
            if (em == null || !SceneLoaded) return;
            TurnFinished = true;
            finishedTurns.Add(CurrentKey);
            if (GameMenu.singleton.state == MenuScreen.PlayerReady) GameMenu.HidePlayerReadyMenu();
            GameMenu.HideEventInfoUI(toLeft: true);
            em.currentScore = remoteResult.Score;
            em.currentSecondaryScore = remoteResult.Secondary;
            PrimaryValid(em) = remoteResult.PrimaryValid;
            SecondaryValid(em) = remoteResult.SecondaryValid;
            em.hasEventFinished = true;
            GameManager.singleton.onScreenTimer.StopTimer();
            AllowRemoteFinish = true;
            try { em.EventFinished(); }
            finally { AllowRemoteFinish = false; }
            InGameUI.OnRemoteTurnFinished();
        }

        private static void ApplyDidNotFinish()
        {
            remoteResult = new TurnResultData { PrimaryValid = false, SecondaryValid = false };
            remoteFinishSeen = true;
            receiver?.Stop();
            ApplyRemoteFinish();
        }

        private static void OnGameMessage(Msg type, ulong origin, PacketReader r)
        {
            try
            {
                switch (type)
                {
                    case Msg.StartGame:
                        {
                            var s = Session.Current;
                            if (Active || s == null || origin != s.HostId) return;
                            var roster = RosterIO.Read(r);
                            int rounds = r.ReadByte();
                            int difficulty = r.ReadByte();
                            int mode = r.ReadByte();
                            bool ghosts = r.ReadBool();
                            int seed = r.ReadInt();
                            var spec = EventSpec.Read(r);
                            StartLocal(roster, rounds, difficulty, mode, ghosts, seed, spec);
                            break;
                        }
                    case Msg.SceneReady:
                        {
                            int round = r.ReadShort(), player = r.ReadShort();
                            MarkReady(Key(round, player), origin);
                            break;
                        }
                    case Msg.TurnResult when AllAtOnce:
                        {
                            if (!Active) return;
                            int round = r.ReadShort(), player = r.ReadShort();
                            var entry = Entry(player);
                            if (entry == null || entry.Owner != origin) return;
                            roundResults[Key(round, player)] = new TurnResultData
                            {
                                Score = r.ReadFloat(),
                                Secondary = r.ReadFloat(),
                                PrimaryValid = r.ReadBool(),
                                SecondaryValid = r.ReadBool(),
                            };
                            Toasts.Show(entry.Name + " finished");
                            break;
                        }
                    case Msg.TurnBegin:
                    case Msg.StreamChunk:
                    case Msg.TurnResult:
                    case Msg.StreamEnd:
                        {
                            if (!Active) return;
                            var payload = new byte[r.Remaining];
                            var copy = new PacketReader(CopyRemaining(r, payload), 0, payload.Length);
                            int round = copy.ReadShort(), player = copy.ReadShort();
                            long key = Key(round, player);
                            var entry = Entry(player);
                            if (entry == null || entry.Owner != origin || finishedTurns.Contains(key) && type != Msg.StreamChunk && type != Msg.StreamEnd) return;
                            if (key == CurrentKey && SceneLoaded && !IsLocalTurn)
                            {
                                HandleTurnMessage(type, origin, new PacketReader(payload, 0, payload.Length));
                            }
                            else if (round >= TurnRound)
                            {
                                early.Add(new Buffered { Type = type, Origin = origin, Key = key, Payload = payload });
                            }
                            break;
                        }
                    case Msg.Continue:
                        {
                            if (!Active || Session.Current == null || origin != Session.Current.HostId) return;
                            var cmd = new FlowCommand
                            {
                                Type = FlowType.Continue,
                                FromState = (MenuScreen)r.ReadByte(),
                                PlaylistIndex = r.ReadShort(),
                                QueuedAt = Time.realtimeSinceStartup,
                            };
                            if (r.ReadBool()) cmd.Spec = EventSpec.Read(r);
                            flow.Enqueue(cmd);
                            break;
                        }
                    case Msg.MoreRounds:
                        {
                            if (!Active || Session.Current == null || origin != Session.Current.HostId) return;
                            flow.Enqueue(new FlowCommand
                            {
                                Type = FlowType.MoreRounds,
                                Rounds = r.ReadByte(),
                                PlaylistIndex = r.ReadShort(),
                                Spec = EventSpec.Read(r),
                                QueuedAt = Time.realtimeSinceStartup,
                            });
                            break;
                        }
                    case Msg.BackToLobby:
                        if (!Active || Session.Current == null || origin != Session.Current.HostId) return;
                        flow.Enqueue(new FlowCommand { Type = FlowType.BackToLobby, QueuedAt = Time.realtimeSinceStartup });
                        break;
                    case Msg.PlayerDropped:
                        OnPlayerDropped(r.ReadULong());
                        break;
                    case Msg.GhostState:
                        GhostSync.OnGhostState(origin, r);
                        break;
                }
            }
            catch (Exception e)
            {
                Plugin.Log.LogError($"Error handling {type}: {e}");
            }
        }

        private static byte[] CopyRemaining(PacketReader r, byte[] into)
        {
            for (int i = 0; i < into.Length; i++) into[i] = r.ReadByte();
            return into;
        }

        private static void HandleTurnMessage(Msg type, ulong origin, PacketReader r)
        {
            r.ReadShort();
            r.ReadShort();
            switch (type)
            {
                case Msg.TurnBegin:
                    {
                        int colorIndex = r.ReadShort();
                        string hat = r.ReadString();
                        ApplyRemoteTurnBegin(colorIndex, hat);
                        break;
                    }
                case Msg.StreamChunk:
                    {
                        int seq = r.ReadInt();
                        bool hasHeader = r.ReadBool();
                        byte[] header = hasHeader ? r.ReadBytes() : null;
                        byte[] body = r.ReadBytes();
                        receiver?.AddChunk(seq, header, body);
                        break;
                    }
                case Msg.TurnResult:
                    remoteResult = new TurnResultData
                    {
                        Score = r.ReadFloat(),
                        Secondary = r.ReadFloat(),
                        PrimaryValid = r.ReadBool(),
                        SecondaryValid = r.ReadBool(),
                    };
                    remoteResultTime = Time.realtimeSinceStartup;
                    break;
                case Msg.StreamEnd:
                    if (receiver != null) receiver.Ended = true;
                    break;
            }
        }

        private static void ApplyRemoteTurnBegin(int colorIndex, string hat)
        {
            if (TurnBegun) return;
            TurnBegun = true;
            var p = TurnManager.CurrentPlayer();
            var gm = GameManager.singleton;
            var guy = GameManager.currentEvent != null ? GameManager.currentEvent.guy : null;
            if (colorIndex >= 0 && colorIndex < gm.playerColors.Length)
            {
                p.colorScheme = gm.playerColors[colorIndex];
                if (guy != null) guy.ApplyColorScheme(p.colorScheme);
                if (ReplayManager.player != null) ReplayManager.player.colorScheme = p.colorScheme;
            }
            if (hat != p.hatID)
            {
                p.hatID = hat;
                ReplayPlayer.hatID = hat;
                if (guy != null) gm.ApplyHat(hat, guy);
            }
            var entry = Entry(p.playerNumber);
            if (entry != null) { entry.ColorIndex = colorIndex; entry.HatId = hat; }
            InGameUI.OnRemoteTurnBegan(p);

            GameManager.isPlayingReplay = false;
            try { GameManager.ShowOnScreenUI(); }
            finally { GameManager.isPlayingReplay = true; }
        }

        private static void OnPlayerDropped(ulong peer)
        {
            if (!Active || !DroppedPeers.Add(peer)) return;
            var names = Roster.Where(e => e.Owner == peer).Select(e => e.Name).ToList();
            if (names.Count > 0) Toasts.Show(string.Join(" & ", names.ToArray()) + " left the game");
            var current = TurnManager.CurrentPlayer();
            if (current != null && !IsLocalTurn && OwnerDropped(current.playerNumber) && SceneLoaded && !TurnFinished)
            {
                ApplyDidNotFinish();
            }
            var s = Session.Current;
            if (s != null && s.Members.Count(m => !DroppedPeers.Contains(m.Id)) <= 1)
            {
                PendingPopup = "Everyone else has left the game.";
                GoBackToLobby();
            }
        }

        public static void OnNextRound()
        {
            if (!Active) return;
            TurnManager.activePlayers.RemoveAll(p => OwnerDropped(p.playerNumber));
            if (AllAtOnce) ReorderLocalFirst();
            TurnManager.currentPlayerIndex = 0;
        }

        public static bool ScoreboardIsShared => !AllAtOnce || RoundOverShown;

        public static void HostAfterScoreboardContinue(MenuScreen fromState, int playlistBefore, bool shared)
        {
            var s = Session.Current;
            if (!Active || s == null || !s.IsHost || ApplyingNetworkCommand || !shared) return;
            var w = new PacketWriter(Msg.Continue, s.LocalId);
            w.Write((byte)fromState);
            w.Write((short)EventRandomiser.playlistIndex);
            bool advanced = EventRandomiser.playlistIndex != playlistBefore;
            w.Write(advanced);
            if (advanced) EventSpec.From(EventRandomiser.currentEvent).Write(w);
            s.Broadcast(w);
        }

        public static void HostAfterMoreRounds(int playlistBefore, int rounds)
        {
            var s = Session.Current;
            if (!Active || s == null || !s.IsHost || ApplyingNetworkCommand || EventRandomiser.playlistIndex == playlistBefore) return;
            var w = new PacketWriter(Msg.MoreRounds, s.LocalId);
            w.Write((byte)rounds);
            w.Write((short)EventRandomiser.playlistIndex);
            EventSpec.From(EventRandomiser.currentEvent).Write(w);
            s.Broadcast(w);
        }

        public static bool ClientNextEvent()
        {
            if (!Active || Session.Current == null || Session.Current.IsHost) return false;
            if (PendingEventSpec == null)
            {
                Plugin.Log.LogWarning("NextEvent on a client without a host pick; falling back to local pick");
                return false;
            }
            EventRandomiser.PrepareToUnload();
            EventRandomiser.currentMutator = null;
            ApplySpec(PendingEventSpec, EventRandomiser.playlistIndex + 1);
            PendingEventSpec = null;
            return true;
        }

        private static void ProcessFlow()
        {
            if (flow.Count == 0) return;
            var cmd = flow.Peek();
            if (GlurpTransition.isTransitioning || MenuPanel.IsNavigationDisabled()) return;
            var menu = GameMenu.singleton;
            switch (cmd.Type)
            {
                case FlowType.Continue:
                    if (menu == null || menu.isPreparingToLoadScene) return;
                    if (menu.state == MenuScreen.Pause && PrePauseState(menu) == cmd.FromState)
                    {
                        GameMenu.HidePauseMenu();
                        return;
                    }
                    if (menu.state != cmd.FromState || !ScoreboardIsShared)
                    {
                        if (Time.realtimeSinceStartup - cmd.QueuedAt > 8f && IsSpectating && !TurnFinished)
                        {
                            if (remoteResult != null) ApplyRemoteFinish(); else ApplyDidNotFinish();
                        }
                        return;
                    }
                    flow.Dequeue();
                    PendingEventSpec = cmd.Spec;
                    ApplyingNetworkCommand = true;
                    try { ScoreboardContinue.Invoke(menu, null); }
                    finally { ApplyingNetworkCommand = false; PendingEventSpec = null; }
                    break;

                case FlowType.MoreRounds:
                    {
                        var gom = UnityEngine.Object.FindObjectOfType<GameOverManager>();
                        if (gom == null) return;
                        flow.Dequeue();
                        PendingEventSpec = cmd.Spec;
                        ApplyingNetworkCommand = true;
                        try
                        {
                            GameManager.AddMoreRounds(cmd.Rounds);
                            EventRandomiser.NextEvent();
                            TurnManager.NextRound();
                        }
                        finally { ApplyingNetworkCommand = false; PendingEventSpec = null; }
                        gom.bottomOptionsMenu.OnLeftTopOfStack();
                        GameManager.singleton.StartCoroutine(GameManager.LoadSceneWithTransition(EventRandomiser.currentEvent));
                        break;
                    }

                case FlowType.BackToLobby:
                    flow.Dequeue();
                    GoBackToLobby();
                    break;
            }
        }

        public static void GoBackToLobby()
        {
            var s = Session.Current;
            if (s != null && s.IsHost && Active)
            {
                s.Broadcast(new PacketWriter(Msg.BackToLobby, s.LocalId));
                s.SetPhase(SessionPhase.Lobby);
            }
            BackToLobbyPending = s != null;
            EndActive();
            GameManager.ReturnToMenu(returnToTitle: false);
        }

        public static void OnReturnToMenu()
        {
            if (!Active) return;
            EndActive();
            if (!BackToLobbyPending) Session.Current?.Leave();
        }

        private static void EndActive()
        {
            if (!Active) return;
            Active = false;
            IsLocalTurn = false;
            ResetTurn();
            flow.Clear();
            early.Clear();
            GameManager.isPlayingReplay = false;
            InGameUI.Reset();
        }

        private static void OnSessionClosed(string reason)
        {
            if (!Active) return;
            PendingPopup = reason;
            BackToLobbyPending = false;
            EndActive();
            GameManager.ReturnToMenu(returnToTitle: false);
        }

        private static void Fail(string reason)
        {
            Plugin.Log.LogError(reason);
            PendingPopup = reason;
            BackToLobbyPending = false;
            EndActive();
            Session.Current?.Leave();
            GameManager.ReturnToMenu(returnToTitle: false);
        }

        public static void Update()
        {
            if (!Active) return;
            sender?.Update();
            receiver?.Update();
            GhostSync.Update();

            float now = Time.realtimeSinceStartup;
            var em = GameManager.currentEvent;
            if (IsLocalTurn && sender != null && !goRecorded && em != null && em.hasEventStarted)
            {
                goRecorded = true;
                RecordCustom(EvGo);
            }

            if (IsSpectating && SceneLoaded && !TurnFinished)
            {
                if (remoteResult != null && (remoteFinishSeen || now - remoteResultTime > 4f || receiver == null || receiver.Broken))
                {
                    ApplyRemoteFinish();
                }
                else if (OwnerDropped(TurnPlayer) && now - sceneLoadedTime > 1.5f)
                {
                    ApplyDidNotFinish();
                }
            }

            ProcessFlow();
        }
    }
}
