using GlurpAPI;
using GlurpOnline.Net;
using GlurpOnline.UI;
using HarmonyLib;
using UnityEngine;

namespace GlurpOnline.Game
{
    [HarmonyPatch(typeof(InternalEventConfiguration), nameof(InternalEventConfiguration.LoadEventScene))]
    internal static class InternalLoadEventScenePatch
    {
        private static void Prefix() => OnlineGame.BeforeEventSceneLoad();
    }

    [HarmonyPatch(typeof(ExternalEventConfiguration), nameof(ExternalEventConfiguration.LoadEventScene))]
    internal static class ExternalLoadEventScenePatch
    {
        private static void Prefix() => OnlineGame.BeforeEventSceneLoad();
    }

    [HarmonyPatch(typeof(GameManager), nameof(GameManager.EventSceneLoaded))]
    internal static class EventSceneLoadedPatch
    {
        private static void Postfix() => OnlineGame.OnEventSceneLoaded();
    }

    [HarmonyPatch(typeof(ReplayPlayer), nameof(ReplayPlayer.ReadThenStartPlayingNextFrame))]
    internal static class ReadThenStartPlayingPatch
    {
        private static bool Prefix(ReplayPlayer __instance)
        {
            if (!OnlineGame.IsSpectating) return true;
            __instance.OnPlaybackFinished.RemoveAllListeners();
            __instance.shouldStopWhenOutOfData = false;
            __instance.shouldCatchUp = true;
            __instance.loop = false;
            var current = TurnManager.CurrentPlayer();
            if (current != null) __instance.colorScheme = current.colorScheme;
            __instance.PrepareToPlay();
            return false;
        }
    }

    [HarmonyPatch(typeof(ReplayPlayer), "Update")]
    internal static class ReplayPlayerUpdatePatch
    {
        private static bool Prefix() => !OnlineGame.Active;
    }

    [HarmonyPatch(typeof(GameManager), nameof(GameManager.ShowOnScreenUI))]
    internal static class ShowOnScreenUIPatch
    {
        private static bool Prefix() => !(OnlineGame.IsSpectating && !OnlineGame.TurnBegun);
    }

    [HarmonyPatch(typeof(GameManager), nameof(GameManager.OnReplayEvent))]
    internal static class OnReplayEventPatch
    {
        private static bool Prefix(ReplayEvent replayEvent) => OnlineGame.OnSpectatorReplayEvent(replayEvent);
    }

    [HarmonyPatch(typeof(GameManager), nameof(GameManager.AssignPlayerController))]
    internal static class AssignPlayerControllerPatch
    {
        private static bool Prefix() => !OnlineGame.IsSpectating;
    }

    [HarmonyPatch(typeof(GameManager), "OnApplicationFocus")]
    internal static class OnApplicationFocusPatch
    {
        private static bool Prefix() => !OnlineGame.Active;
    }

    [HarmonyPatch(typeof(GameManager), nameof(GameManager.RoundOver))]
    internal static class RoundOverPatch
    {
        private static void Prefix() => OnlineGame.OnRoundOver();
    }

    [HarmonyPatch(typeof(GlurpTimerScoreManager), "Update")]
    internal static class TimerScoreManagerPatch
    {
        private static bool Prefix() => !OnlineGame.IsSpectating;
    }

    [HarmonyPatch(typeof(SecondVariationSpawner), "OnReady")]
    internal static class SecondVariationSpawnerPatch
    {
        private static void Prefix(SecondVariationSpawner __instance)
        {
            var e = EventRandomiser.currentEvent;
            if (!OnlineGame.Active || e == null || !string.IsNullOrEmpty(e.secondVariation)) return;
            if (!Glurp.gameManager.IsFirstTurnOfRound() || __instance.objectPrefabs == null || __instance.objectPrefabs.Length == 0) return;
            int count = __instance.objectPrefabs.Length;
            int index = ((OnlineGame.RoundSeed(EventRandomiser.playlistIndex) % count) + count) % count;
            e.secondVariation = index.ToString();
        }
    }

    [HarmonyPatch(typeof(GlurpEvents.Targets.Target), nameof(GlurpEvents.Targets.Target.OnReplayEvent))]
    internal static class TargetRespawnPatch
    {
        private static readonly AccessTools.FieldRef<GlurpEvents.Targets.Target, SpriteRenderer> Renderer =
            AccessTools.FieldRefAccess<GlurpEvents.Targets.Target, SpriteRenderer>("spriteRenderer");

        private static void Postfix(GlurpEvents.Targets.Target __instance, ReplayEvent replayEvent)
        {
            if (!OnlineGame.IsSpectating || replayEvent.eventType != ReplayEvent.Type.Destroy || !__instance.shouldRespawnAfterHit) return;
            Plugin.Instance.StartCoroutine(Respawn(__instance));
        }

        private static System.Collections.IEnumerator Respawn(GlurpEvents.Targets.Target target)
        {
            yield return new WaitForSeconds(target.respawnDelay);
            if (target != null && Renderer(target) != null) Renderer(target).enabled = true;
        }
    }

    [HarmonyPatch(typeof(GameManager), nameof(GameManager.StartTurn))]
    internal static class StartTurnPatch
    {
        private static void Postfix() => OnlineGame.OnLocalTurnStarted();
    }

    [HarmonyPatch(typeof(ReplayRecorder), nameof(ReplayRecorder.StartRecording))]
    internal static class StartRecordingPatch
    {
        private static bool Prefix(ReplayRecorder __instance) => !(OnlineGame.Active && __instance.isRecording);
    }

    [HarmonyPatch(typeof(ReplayRecorder), nameof(ReplayRecorder.StopRecording))]
    internal static class StopRecordingPatch
    {
        private static void Prefix() => OnlineGame.OnLocalRecordingStopping();
    }

    [HarmonyPatch(typeof(EventManager), nameof(EventManager.EventFinished))]
    internal static class EventFinishedPatch
    {
        private static bool Prefix(EventManager __instance)
        {
            if (!OnlineGame.Active) return true;
            if (OnlineGame.IsLocalTurn) return OnlineGame.OnLocalTurnFinished(__instance);
            return OnlineGame.AllowRemoteFinish;
        }
    }

    [HarmonyPatch(typeof(GameManager), nameof(GameManager.StartCountdown))]
    internal static class StartCountdownPatch
    {
        private static void Postfix() => OnlineGame.RecordCountdown();
    }

    [HarmonyPatch(typeof(GameManager), nameof(GameManager.ShowFinishText))]
    internal static class ShowFinishTextPatch
    {
        private static void Postfix() => OnlineGame.RecordFinishText();
    }

    [HarmonyPatch(typeof(GameManager), nameof(GameManager.OnTimeUp))]
    internal static class OnTimeUpPatch
    {
        private static void Postfix() => OnlineGame.RecordTimeUp();
    }

    [HarmonyPatch(typeof(GameManager), nameof(GameManager.OnOutOfBounds))]
    internal static class OnOutOfBoundsPatch
    {
        private static void Postfix() => OnlineGame.RecordOutOfBounds();
    }

    [HarmonyPatch(typeof(GameMenu), nameof(GameMenu.OnStartTurnPressed))]
    internal static class OnStartTurnPressedPatch
    {
        private static bool Prefix()
        {
            if (!OnlineGame.Active) return true;
            if (!OnlineGame.IsLocalTurn) return false;
            if (!OnlineGame.AllPeersReady())
            {
                SoundEffectManager.PlayUploadFailSound();
                return false;
            }
            return true;
        }
    }

    [HarmonyPatch(typeof(GameMenu), "Update")]
    internal static class GameMenuUpdatePatch
    {
        private static bool Prefix(GameMenu __instance)
        {
            if (!OnlineGame.Active) return true;
            switch (__instance.state)
            {
                case MenuScreen.PlayerReady:
                    return OnlineGame.IsLocalTurn;
                case MenuScreen.Scoreboard:
                case MenuScreen.ScoreboardTotals:
                    return (Session.Current != null && Session.Current.IsHost) || !OnlineGame.ScoreboardIsShared;
                default:
                    return true;
            }
        }
    }

    [HarmonyPatch]
    internal static class DisabledOnlineActionsPatch
    {
        private static System.Collections.Generic.IEnumerable<System.Reflection.MethodBase> TargetMethods()
        {
            yield return AccessTools.Method(typeof(GameMenu), nameof(GameMenu.OnManagePlayersPressed));
            yield return AccessTools.Method(typeof(GameMenu), nameof(GameMenu.OnDeferTurnPressed));
            yield return AccessTools.Method(typeof(GameMenu), nameof(GameMenu.OnAddPlayerInScoreboardPressed));
            yield return AccessTools.Method(typeof(GameMenu), nameof(GameMenu.OnSetNamePressed));
        }

        private static bool Prefix() => !OnlineGame.Active;
    }

    [HarmonyPatch(typeof(GameMenu), "OnScoreboardContinuePressed")]
    internal static class OnScoreboardContinuePressedPatch
    {
        private static bool Prefix() =>
            !OnlineGame.Active || OnlineGame.ApplyingNetworkCommand || (Session.Current != null && Session.Current.IsHost) || !OnlineGame.ScoreboardIsShared;
    }

    [HarmonyPatch(typeof(GameMenu), "ScoreboardContinue")]
    internal static class ScoreboardContinuePatch
    {
        private struct State
        {
            public MenuScreen Screen;
            public int Playlist;
            public bool Shared;
        }

        private static void Prefix(GameMenu __instance, out State __state)
        {
            __state = new State { Screen = __instance.state, Playlist = EventRandomiser.playlistIndex, Shared = OnlineGame.ScoreboardIsShared };
        }

        private static void Postfix(State __state) => OnlineGame.HostAfterScoreboardContinue(__state.Screen, __state.Playlist, __state.Shared);
    }

    [HarmonyPatch(typeof(GameMenu), nameof(GameMenu.ShowPlayerReadyMenu))]
    internal static class ShowPlayerReadyMenuPatch
    {
        private static void Postfix() => InGameUI.ConfigureReadyScreen();
    }

    [HarmonyPatch]
    internal static class ScoreboardShownPatch
    {
        private static System.Collections.Generic.IEnumerable<System.Reflection.MethodBase> TargetMethods()
        {
            yield return AccessTools.Method(typeof(GameMenu), nameof(GameMenu.ShowEndOfTurnScoreboard));
            yield return AccessTools.Method(typeof(GameMenu), nameof(GameMenu.ShowEndOfRoundScoreboard));
            yield return AccessTools.Method(typeof(GameMenu), "ShowTotals");
        }

        private static void Postfix() => InGameUI.ConfigureScoreboard();
    }

    [HarmonyPatch(typeof(Popup), nameof(Popup.ShowPopup))]
    internal static class PopupTextPatch
    {
        private static void Prefix(ref string text)
        {
            if (OnlineGame.Active && text == Lean.Localization.LeanLocalization.GetTranslationText("Popup/Want to Main Menu"))
            {
                text = "Leave this online game?";
            }
        }
    }

    [HarmonyPatch(typeof(EventRandomiser), nameof(EventRandomiser.GameStarted))]
    internal static class EventRandomiserGameStartedPatch
    {
        private static void Postfix()
        {
            if (OnlineGame.Active && OnlineGame.PendingEventSpec != null) OnlineGame.ApplySpec(OnlineGame.PendingEventSpec, 0);
        }
    }

    [HarmonyPatch(typeof(EventRandomiser), nameof(EventRandomiser.NextEvent))]
    internal static class NextEventPatch
    {
        private static bool Prefix() => !OnlineGame.ClientNextEvent();
    }

    [HarmonyPatch(typeof(TurnManager), nameof(TurnManager.NextRound))]
    internal static class NextRoundPatch
    {
        private static void Postfix() => OnlineGame.OnNextRound();
    }

    [HarmonyPatch(typeof(GameManager), nameof(GameManager.ReturnToMenu))]
    internal static class ReturnToMenuPatch
    {
        private static void Prefix() => OnlineGame.OnReturnToMenu();
    }

    [HarmonyPatch(typeof(GameOverManager), "Start")]
    internal static class GameOverStartPatch
    {
        private static void Postfix(GameOverManager __instance) => InGameUI.ConfigureGameOver(__instance);
    }

    [HarmonyPatch(typeof(GameOverManager), nameof(GameOverManager.OnMainMenuPressed))]
    internal static class GameOverMainMenuPatch
    {
        private static bool Prefix()
        {
            if (!OnlineGame.Active) return true;
            if (GlurpTransition.isTransitioning || MenuPanel.IsNavigationDisabled()) return false;
            if (Session.Current != null && Session.Current.IsHost)
            {
                OnlineGame.GoBackToLobby();
            }
            else
            {
                OnlineUI.ConfirmLeaveGame();
            }
            return false;
        }
    }

    [HarmonyPatch(typeof(GameOverManager), nameof(GameOverManager.OnMoreRoundsPressed))]
    internal static class GameOverMoreRoundsPatch
    {
        private static bool Prefix() => !OnlineGame.Active || (Session.Current != null && Session.Current.IsHost);
    }

    [HarmonyPatch(typeof(GameOverManager), nameof(GameOverManager.OnMoreRoundsContinuePressed))]
    internal static class GameOverMoreRoundsContinuePatch
    {
        private static void Prefix(out int __state) => __state = EventRandomiser.playlistIndex;

        private static void Postfix(GameOverManager __instance, int __state)
        {
            OnlineGame.HostAfterMoreRounds(__state, __instance.moreRoundsNumberOfRounds.currentIndex + 1);
        }
    }

    [HarmonyPatch(typeof(MainMenuIdleReplayManager), "StartIdleReplay")]
    internal static class IdleReplayPatch
    {
        private static bool Prefix() => Session.Current == null;
    }
}
