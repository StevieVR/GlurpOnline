using GlurpAPI;
using GlurpOnline.Game;
using GlurpOnline.Net;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace GlurpOnline.UI
{
    internal static class InGameUI
    {
        private static GameObject readyWaitChip;
        private static GameObject scoreboardWaitChip;
        private static GameObject watchBar;
        private static float hideWatchBarAt = -1f;
        private static string originalReadyTitle;

        private static Transform ReadyRoot => GameMenu.singleton != null ? GameMenu.singleton.playerReadyScreen.transform : null;

        private static GameObject MakeWaitChip(Transform parent, RectTransform like, string name)
        {
            var template = ReadyRoot?.Find("Container/Bottom Menu/Defer Turn");
            if (template == null || parent == null || like == null) return null;
            var chip = UIKit.Clone(template.gameObject, parent, name);
            UIKit.SetActive(chip.transform, "Button Container", false);
            var b = chip.GetComponent<Button>();
            if (b != null) { UIKit.ClearListeners(b); b.interactable = false; }
            var hlg = chip.GetComponent<HorizontalLayoutGroup>();
            if (hlg != null) hlg.padding = new RectOffset(148, 148, 0, 0);
            var fitter = chip.GetComponent<ContentSizeFitter>() ?? chip.AddComponent<ContentSizeFitter>();
            fitter.horizontalFit = ContentSizeFitter.FitMode.PreferredSize;
            var rt = (RectTransform)chip.transform;
            rt.anchorMin = like.anchorMin;
            rt.anchorMax = like.anchorMax;
            rt.pivot = like.pivot;
            rt.anchoredPosition = like.anchoredPosition;
            chip.SetActive(false);
            return chip;
        }

        private static void SetChip(GameObject chip, string text)
        {
            if (chip == null) return;
            bool show = text != null;
            if (show)
            {
                var tmp = chip.transform.Find("Text")?.GetComponent<TMP_Text>();
                if (tmp != null && tmp.text != text.ToUpperInvariant()) UIKit.SetText(tmp, text.ToUpperInvariant());
            }
            if (chip.activeSelf != show) chip.SetActive(show);
        }

        private static void SetVisible(Component c, bool visible)
        {
            if (c == null) return;
            var group = c.GetComponent<CanvasGroup>() ?? c.gameObject.AddComponent<CanvasGroup>();
            group.alpha = visible ? 1f : 0f;
            group.blocksRaycasts = visible;
        }

        public static void ConfigureReadyScreen()
        {
            var root = ReadyRoot;
            if (root == null) return;
            bool online = OnlineGame.Active;
            bool local = !online || OnlineGame.IsLocalTurn;
            UIKit.SetActive(root, "Container/Bottom Menu/Defer Turn", !online);
            UIKit.SetActive(root, "Container/Bottom Menu/Drop In Out", !online);
            UIKit.SetActive(root, "Container/Panel/Bottom Menu/Customise Character", local);

            var title = root.Find("Title Bar/Bar/Text")?.GetComponent<TMP_Text>();
            if (title != null)
            {
                if (originalReadyTitle == null) originalReadyTitle = title.text;
                string want = local ? originalReadyTitle : "UP NEXT";
                if (title.text != want) UIKit.SetText(title, want);
            }

            if (readyWaitChip == null)
            {
                var cont = root.Find("Container/Continue") as RectTransform;
                readyWaitChip = MakeWaitChip(root.Find("Container"), cont, "Online Wait Chip");
            }
            UpdateReadyGate();
        }

        private static void UpdateReadyGate()
        {
            var menu = GameMenu.singleton;
            var root = ReadyRoot;
            if (menu == null || root == null) return;
            var arrow = root.Find("Container/Panel/Arrow Button");
            if (!OnlineGame.Active)
            {
                SetVisible(menu.playerReadyHoldToContinue, true);
                if (arrow != null) arrow.gameObject.SetActive(true);
                SetChip(readyWaitChip, null);
                return;
            }
            string wait = null;
            if (!OnlineGame.IsLocalTurn)
            {
                var p = TurnManager.CurrentPlayer();
                wait = p != null ? $"Waiting for {p.teamName}" : "Waiting";
            }
            else if (!OnlineGame.AllPeersReady())
            {
                wait = "Waiting for " + OnlineGame.WaitingForNames();
            }
            SetVisible(menu.playerReadyHoldToContinue, wait == null);
            if (arrow != null && arrow.gameObject.activeSelf != (wait == null)) arrow.gameObject.SetActive(wait == null);
            SetChip(readyWaitChip, wait);
        }

        public static void ShowSpectatorReady(GameManager.Player player)
        {
            if (GameMenu.singleton.state != MenuScreen.PlayerReady) GameMenu.ShowPlayerReadyMenu();
            else ConfigureReadyScreen();
        }

        private static bool EnsureWatchBar()
        {
            if (watchBar != null) return true;
            var menu = GameMenu.singleton;
            var bar = menu != null && menu.replayUI != null ? menu.replayUI.transform.Find("Replay Text Bar") : null;
            if (bar == null) return false;
            watchBar = UIKit.Clone(bar.gameObject, menu.replayUI.transform.parent, "Online Watch Bar");
            var anim = watchBar.GetComponent<Animator>();
            if (anim != null) Object.DestroyImmediate(anim);
            var group = watchBar.GetComponent<CanvasGroup>();
            if (group != null) group.alpha = 1f;
            var rt = (RectTransform)watchBar.transform;
            rt.anchorMin = rt.anchorMax = new Vector2(0.5f, 0f);
            rt.pivot = new Vector2(0.5f, 0f);
            rt.anchoredPosition = new Vector2(0f, 360f);
            watchBar.SetActive(false);
            return true;
        }

        public static void OnTurnSceneLoading()
        {
            if (watchBar != null) watchBar.SetActive(false);
            hideWatchBarAt = -1f;
        }

        public static void OnRemoteTurnBegan(GameManager.Player player)
        {
            if (GameMenu.singleton.state == MenuScreen.PlayerReady) GameMenu.HidePlayerReadyMenu();
            GameMenu.HideEventInfoUI(toLeft: true);
            SetChip(readyWaitChip, null);
            if (EnsureWatchBar())
            {
                UIKit.SetText(watchBar.transform, "Text", "WATCHING " + player.teamName.ToUpperInvariant());
                watchBar.SetActive(true);
                LayoutRebuilder.MarkLayoutForRebuild((RectTransform)watchBar.transform);
            }
            hideWatchBarAt = -1f;
        }

        public static void OnRemoteTurnFinished()
        {
            hideWatchBarAt = Time.unscaledTime + 2.2f;
        }

        public static void ConfigureScoreboard()
        {
            var menu = GameMenu.singleton;
            if (menu == null) return;
            var hold = menu.scoreboardHoldToContinue;
            if (scoreboardWaitChip == null && hold != null)
            {
                scoreboardWaitChip = MakeWaitChip(hold.transform.parent, (RectTransform)hold.transform, "Online Scoreboard Wait Chip");
            }
            if (!OnlineGame.Active)
            {
                SetVisible(hold, true);
                SetChip(scoreboardWaitChip, null);
                SetScoreboardArrow(true);
                return;
            }
            if (menu.addNewPlayerInScoreboardButton != null) menu.addNewPlayerInScoreboardButton.SetActive(false);
            var s = Session.Current;
            bool canContinue = (s != null && s.IsHost) || !OnlineGame.ScoreboardIsShared;
            SetVisible(hold, canContinue);
            SetChip(scoreboardWaitChip, canContinue ? null : $"Waiting for {s?.Host?.Name ?? "host"}");
            SetScoreboardArrow(canContinue);
        }

        private static void SetScoreboardArrow(bool visible)
        {
            var arrow = GameMenu.singleton.scoreboardScreen.GetComponentInChildren<UIArrowButton>(true);
            if (arrow != null && arrow.gameObject.activeSelf != visible) arrow.gameObject.SetActive(visible);
        }

        public static void ShowStatusBar(string text)
        {
            if (!EnsureWatchBar()) return;
            var label = watchBar.transform.Find("Text")?.GetComponent<TMP_Text>();
            string upper = text.ToUpperInvariant();
            if (label != null && label.text != upper)
            {
                UIKit.SetText(label, upper);
                LayoutRebuilder.MarkLayoutForRebuild((RectTransform)watchBar.transform);
            }
            if (!watchBar.activeSelf) watchBar.SetActive(true);
            hideWatchBarAt = -1f;
        }

        public static void HideStatusBar()
        {
            if (watchBar != null) watchBar.SetActive(false);
        }

        public static void ConfigureGameOver(GameOverManager gom)
        {
            if (!OnlineGame.Active || gom == null || gom.bottomOptionsMenu == null) return;
            bool isHost = Session.Current != null && Session.Current.IsHost;
            foreach (var icon in gom.bottomOptionsMenu.GetComponentsInChildren<GlurpControllerButtonIcon>(true))
            {
                var chip = icon.GetComponentInParent<Button>();
                if (chip == null) continue;
                if (icon.buttonType == GlurpControllerManager.ButtonLocation.West)
                {
                    UIKit.SetText(chip.transform, "Text", isHost ? "BACK TO LOBBY" : "LEAVE GAME");
                    LayoutRebuilder.MarkLayoutForRebuild((RectTransform)chip.transform);
                }
                else if (icon.buttonType == GlurpControllerManager.ButtonLocation.North)
                {
                    chip.gameObject.SetActive(isHost);
                }
            }
            if (!isHost) Toasts.Show("Waiting for the host: more rounds or back to the lobby");
        }

        public static void Update()
        {
            var menu = GameMenu.singleton;
            if (menu == null) return;
            if (OnlineGame.Active && menu.state == MenuScreen.PlayerReady) UpdateReadyGate();
            if (hideWatchBarAt > 0f && Time.unscaledTime > hideWatchBarAt)
            {
                hideWatchBarAt = -1f;
                if (watchBar != null) watchBar.SetActive(false);
            }
        }

        public static void Reset()
        {
            if (GameMenu.singleton == null) return;
            ConfigureReadyScreen();
            ConfigureScoreboard();
            if (watchBar != null) watchBar.SetActive(false);
            hideWatchBarAt = -1f;
        }
    }
}
