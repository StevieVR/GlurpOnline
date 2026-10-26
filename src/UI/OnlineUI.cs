using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using GlurpAPI;
using GlurpOnline.Game;
using GlurpOnline.Net;
using GlurpOnline.Update;
using HarmonyLib;
using Steamworks;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace GlurpOnline.UI
{
    internal static class OnlineUI
    {
        private const float Skew = 0.19f;
        private static readonly string[] DifficultyNames = { "BEGINNER", "INTERMEDIATE", "ADVANCED" };
        private static readonly string[] PrivacyNames = { "FRIENDS ONLY", "PUBLIC", "INVITE ONLY" };
        private static readonly string[] ModeNames = { "TAKE TURNS", "ALL AT ONCE" };
        private static readonly string[] SharingNames = { "NONE", "PROPS", "EVERYTHING" };
        private static readonly string[] OffOn = { "OFF", "ON" };
        private const float OptionTop = 1110f;
        private const float OptionSpacing = 330f;
        private const float OptionX = 1560f;
        private const float OptionScale = 0.76f;

        private static readonly System.Reflection.MethodInfo ShowMenuMethod = AccessTools.Method(typeof(MainMenu), "ShowMenu");
        private static readonly System.Reflection.MethodInfo PopMenuMethod = AccessTools.Method(typeof(MainMenu), "PopMenu");
        private static readonly AccessTools.FieldRef<MainMenu, Stack<MenuPanel>> MenuStack =
            AccessTools.FieldRefAccess<MainMenu, Stack<MenuPanel>>("menuStack");
        private static readonly AccessTools.FieldRef<MainMenu, int> CameraIndex =
            AccessTools.FieldRefAccess<MainMenu, int>("cameraMoveIndex");

        private static MainMenu menu;
        private static MenuPanel hubPanel, lobbyPanel, browserPanel, directPanel;

        private sealed class PlayerRow
        {
            public RectTransform Root;
            public Button Button;
            public TMP_Text Name;
            public Image LeftColor, RightColor;
            public Image TagImage;
            public TMP_Text Tag;
            public ulong MemberId;
        }

        private static readonly List<PlayerRow> playerRows = new List<PlayerRow>();
        private static UIHorizontalOptions optRounds, optDifficulty, optPrivacy, optMax, optLocal;
        private static UIButtonOption gameSettingsRow;
        private static readonly List<Component> lobbyRows = new List<Component>();

        private static MenuPanel gameSettingsPanel;
        private static UIHorizontalOptions optMode, optGhosts, optSharing, optCollide;
        private static readonly List<UIHorizontalOptions> gameOptions = new List<UIHorizontalOptions>();
        private static TMP_Text gameSettingsInfo, gameSettingsInfo2;
        private static UIArrowButton lobbyArrow;
        private static TMP_Text lobbyStatus, lobbyHint, lobbyModeTab;
        private static Transform lobbyLeaveChip, lobbyInviteChip, lobbyCustomiseChip;
        private static bool suppressOptionEvents;

        private sealed class Listing
        {
            public CSteamID Id;
            public string Name, Version;
            public int Players, Max;
            public bool Playing, Friend, Compatible = true;
        }

        private static readonly List<UIButtonOption> browserRows = new List<UIButtonOption>();
        private static readonly List<Listing> listings = new List<Listing>();
        private static TMP_Text browserStatus;
        private static bool searching;
        private static float nextBrowserRefresh;
        private static CallResult<LobbyMatchList_t> lobbyListCall;
        private static Callback<LobbyDataUpdate_t> lobbyDataCallback;
        private static Callback<GameLobbyJoinRequested_t> joinRequestedCallback;

        private static UIButtonOption directAddressRow, directHostRow;
        private static UIArrowButton directJoinArrow;
        private static TMP_Text directStatus, directHint;
        private static bool keyboardOpen;

        private static bool customising;
        private static CSteamID pendingJoin = CSteamID.Nil;
        private static bool commandLineChecked;
        private static float nextLobbyRefresh;
        private static bool initialised;

        public static void Init()
        {
            if (initialised) return;
            initialised = true;
            Session.LobbyChanged += () => { if (lobbyPanel != null) RefreshLobby(); };
            Session.SessionJoined += () => { if (lobbyPanel != null) RefreshLobby(); };
            Session.SessionClosed += OnSessionClosed;
            Session.MemberJoined += m => { if (Session.Current != null && m.Id != Session.Current.LocalId) Toasts.Show(m.Name + " joined"); };
            Session.MemberLeft += m => { if (!OnlineGame.Active) Toasts.Show(m.Name + " left"); };
            LanDiscovery.Changed += () => { if (menu != null && InStack(directPanel)) RefreshDirect(); };
        }

        public static void OnMainMenuStart(MainMenu m)
        {
            menu = m;
            playerRows.Clear();
            browserRows.Clear();
            customising = false;
            keyboardOpen = false;
            try
            {
                BuildMainMenuCard();
                BuildHub();
                BuildLobby();
                BuildGameSettings();
                BuildBrowser();
                BuildDirect();
                BuildInvite();
            }
            catch (Exception e)
            {
                Plugin.Log.LogError("Failed to build online menus: " + e);
            }
            Plugin.Instance.StartCoroutine(AfterMainMenuStart());
        }

        private static IEnumerator AfterMainMenuStart()
        {
            yield return new WaitForSecondsRealtime(0.6f);
            if (menu == null) yield break;
            if (OnlineGame.BackToLobbyPending)
            {
                OnlineGame.BackToLobbyPending = false;
                if (Session.Current != null) ShowLobby();
            }
            if (OnlineGame.PendingPopup != null)
            {
                var text = OnlineGame.PendingPopup;
                OnlineGame.PendingPopup = null;
                yield return new WaitForSecondsRealtime(0.4f);
                Info(text);
            }
            HandleConnectString(Updater.TakeRejoin());
            Updater.OnMainMenu();
        }

        private static void ShowMenu(MenuPanel panel) => ShowMenuMethod.Invoke(menu, new object[] { panel, true });
        private static void PopMenu() => PopMenuMethod.Invoke(menu, null);
        private static MenuPanel Top() => menu != null && MenuStack(menu).Count > 0 ? MenuStack(menu).Peek() : null;
        private static bool InStack(MenuPanel panel) => menu != null && panel != null && MenuStack(menu).Contains(panel);

        private static void PopTo(MenuPanel panel)
        {
            if (!InStack(panel)) return;
            var stack = MenuStack(menu);
            while (stack.Count > 0 && stack.Peek() != panel) PopMenu();
            if (stack.Count > 0) PopMenu();
        }

        private static bool MenuIsIdle() => menu != null && !GlurpTransition.isTransitioning && !MenuPanel.IsNavigationDisabled();

        public static void Info(string text)
        {
            Popup.ShowPopup(text, new[] { "OK" }, 0, 1154f, null, _ => Popup.HidePopup());
        }

        private static readonly AccessTools.FieldRef<Popup, MenuPanel> PopupPanel = AccessTools.FieldRefAccess<Popup, MenuPanel>("menuPanel");

        public static bool CanPrompt(bool allowInSession)
        {
            if (menu == null || GameManager.isPlaying || OnlineGame.Active || !MenuIsIdle() || customising || keyboardOpen) return false;
            if (Popup.singleton == null || PopupShowing() || popLobbyWhenIdle) return false;
            return allowInSession || Session.Current == null;
        }

        private static bool PopupShowing()
        {
            var panel = Popup.singleton != null ? PopupPanel(Popup.singleton) : null;
            return panel != null && panel.isShowing;
        }

        public static void Confirm(string text, string no, string yes, Action onYes)
        {
            Popup.ShowPopup(text, new[] { no, yes }, 0, 1154f, null, submit =>
            {
                Popup.HidePopup();
                if (submit) onYes();
            });
        }

        private static MenuPanel CloneLocalMenu(string name, string modeTab, Vector2 panelSize, float panelY, out RectTransform content)
        {
            var src = menu.localPlayMenuScreen;
            var panel = UIKit.Clone(src, src.transform.parent, name);
            panel.menuState = MenuScreen.Online;
            panel.isShowing = false;

            var root = panel.transform;
            var panelRect = (RectTransform)root.Find("Container/Panel");
            var oldSize = panelRect.sizeDelta;
            var oldPos = panelRect.anchoredPosition;
            panelRect.sizeDelta = panelSize;
            panelRect.anchoredPosition = new Vector2(oldPos.x, panelY);
            var underline = (RectTransform)panelRect.Find("Underline");
            if (underline != null)
            {
                float lip = underline.anchoredPosition.y - underline.sizeDelta.y / 2f;
                float height = underline.sizeDelta.y * panelSize.y / oldSize.y;
                underline.sizeDelta = new Vector2(underline.sizeDelta.x, height);
                underline.anchoredPosition = new Vector2(underline.anchoredPosition.x, lip + height / 2f);
            }
            var title = (RectTransform)root.Find("Title Bar");
            title.anchoredPosition += new Vector2(-(panelSize.x - oldSize.x) / 2f, (panelY + panelSize.y / 2f) - (oldPos.y + oldSize.y / 2f));

            UIKit.SetText(root, "Title Bar/Bars/Party/Text", "ONLINE");
            UIKit.SetText(root, "Title Bar/Bars/Mode/Text", modeTab);

            var contentGo = new GameObject("Online Content", typeof(RectTransform));
            content = (RectTransform)contentGo.transform;
            content.SetParent(panelRect, false);
            content.anchorMin = content.anchorMax = new Vector2(0.5f, 0.5f);
            content.sizeDelta = Vector2.zero;
            content.anchoredPosition = Vector2.zero;

            UIKit.ConfigureChip(root.Find("Container/Bottom Menu/Back"), GlurpControllerManager.ButtonLocation.East, "BACK", () => menu.OnBackPressed());
            return panel;
        }

        private static Transform LocalMenuTemplate(string path) => menu.localPlayMenuScreen.transform.Find(path);

        private static UIHorizontalOptions MakeOption(RectTransform parent, string label, Vector2 pos, float scale, bool wide)
        {
            var src = LocalMenuTemplate("Container/Panel/Column/Row/Container/Players").GetComponent<UIHorizontalOptions>();
            var opt = UIKit.Clone(src, parent, label);
            var rt = (RectTransform)opt.transform;
            rt.anchorMin = rt.anchorMax = new Vector2(0.5f, 0.5f);
            rt.pivot = new Vector2(0.5f, 0.5f);
            rt.sizeDelta = new Vector2(3992f, 296f);
            rt.localScale = new Vector3(scale, scale, 1f);
            rt.anchoredPosition = pos + new Vector2(pos.y * Skew, 0f);
            UIKit.SetText(opt.transform, "Label", label);
            if (wide)
            {
                const float width = 1150f;
                var window = (RectTransform)opt.transform.Find("Value Window");
                var left = (RectTransform)opt.leftButton.transform;
                var right = (RectTransform)opt.rightButton.transform;
                float rightGap = right.anchoredPosition.x - (window.anchoredPosition.x + window.sizeDelta.x / 2f);
                float leftGap = window.anchoredPosition.x - window.sizeDelta.x / 2f - left.anchoredPosition.x;
                float windowX = right.anchoredPosition.x - rightGap - width / 2f;
                window.sizeDelta = new Vector2(width, window.sizeDelta.y);
                window.anchoredPosition = new Vector2(windowX, window.anchoredPosition.y);
                left.anchoredPosition = new Vector2(windowX - width / 2f - leftGap, left.anchoredPosition.y);
                var value = (RectTransform)window.Find("Value Label");
                value.sizeDelta = new Vector2(width - 150f, value.sizeDelta.y);
                var tmp = value.GetComponent<TMP_Text>();
                tmp.enableAutoSizing = true;
                tmp.fontSizeMin = 60f;
                tmp.fontSizeMax = 140f;
            }
            opt.OnValueChanged.RemoveAllListeners();
            UIKit.OnClick(opt.leftButton, () => { if (opt.GetComponent<Selectable>().interactable) opt.OnLeftButtonPressed(); });
            UIKit.OnClick(opt.rightButton, () => { if (opt.GetComponent<Selectable>().interactable) opt.OnRightButtonPressed(); });
            return opt;
        }

        private static void SetOptionValues(UIHorizontalOptions opt, int min, int max)
        {
            opt.useIntegerValues = true;
            opt.localiseOptionNames = false;
            opt.minValue = min;
            opt.maxValue = max;
            opt.RefreshOptions();
        }

        private static void SetOptionNames(UIHorizontalOptions opt, IEnumerable<string> names)
        {
            opt.useIntegerValues = false;
            opt.localiseOptionNames = false;
            opt.SetOptionNames(names.ToList());
        }

        private static void SetOptionIndex(UIHorizontalOptions opt, int index)
        {
            if (opt == null || opt.optionNames.Count == 0) return;
            index = Mathf.Clamp(index, 0, opt.optionNames.Count - 1);
            if (opt.currentIndex != index) opt.SelectOption(index);
        }

        private static void SetOptionEditable(UIHorizontalOptions opt, bool editable)
        {
            if (opt == null) return;
            var sel = opt.GetComponent<Selectable>();
            if (sel != null) sel.interactable = editable;
            opt.leftButton.gameObject.SetActive(editable);
            opt.rightButton.gameObject.SetActive(editable);
        }

        private static UIArrowButton MakeArrow(RectTransform parent, string label, Vector2 pos)
        {
            var src = LocalMenuTemplate("Container/Panel/Column/Row (3)/Container/Arrow").GetComponent<UIArrowButton>();
            var arrow = UIKit.Clone(src, parent, "Arrow " + label);
            var rt = (RectTransform)arrow.transform;
            rt.anchorMin = rt.anchorMax = new Vector2(0.5f, 0.5f);
            rt.pivot = new Vector2(1f, 0.5f);
            rt.anchoredPosition = pos;
            UIKit.SetText(arrow.transform, "Label", label);
            UIKit.ClearListeners(arrow);
            return arrow;
        }

        private static TMP_Text MakeLine(RectTransform parent, Vector2 pos, float width)
        {
            var src = LocalMenuTemplate("Container/Panel/Column/Row (3)/Container/Line 1").GetComponent<TMP_Text>();
            var line = UIKit.Clone(src, parent, "Line");
            line.overflowMode = TextOverflowModes.Overflow;
            line.enableWordWrapping = true;
            var rt = line.rectTransform;
            rt.anchorMin = rt.anchorMax = new Vector2(0.5f, 0.5f);
            rt.pivot = new Vector2(0f, 1f);
            rt.sizeDelta = new Vector2(width, 120f);
            rt.anchoredPosition = pos + new Vector2(pos.y * Skew, 0f);
            UIKit.SetText(line, "");
            return line;
        }

        private static UIButtonOption MakeButtonRow(RectTransform parent, string name, Vector2 pos, float width)
        {
            var src = UIKit.FindDeep(menu.fullSettingsMenuScreen.transform, "Configure Controllers")?.GetComponent<UIButtonOption>();
            if (src == null) throw new Exception("button row template missing");
            var row = UIKit.Clone(src, parent, name);
            var rt = (RectTransform)row.transform;
            rt.anchorMin = rt.anchorMax = new Vector2(0.5f, 0.5f);
            rt.pivot = new Vector2(0.5f, 0.5f);
            rt.sizeDelta = new Vector2(width, 296f);
            rt.anchoredPosition = pos + new Vector2(pos.y * Skew, 0f);
            UIKit.ClearListeners(row);
            return row;
        }

        private static void SetButtonRow(UIButtonOption row, string label, string value)
        {
            UIKit.SetText(row.transform, "Label", label);
            UIKit.SetText(row.transform, "Value Window/Value Label", value);
        }

        private static void BuildMainMenuCard()
        {
            var local = menu.menuOptions[0];
            var challenge = menu.menuOptions.Length > 2 ? menu.menuOptions[2] : null;
            if (local == null || challenge == null) return;
            var row = local.transform.parent;
            if (row.Find("Online") != null) return;
            LayoutRebuilder.ForceRebuildLayoutImmediate((RectTransform)row);
            float cardY = ((RectTransform)local.transform).anchoredPosition.y;

            var card = UIKit.Clone(local, row, "Online");
            card.transform.SetSiblingIndex(local.transform.GetSiblingIndex() + 1);
            UIKit.SetText(card.transform, "Title Container/Title Bar/Text", "ONLINE");
            UIKit.SetText(card.transform, "Description/Text", "2-16 Players");
            var underline = card.transform.Find("Panel/Underline")?.GetComponent<Image>();
            if (underline != null) underline.color = UIKit.Pink;
            var iconBar = card.transform.Find("Title Container/Title Bar/Icon Bar/Icon Bar (1)")?.GetComponent<Image>();
            if (iconBar != null) iconBar.color = UIKit.Pink;
            var icon = card.transform.Find("Title Container/Title Bar/Icon Bar/Icon Bar (1)/Image")?.GetComponent<Image>();
            if (icon != null)
            {
                icon.sprite = Glyphs.Online;
                icon.preserveAspect = true;
                var iconRect = (RectTransform)icon.transform;
                iconRect.sizeDelta = new Vector2(190f, 190f);
                iconRect.anchoredPosition = new Vector2(2f, 14f);
            }
            UIKit.OnClick(card, OpenHub);

            var layout = row.GetComponent<HorizontalLayoutGroup>();
            if (layout != null) layout.enabled = false;
            float width = ((RectTransform)row).rect.width;
            if (width < 100f) width = 7620f;
            const float scale = 0.72f;
            PlaceCard(local, width * (1f / 6f) - 928f * scale + 60f, cardY, scale);
            PlaceCard(card, width * (3f / 6f) - 928f * scale, cardY, scale);
            PlaceCard(challenge, width * (5f / 6f) - 708f * scale - 60f, cardY, scale);

            var localNav = local.navigation;
            localNav.selectOnRight = card;
            local.navigation = localNav;
            var cardNav = localNav;
            cardNav.selectOnLeft = local;
            cardNav.selectOnRight = challenge;
            card.navigation = cardNav;
            var challengeNav = challenge.navigation;
            challengeNav.selectOnLeft = card;
            challenge.navigation = challengeNav;
            menu.menuOptions[1] = card;
        }

        private static void PlaceCard(Component card, float x, float y, float scale)
        {
            var rt = (RectTransform)card.transform;
            rt.anchoredPosition = new Vector2(x, y);
            rt.localScale = new Vector3(scale, scale, 1f);
        }

        private static void BuildHub()
        {
            var src = menu.localModeMenuScreen;
            hubPanel = UIKit.Clone(src, src.transform.parent, "Online Mode Menu");
            hubPanel.menuState = MenuScreen.Online;
            hubPanel.isShowing = false;
            var rowT = hubPanel.transform.Find("Container/Row");
            ConfigureHubCard(rowT.Find("Beginner"), "HOST GAME", "Start a lobby and invite your friends.", UIKit.Mint, Glyphs.Host, HostSteamGame);
            ConfigureHubCard(rowT.Find("Intermediate"), "FIND GAMES", "Join friends or public games.", UIKit.Purple, Glyphs.Find, OpenBrowser);
            ConfigureHubCard(rowT.Find("Advanced"), "DIRECT CONNECT", "Play over LAN or by IP address.", UIKit.Pink, Glyphs.Direct, OpenDirect);
            UIKit.SetActive(rowT, "Custom", false);
            UIKit.ConfigureChip(hubPanel.transform.Find("Container/Bottom Menu/Back"), GlurpControllerManager.ButtonLocation.East, "BACK", () => menu.OnBackPressed());
        }

        private static void ConfigureHubCard(Transform card, string title, string description, Color color, Sprite glyph, Action onClick)
        {
            if (card == null) return;
            UIKit.SetText(card, "Panel/Mode Text/Text", title);
            UIKit.SetText(card, "Panel/Line 1", description);
            var underline = card.Find("Panel/Underline")?.GetComponent<Image>();
            if (underline != null) underline.color = color;
            var medal = card.Find("Panel/Medal")?.GetComponent<Image>();
            if (medal != null)
            {
                medal.sprite = glyph;
                medal.preserveAspect = true;
                ((RectTransform)medal.transform).sizeDelta = new Vector2(440f, 440f);
                medal.color = new Color(color.r, color.g, color.b, medal.color.a);
            }
            var button = card.GetComponent<Button>();
            if (button != null) UIKit.OnClick(button, onClick);
        }

        private static void OpenHub()
        {
            if (!MenuIsIdle() || menu.state != MenuScreen.MainMenu) return;
            menu.mainMenuScreen.SetDefaultOption(menu.menuOptions[1]);
            ShowMenu(hubPanel);
        }

        private static LobbySettings SavedSettings() => new LobbySettings
        {
            Rounds = Mathf.Clamp(Plugin.LobbyRounds.Value, 1, 20),
            Difficulty = Mathf.Clamp(Plugin.LobbyDifficulty.Value, 0, 2),
            Privacy = Mathf.Clamp(Plugin.LobbyPrivacy.Value, 0, 2),
            MaxPlayers = Mathf.Clamp(Plugin.LobbyMaxPlayers.Value, 2, 16),
            Mode = Mathf.Clamp(Plugin.LobbyMode.Value, 0, 1),
            SeeEachOther = Plugin.LobbySeeEachOther.Value,
            Sharing = Mathf.Clamp(Plugin.LobbySharing.Value, 0, 2),
            Collide = Plugin.LobbyCollide.Value,
        };

        private static void HostSteamGame()
        {
            if (!MenuIsIdle()) return;
            if (!Session.SteamAvailable)
            {
                Info("Steam isn't running, so Steam lobbies aren't available. Use Direct Connect instead.");
                return;
            }
            Session.HostSteam(SavedSettings());
            ShowLobby();
        }

        private static void BuildLobby()
        {
            lobbyPanel = CloneLocalMenu("Online Lobby Menu", "LOBBY", new Vector2(6700f, 2950f), 330f, out var content);
            var root = lobbyPanel.transform;
            lobbyModeTab = root.Find("Title Bar/Bars/Mode/Text")?.GetComponent<TMP_Text>();

            var nameTemplate = GameMenu.singleton.playerReadyScreen.transform.Find("Container/Panel/Name Panel");
            var tagTemplate = LocalMenuTemplate("Container/Panel/Column/Row/Container/Players/Value Window");
            var colors = LocalMenuTemplate("Container/Panel/Column/Row/Container/Players").GetComponent<Selectable>().colors;
            for (int i = 0; i < 8; i++)
            {
                var go = UIKit.Clone(nameTemplate.gameObject, content, "Player " + i);
                var rt = (RectTransform)go.transform;
                rt.anchorMin = rt.anchorMax = new Vector2(0.5f, 0.5f);
                rt.pivot = new Vector2(0.5f, 0.5f);
                rt.sizeDelta = new Vector2(2800f, 296f);
                rt.localScale = new Vector3(0.92f, 0.92f, 1f);
                float y = 1110f - i * 300f;
                rt.anchoredPosition = new Vector2(-1720f + y * Skew, y);

                var text = (RectTransform)go.transform.Find("Text");
                text.offsetMin = new Vector2(700f, text.offsetMin.y);
                text.offsetMax = new Vector2(-380f, text.offsetMax.y);
                var left = (RectTransform)go.transform.Find("Left Color");
                var right = (RectTransform)go.transform.Find("Right Color");
                left.anchoredPosition = new Vector2(1400f - 270f, left.anchoredPosition.y);
                right.anchoredPosition = new Vector2(1400f - 155f, right.anchoredPosition.y);

                var tag = UIKit.Clone(tagTemplate.gameObject, go.transform, "Tag");
                var tagRt = (RectTransform)tag.transform;
                tagRt.anchorMin = tagRt.anchorMax = new Vector2(0f, 0.5f);
                tagRt.pivot = new Vector2(0f, 0.5f);
                tagRt.sizeDelta = new Vector2(640f, 296f);
                tagRt.anchoredPosition = new Vector2(30f, 0f);
                var tagLabel = tag.transform.Find("Value Label").GetComponent<TMP_Text>();
                tagLabel.enableWordWrapping = false;
                tagLabel.enableAutoSizing = true;
                tagLabel.fontSizeMin = 50f;
                tagLabel.fontSizeMax = 110f;
                ((RectTransform)tagLabel.transform).sizeDelta = new Vector2(560f, 250f);

                var button = go.AddComponent<Button>();
                button.targetGraphic = go.GetComponent<Image>();
                button.colors = colors;
                var rowData = new PlayerRow
                {
                    Root = rt,
                    Button = button,
                    Name = go.transform.Find("Text").GetComponent<TMP_Text>(),
                    LeftColor = left.GetComponent<Image>(),
                    RightColor = right.GetComponent<Image>(),
                    TagImage = tag.GetComponent<Image>(),
                    Tag = tagLabel,
                };
                UIKit.OnClick(button, () => OnPlayerRowClicked(rowData));
                playerRows.Add(rowData);
            }

            lobbyRows.Clear();
            optRounds = MakeOption(content, "ROUNDS", Vector2.zero, OptionScale, false);
            SetOptionValues(optRounds, 1, 20);
            optDifficulty = MakeOption(content, "DIFFICULTY", Vector2.zero, OptionScale, true);
            SetOptionNames(optDifficulty, DifficultyNames);
            gameSettingsRow = MakeButtonRow(content, "Game Settings", Vector2.zero, 3992f);
            gameSettingsRow.transform.localScale = new Vector3(OptionScale, OptionScale, 1f);
            SetButtonRow(gameSettingsRow, "GAME SETTINGS", "");
            UIKit.SetActive(gameSettingsRow.transform, "Value Window", false);
            UIKit.OnClick(gameSettingsRow, OpenGameSettings);
            optPrivacy = MakeOption(content, "LOBBY", Vector2.zero, OptionScale, true);
            SetOptionNames(optPrivacy, PrivacyNames);
            optMax = MakeOption(content, "MAX PLAYERS", Vector2.zero, OptionScale, false);
            SetOptionValues(optMax, 2, 16);
            optLocal = MakeOption(content, "PLAYERS ON THIS PC", Vector2.zero, OptionScale, false);
            SetOptionValues(optLocal, 1, 4);
            lobbyRows.AddRange(new Component[] { optRounds, optDifficulty, gameSettingsRow, optPrivacy, optMax, optLocal });
            foreach (var opt in new[] { optRounds, optDifficulty, optPrivacy, optMax }) opt.OnValueChanged.AddListener(_ => OnSettingsEdited());
            optLocal.OnValueChanged.AddListener(v =>
            {
                if (!suppressOptionEvents) Session.Current?.UpdateSelf(m => m.LocalPlayers = v + 1);
            });

            lobbyArrow = MakeArrow(content, "LET'S GO!", new Vector2(3000f, -930f));
            UIKit.OnClick(lobbyArrow, OnLobbyArrow);
            lobbyStatus = MakeLine(content, new Vector2(150f, -1150f), 3000f);
            lobbyHint = MakeLine(content, new Vector2(150f, -1270f), 3000f);
            foreach (var line in new[] { lobbyStatus, lobbyHint })
            {
                line.enableWordWrapping = false;
                line.overflowMode = TextOverflowModes.Ellipsis;
            }

            UIKit.SetActive(root, "Container/Panel/Column", false);
            lobbyLeaveChip = root.Find("Container/Bottom Menu/Back");
            UIKit.ConfigureChip(lobbyLeaveChip, GlurpControllerManager.ButtonLocation.East, "LEAVE", () => menu.OnBackPressed());
            lobbyInviteChip = root.Find("Container/Bottom Menu/Options");
            UIKit.ConfigureChip(lobbyInviteChip, GlurpControllerManager.ButtonLocation.North, "INVITE", Invite);
            lobbyCustomiseChip = UIKit.Clone(lobbyInviteChip.gameObject, lobbyInviteChip.parent, "Customise").transform;
            UIKit.ConfigureChip(lobbyCustomiseChip, GlurpControllerManager.ButtonLocation.West, "CUSTOMISE", BeginCustomise);
        }

        private static void ShowLobby()
        {
            if (lobbyPanel == null || menu == null) return;
            if (Top() != lobbyPanel)
            {
                if (InStack(lobbyPanel)) PopTo(lobbyPanel);
                ShowMenu(lobbyPanel);
            }
            RefreshLobby();
        }

        private static void RefreshLobby()
        {
            if (lobbyPanel == null) return;
            var s = Session.Current;
            nextLobbyRefresh = Time.unscaledTime + 1f;
            bool connected = s != null && s.Phase != SessionPhase.Connecting;
            bool isHost = connected && s.IsHost;
            var members = connected ? s.Members : new List<Member>();
            int colorCount = GameManager.singleton.playerColors.Length;

            string mode;
            if (s == null) mode = "OFFLINE";
            else if (!connected) mode = s.Creating ? "CREATING…" : "JOINING…";
            else if (s.Kind == SessionKind.Direct) mode = "DIRECT";
            else mode = PrivacyNames[Mathf.Clamp(s.Settings.Privacy, 0, 2)];
            if (lobbyModeTab != null && lobbyModeTab.text != mode)
            {
                UIKit.SetText(lobbyModeTab, mode);
                LayoutRebuilder.MarkLayoutForRebuild((RectTransform)lobbyModeTab.transform.parent);
            }

            int slots = connected ? Mathf.Min(8, Math.Max(members.Count, s.Settings.MaxPlayers)) : 0;
            for (int i = 0; i < playerRows.Count; i++)
            {
                var row = playerRows[i];
                bool visible = i < slots;
                if (row.Root.gameObject.activeSelf != visible) row.Root.gameObject.SetActive(visible);
                if (!visible) continue;
                if (i < members.Count && !(i == 7 && members.Count > 8))
                {
                    var m = members[i];
                    row.MemberId = m.Id;
                    string name = OnlineGame.CleanName(m.Name);
                    if (m.LocalPlayers > 1) name += $" +{m.LocalPlayers - 1}";
                    if (m.PingMs >= 0 && !m.IsHost) name += $"<size=65%><color=#9a9a9a>  {m.PingMs}MS</color></size>";
                    if (row.Name.text != name) UIKit.SetText(row.Name, name);
                    var scheme = m.ColorIndex >= 0 && m.ColorIndex < colorCount ? GameManager.singleton.playerColors[m.ColorIndex] : null;
                    row.LeftColor.color = scheme != null ? scheme.secondary : UIKit.Grey;
                    row.RightColor.color = scheme != null ? scheme.primary : UIKit.Grey;
                    row.LeftColor.enabled = row.RightColor.enabled = true;
                    string tag = m.IsHost ? "HOST" : m.Ready ? "READY" : "NOT READY";
                    if (row.Tag.text != tag) UIKit.SetText(row.Tag, tag);
                    row.TagImage.color = m.IsHost ? UIKit.Pink : m.Ready ? UIKit.Mint : new Color(0.88f, 0.88f, 0.88f, 1f);
                    row.Tag.color = m.IsHost || m.Ready ? Color.white : UIKit.Grey;
                    row.TagImage.gameObject.SetActive(true);
                    row.Button.interactable = isHost && m.Id != s.LocalId;
                    SetRowAlpha(row, 1f);
                }
                else
                {
                    row.MemberId = 0;
                    string label = i == 7 && members.Count > 8 ? $"+{members.Count - 7} MORE" : "OPEN SLOT";
                    if (row.Name.text != label) UIKit.SetText(row.Name, label);
                    row.LeftColor.enabled = row.RightColor.enabled = false;
                    row.TagImage.gameObject.SetActive(false);
                    row.Button.interactable = false;
                    SetRowAlpha(row, 0.45f);
                }
            }

            suppressOptionEvents = true;
            try
            {
                var settings = s?.Settings ?? SavedSettings();
                SetOptionIndex(optRounds, settings.Rounds - 1);
                SetOptionIndex(optDifficulty, settings.Difficulty);
                SetOptionIndex(optPrivacy, settings.Privacy);
                SetOptionIndex(optMax, settings.MaxPlayers - 2);
                SetOptionIndex(optLocal, (s?.Self?.LocalPlayers ?? 1) - 1);
            }
            finally { suppressOptionEvents = false; }

            bool editable = isHost && s.Phase == SessionPhase.Lobby;
            SetOptionEditable(optRounds, editable);
            SetOptionEditable(optDifficulty, editable);
            gameSettingsRow.interactable = connected;
            SetOptionEditable(optMax, editable);
            optPrivacy.gameObject.SetActive(s == null || s.Kind == SessionKind.Steam);
            SetOptionEditable(optPrivacy, editable && s.Kind == SessionKind.Steam);
            SetOptionEditable(optLocal, connected && s.Phase == SessionPhase.Lobby);

            float y = OptionTop;
            int visibleRows = lobbyRows.Count(o => o.gameObject.activeSelf);
            float spacing = visibleRows > 6 ? 290f : OptionSpacing;
            foreach (var row in lobbyRows)
            {
                if (!row.gameObject.activeSelf) continue;
                ((RectTransform)row.transform).anchoredPosition = new Vector2(OptionX + y * Skew, y);
                y -= spacing;
            }

            string arrowLabel, status, hint;
            if (!connected)
            {
                arrowLabel = "…";
                status = s == null ? "" : s.Kind == SessionKind.Direct ? $"Connecting to {s.DirectAddress}…" : "Talking to Steam…";
                hint = "";
            }
            else if (isHost)
            {
                int notReady = members.Count(m => !m.IsHost && !m.Ready);
                arrowLabel = notReady == 0 ? "LET'S GO!" : "WAITING…";
                status = members.Count == 1 ? "Invite some friends, or start on your own."
                    : notReady > 0 ? $"{notReady} player{(notReady == 1 ? " isn't" : "s aren't")} ready yet."
                    : "Everyone's ready!";
                hint = s.Kind == SessionKind.Steam ? "Friends can also join you from their Steam friends list."
                    : $"Your address: {s.DirectAddress}";
            }
            else
            {
                bool ready = s.Self != null && s.Self.Ready;
                arrowLabel = ready ? "NOT READY" : "READY!";
                status = ready ? $"Waiting for {s.Host?.Name ?? "the host"} to start…" : "Press READY when you're set.";
                hint = ModeSummary(s.Settings);
            }
            var arrowText = lobbyArrow.transform.Find("Label")?.GetComponent<TMP_Text>();
            if (arrowText != null && arrowText.text != arrowLabel) UIKit.SetText(arrowText, arrowLabel);
            lobbyArrow.interactable = connected;
            if (lobbyStatus.text != status) UIKit.SetText(lobbyStatus, status);
            if (lobbyHint.text != hint) UIKit.SetText(lobbyHint, hint);

            if (lobbyInviteChip != null)
            {
                bool direct = connected && s.Kind == SessionKind.Direct;
                lobbyInviteChip.gameObject.SetActive(connected && (!direct || isHost));
                var chipText = lobbyInviteChip.Find("Text")?.GetComponent<TMP_Text>();
                string want = direct && !Session.SteamAvailable ? "COPY ADDRESS" : "INVITE";
                if (chipText != null && chipText.text != want)
                {
                    UIKit.SetText(chipText, want);
                    LayoutRebuilder.MarkLayoutForRebuild((RectTransform)lobbyInviteChip);
                }
            }
            if (lobbyCustomiseChip != null) lobbyCustomiseChip.gameObject.SetActive(connected);

            RebuildLobbyNavigation(isHost, connected);
            RefreshGameSettings();
        }

        private static string ModeSummary(LobbySettings settings)
        {
            if (!settings.AllAtOnce) return "Take turns, and watch everyone else's go live.";
            if (settings.Sharing == 2) return "Everyone plays at once in one shared level.";
            if (settings.Sharing == 1) return "Everyone plays at once, sharing loose objects.";
            return settings.SeeEachOther ? "Everyone plays at once and sees the others as ghosts."
                : "Everyone plays each event at once on their own screen.";
        }

        private static void BuildGameSettings()
        {
            gameSettingsPanel = CloneLocalMenu("Online Game Settings Menu", "GAME SETTINGS", new Vector2(5800f, 2950f), 330f, out var content);
            gameOptions.Clear();
            optMode = MakeOption(content, "TURNS", Vector2.zero, 1f, true);
            SetOptionNames(optMode, ModeNames);
            optGhosts = MakeOption(content, "SEE EACH OTHER", Vector2.zero, 1f, false);
            SetOptionNames(optGhosts, OffOn);
            optSharing = MakeOption(content, "SHARED OBJECTS", Vector2.zero, 1f, true);
            SetOptionNames(optSharing, SharingNames);
            optCollide = MakeOption(content, "PLAYERS COLLIDE", Vector2.zero, 1f, false);
            SetOptionNames(optCollide, OffOn);
            gameOptions.AddRange(new[] { optMode, optGhosts, optSharing, optCollide });
            foreach (var opt in gameOptions) opt.OnValueChanged.AddListener(_ => OnSettingsEdited());
            gameSettingsInfo = MakeLine(content, new Vector2(-2250f, -880f), 4500f);
            gameSettingsInfo2 = MakeLine(content, new Vector2(-2250f, -1000f), 4500f);
            UIKit.SetActive(gameSettingsPanel.transform, "Container/Panel/Column", false);
        }

        private static void OpenGameSettings()
        {
            var s = Session.Current;
            if (s == null || s.Phase == SessionPhase.Connecting || !MenuIsIdle() || Top() != lobbyPanel) return;
            RefreshGameSettings();
            ShowMenu(gameSettingsPanel);
        }

        private static void RefreshGameSettings()
        {
            if (gameSettingsPanel == null) return;
            var s = Session.Current;
            var settings = s?.Settings ?? SavedSettings();
            suppressOptionEvents = true;
            try
            {
                SetOptionIndex(optMode, settings.Mode);
                SetOptionIndex(optGhosts, settings.SeeEachOther ? 1 : 0);
                SetOptionIndex(optSharing, settings.Sharing);
                SetOptionIndex(optCollide, settings.Collide ? 1 : 0);
            }
            finally { suppressOptionEvents = false; }

            bool editable = s != null && s.IsHost && s.Phase == SessionPhase.Lobby;
            optGhosts.gameObject.SetActive(settings.AllAtOnce);
            optSharing.gameObject.SetActive(settings.AllAtOnce);
            optCollide.gameObject.SetActive(settings.AllAtOnce && settings.Sharing == 2 && settings.SeeEachOther);
            foreach (var opt in gameOptions) SetOptionEditable(opt, editable);

            float y = 1000f;
            foreach (var opt in gameOptions)
            {
                if (!opt.gameObject.activeSelf) continue;
                ((RectTransform)opt.transform).anchoredPosition = new Vector2(y * Skew, y);
                y -= 470f;
            }

            DescribeSettings(settings, out string line1, out string line2);
            if (gameSettingsInfo.text != line1) UIKit.SetText(gameSettingsInfo, line1);
            if (gameSettingsInfo2.text != line2) UIKit.SetText(gameSettingsInfo2, line2);

            var column = editable ? gameOptions.Where(o => o.gameObject.activeSelf).Select(o => o.GetComponent<Selectable>()).ToList() : new List<Selectable>();
            UIKit.LinkVertical(column);
            gameSettingsPanel.SetDefaultOption(column.Count > 0 ? column[0] : null);
            if (Top() == gameSettingsPanel && MenuIsIdle() && column.Count > 0)
            {
                var selected = EventSystem.current != null ? EventSystem.current.currentSelectedGameObject : null;
                if (selected == null || !selected.activeInHierarchy || !selected.transform.IsChildOf(gameSettingsPanel.transform)) column[0].Select();
            }
        }

        private static void DescribeSettings(LobbySettings settings, out string line1, out string line2)
        {
            if (!settings.AllAtOnce)
            {
                line1 = "Everyone takes turns at each event and watches the others' goes live.";
                line2 = "";
                return;
            }
            line1 = settings.Sharing == 2 && settings.SeeEachOther ? "Everyone plays each event at the same time, together in one level."
                : settings.SeeEachOther ? "Everyone plays each event at the same time, seeing the others as ghosts."
                : "Everyone plays each event at the same time on their own screen.";
            line2 = settings.Sharing == 1 ? "Loose objects like boxes and balls are shared. Anything that scores stays your own."
                : settings.Sharing == 2 ? (settings.Collide && settings.SeeEachOther ? "Everything is shared, targets and pickups too, and players bump into each other."
                                          : "Everything is shared, targets and pickups too.")
                : "Nothing in the level is shared.";
        }

        private static void SetRowAlpha(PlayerRow row, float alpha)
        {
            var group = row.Root.GetComponent<CanvasGroup>() ?? row.Root.gameObject.AddComponent<CanvasGroup>();
            group.alpha = alpha;
        }

        private static void RebuildLobbyNavigation(bool isHost, bool connected)
        {
            var column = new List<Selectable>();
            if (isHost)
            {
                column.Add(optRounds.GetComponent<Selectable>());
                column.Add(optDifficulty.GetComponent<Selectable>());
            }
            if (connected) column.Add(gameSettingsRow);
            if (isHost)
            {
                if (optPrivacy.gameObject.activeSelf && optPrivacy.GetComponent<Selectable>().interactable) column.Add(optPrivacy.GetComponent<Selectable>());
                if (optMax.GetComponent<Selectable>().interactable) column.Add(optMax.GetComponent<Selectable>());
            }
            if (connected) column.Add(optLocal.GetComponent<Selectable>());
            column.Add(lobbyArrow);
            UIKit.LinkVertical(column);

            var rows = playerRows.Where(r => r.Root.gameObject.activeSelf && r.Button.interactable).Select(r => (Selectable)r.Button).ToList();
            UIKit.LinkVertical(rows);
            if (rows.Count > 0)
            {
                foreach (var r in rows)
                {
                    var nav = r.navigation;
                    nav.selectOnRight = column[0];
                    r.navigation = nav;
                }
                var arrowNav = lobbyArrow.navigation;
                arrowNav.selectOnLeft = rows[0];
                lobbyArrow.navigation = arrowNav;
            }

            var preferred = isHost ? column[0] : lobbyArrow;
            lobbyPanel.SetDefaultOption(preferred);
            var selected = EventSystem.current != null ? EventSystem.current.currentSelectedGameObject : null;
            if (Top() == lobbyPanel && MenuIsIdle())
            {
                var sel = selected != null ? selected.GetComponent<Selectable>() : null;
                if (sel == null || !sel.interactable || !sel.gameObject.activeInHierarchy || !sel.transform.IsChildOf(lobbyPanel.transform))
                {
                    preferred.Select();
                }
            }
        }

        private static void OnSettingsEdited()
        {
            var s = Session.Current;
            if (suppressOptionEvents || s == null || !s.IsHost) return;
            var settings = new LobbySettings
            {
                Rounds = optRounds.currentIndex + 1,
                Difficulty = optDifficulty.currentIndex,
                Privacy = optPrivacy.currentIndex,
                MaxPlayers = Mathf.Max(optMax.currentIndex + 2, s.TotalPlayers),
                Mode = optMode.currentIndex,
                SeeEachOther = optGhosts.currentIndex == 1,
                Sharing = optSharing.currentIndex,
                Collide = optCollide.currentIndex == 1,
            };
            Plugin.LobbyMode.Value = settings.Mode;
            Plugin.LobbySeeEachOther.Value = settings.SeeEachOther;
            Plugin.LobbySharing.Value = settings.Sharing;
            Plugin.LobbyCollide.Value = settings.Collide;
            Plugin.LobbyRounds.Value = settings.Rounds;
            Plugin.LobbyDifficulty.Value = settings.Difficulty;
            Plugin.LobbyPrivacy.Value = settings.Privacy;
            Plugin.LobbyMaxPlayers.Value = settings.MaxPlayers;
            s.ApplySettings(settings);
        }

        private static void OnLobbyArrow()
        {
            var s = Session.Current;
            if (s == null || s.Phase != SessionPhase.Lobby || !MenuIsIdle()) return;
            if (s.IsHost)
            {
                if (s.Members.Any(m => !m.IsHost && !m.Ready))
                {
                    SoundEffectManager.PlayUploadFailSound();
                    Toasts.Show("Waiting for everyone to ready up");
                    return;
                }
                if (customising) return;
                OnlineGame.HostStartGame();
            }
            else
            {
                s.UpdateSelf(m => m.Ready = !m.Ready);
                SoundEffectManager.PlayMenuSelectSound();
            }
        }

        private static void OnPlayerRowClicked(PlayerRow row)
        {
            var s = Session.Current;
            if (s == null || !s.IsHost || row.MemberId == 0 || row.MemberId == s.LocalId || !MenuIsIdle()) return;
            var member = s.Members.FirstOrDefault(m => m.Id == row.MemberId);
            if (member == null) return;
            Confirm($"Remove {OnlineGame.CleanName(member.Name)} from the lobby?", "NO", "REMOVE", () => s.Kick(member.Id));
        }

        private static void Invite()
        {
            var s = Session.Current;
            if (s == null || s.Phase == SessionPhase.Connecting || !MenuIsIdle()) return;
            if (s.Kind == SessionKind.Steam && s.LobbyId.IsValid())
            {
                if (SteamUtils.IsOverlayEnabled())
                {
                    SteamFriends.ActivateGameOverlayInviteDialog(s.LobbyId);
                    return;
                }
                Toasts.Show("Steam overlay is off - invite friends from here");
                OpenInvite();
            }
            else if (s.IsHost)
            {
                if (Session.SteamAvailable) OpenInvite();
                else CopyAddress();
            }
        }

        private static void CopyAddress()
        {
            var s = Session.Current;
            if (s == null || s.DirectAddress == null) return;
            GUIUtility.systemCopyBuffer = s.DirectAddress;
            Toasts.Show("Copied " + s.DirectAddress);
        }

        private sealed class InviteEntry
        {
            public string Label, Value;
            public Action Action;
        }

        private const int InvitePageSize = 7;
        private static MenuPanel invitePanel;
        private static readonly List<UIButtonOption> inviteRows = new List<UIButtonOption>();
        private static readonly List<InviteEntry> inviteEntries = new List<InviteEntry>();
        private static readonly HashSet<ulong> invitedFriends = new HashSet<ulong>();
        private static TMP_Text inviteStatus;
        private static int invitePage;

        private static void BuildInvite()
        {
            invitePanel = CloneLocalMenu("Online Invite Menu", "INVITE FRIENDS", new Vector2(6000f, 2950f), 330f, out var content);
            inviteRows.Clear();
            for (int i = 0; i < InvitePageSize; i++)
            {
                var row = MakeButtonRow(content, "Friend " + i, new Vector2(-120f, 1020f - i * 330f), 4900f);
                int index = i;
                UIKit.OnClick(row, () =>
                {
                    int entry = invitePage * InvitePageSize + index;
                    if (entry < inviteEntries.Count) inviteEntries[entry].Action?.Invoke();
                });
                inviteRows.Add(row);
            }
            inviteStatus = MakeLine(content, new Vector2(-2200f, -1230f), 4400f);
            UIKit.SetActive(invitePanel.transform, "Container/Panel/Column", false);
            UIKit.ConfigureChip(invitePanel.transform.Find("Container/Bottom Menu/Options"), GlurpControllerManager.ButtonLocation.RightShoulder, "MORE FRIENDS", () => TurnInvitePage(1));
        }

        private static void OpenInvite()
        {
            if (invitePanel == null) return;
            invitePage = 0;
            RefreshInvite();
            ShowMenu(invitePanel);
        }

        private static void TurnInvitePage(int delta)
        {
            int pages = Math.Max(1, (inviteEntries.Count + InvitePageSize - 1) / InvitePageSize);
            invitePage = ((invitePage + delta) % pages + pages) % pages;
            SoundEffectManager.PlayMenuSelectSound();
            RefreshInvite();
        }

        private static void RefreshInvite()
        {
            var s = Session.Current;
            inviteEntries.Clear();
            if (s != null && s.Kind == SessionKind.Direct && s.IsHost)
            {
                inviteEntries.Add(new InviteEntry { Label = "COPY MY ADDRESS", Value = s.DirectAddress, Action = CopyAddress });
            }
            if (Session.SteamAvailable && s != null)
            {
                var app = SteamUtils.GetAppID();
                var friends = new List<KeyValuePair<int, InviteEntry>>();
                int count = SteamFriends.GetFriendCount(EFriendFlags.k_EFriendFlagImmediate);
                for (int i = 0; i < count; i++)
                {
                    var id = SteamFriends.GetFriendByIndex(i, EFriendFlags.k_EFriendFlagImmediate);
                    var state = SteamFriends.GetFriendPersonaState(id);
                    if (state == EPersonaState.k_EPersonaStateOffline || state == EPersonaState.k_EPersonaStateInvisible) continue;
                    bool playing = SteamFriends.GetFriendGamePlayed(id, out var info) && info.m_gameID.AppID() == app;
                    bool inLobby = s.Members.Any(m => m.Id == id.m_SteamID);
                    bool away = state == EPersonaState.k_EPersonaStateAway || state == EPersonaState.k_EPersonaStateSnooze || state == EPersonaState.k_EPersonaStateBusy;
                    var friend = id;
                    string name = OnlineGame.CleanName(SteamFriends.GetFriendPersonaName(id)).ToUpperInvariant();
                    var entry = new InviteEntry
                    {
                        Label = name,
                        Value = inLobby ? "IN LOBBY" : invitedFriends.Contains(id.m_SteamID) ? "INVITED!" : playing ? "IN GLURP" : away ? "AWAY" : "ONLINE",
                        Action = inLobby ? (Action)null : () => SendInvite(friend, name),
                    };
                    friends.Add(new KeyValuePair<int, InviteEntry>(playing ? 0 : away ? 2 : 1, entry));
                }
                inviteEntries.AddRange(friends.OrderBy(f => f.Key).ThenBy(f => f.Value.Label).Select(f => f.Value));
            }

            int pages = Math.Max(1, (inviteEntries.Count + InvitePageSize - 1) / InvitePageSize);
            invitePage = Mathf.Clamp(invitePage, 0, pages - 1);
            var nav = new List<Selectable>();
            for (int i = 0; i < inviteRows.Count; i++)
            {
                int index = invitePage * InvitePageSize + i;
                var row = inviteRows[i];
                bool visible = index < inviteEntries.Count;
                row.gameObject.SetActive(visible);
                if (!visible) continue;
                SetButtonRow(row, inviteEntries[index].Label, inviteEntries[index].Value);
                row.interactable = inviteEntries[index].Action != null;
                if (row.interactable) nav.Add(row);
            }
            UIKit.LinkVertical(nav);
            invitePanel.SetDefaultOption(nav.Count > 0 ? nav[0] : null);

            var chip = invitePanel.transform.Find("Container/Bottom Menu/Options");
            if (chip != null) chip.gameObject.SetActive(pages > 1);
            string status = !Session.SteamAvailable ? "Steam isn't running, so friends can't be invited."
                : inviteEntries.Count == 0 ? "None of your Steam friends are online right now."
                : pages > 1 ? $"Page {invitePage + 1} of {pages}. Friends get a Steam invite to join you."
                : "Friends get a Steam invite to join you.";
            if (inviteStatus.text != status) UIKit.SetText(inviteStatus, status);
            if (Top() == invitePanel && MenuIsIdle() && nav.Count > 0)
            {
                var selected = EventSystem.current != null ? EventSystem.current.currentSelectedGameObject : null;
                if (selected == null || !selected.activeInHierarchy || !selected.transform.IsChildOf(invitePanel.transform)) nav[0].Select();
            }
        }

        private static void SendInvite(CSteamID friend, string name)
        {
            var s = Session.Current;
            if (s == null) return;
            bool sent = s.Kind == SessionKind.Steam
                ? SteamMatchmaking.InviteUserToLobby(s.LobbyId, friend)
                : SteamFriends.InviteUserToGame(friend, "+glurp_direct " + s.DirectAddress);
            if (sent)
            {
                invitedFriends.Add(friend.m_SteamID);
                SoundEffectManager.PlayMenuSelectSound();
                Toasts.Show("Invite sent to " + name);
            }
            else
            {
                SoundEffectManager.PlayUploadFailSound();
                Toasts.Show("Couldn't invite " + name);
            }
            RefreshInvite();
        }

        private static void ConfirmLeaveLobby()
        {
            var s = Session.Current;
            if (s == null || s.Phase == SessionPhase.Connecting)
            {
                s?.Leave();
                if (Top() == lobbyPanel) PopMenu();
                return;
            }
            string text = !s.IsHost ? "Leave this lobby?"
                : s.Kind == SessionKind.Steam && s.Members.Count > 1 ? "Leave this lobby? Someone else will become the host."
                : s.Members.Count > 1 ? "Close this lobby? Everyone will be disconnected."
                : "Close this lobby?";
            Confirm(text, "STAY", "LEAVE", () => s.Leave());
        }

        public static void ConfirmLeaveGame()
        {
            Confirm("Leave this online game?", "STAY", "LEAVE", () => GameManager.ReturnToMenu(returnToTitle: false));
        }

        private static void OnSessionClosed(string reason)
        {
            if (menu == null || OnlineGame.Active) return;
            if (customising) EndCustomise(pop: false);
            if (InStack(lobbyPanel))
            {
                if (MenuIsIdle()) PopTo(lobbyPanel);
                else popLobbyWhenIdle = true;
            }
            if (!string.IsNullOrEmpty(reason)) Info(reason);
        }

        private static bool popLobbyWhenIdle;

        public static void OnGameStarting()
        {
            if (customising) EndCustomise(pop: true);
            if (keyboardOpen) CloseKeyboard(pop: false);
        }

        private static void BeginCustomise()
        {
            var s = Session.Current;
            var self = s?.Self;
            if (self == null || s.Phase != SessionPhase.Lobby || customising || !MenuIsIdle() || Top() != lobbyPanel) return;
            var guy = UnityEngine.Object.FindObjectOfType<Guy>();
            if (guy == null) return;
            var gm = GameMenu.singleton;
            customising = true;
            if (!s.IsHost && self.Ready) s.UpdateSelf(m => m.Ready = false);
            MainMenu.mainMenuCustomizeGuy = guy;
            GameManager.isInChallengeMode = true;
            TurnManager.Reset();
            var p = TurnManager.AddPlayer(0);
            p.teamName = OnlineGame.CleanName(self.Name);
            p.hasBeenNamed = true;
            p.hatID = string.IsNullOrEmpty(self.HatId) ? null : self.HatId;
            var colors = GameManager.singleton.playerColors;
            if (self.ColorIndex >= 0 && self.ColorIndex < colors.Length) p.colorScheme = colors[self.ColorIndex];
            guy.ApplyColorScheme(p.colorScheme);
            menu.StartCoroutine(menu.ApplyHatRoutine(p.hatID));
            gm.customiseCharacterBackButton.onClick.AddListener(OnCustomiseDone);
            gm.customiseCharacterReadyArrow.onClick.AddListener(OnCustomiseDone);
            gm.BuildCustomiseCharacterMenu();
            ShowMenu(gm.customiseCharacterScreen);
            CameraIndex(menu) = 2;
            menu.MoveCameraRight();
        }

        private static void OnCustomiseDone()
        {
            if (!customising || !MenuIsIdle()) return;
            EndCustomise(pop: true);
        }

        private static void EndCustomise(bool pop)
        {
            if (!customising) return;
            customising = false;
            var gm = GameMenu.singleton;
            gm.customiseCharacterBackButton.onClick.RemoveListener(OnCustomiseDone);
            gm.customiseCharacterReadyArrow.onClick.RemoveListener(OnCustomiseDone);
            var p = TurnManager.CurrentPlayer();
            if (p != null)
            {
                int colorIndex = Array.IndexOf(GameManager.singleton.playerColors, p.colorScheme);
                string hat = p.hatID;
                Plugin.LastColorIndex.Value = colorIndex;
                Plugin.LastHatId.Value = hat ?? "";
                Session.Current?.UpdateSelf(m => { m.ColorIndex = colorIndex; m.HatId = hat; });
            }
            gm.customiseCharacterGuy.TidyUp();
            gm.customiseCharacterHatPicker.TidyUp();
            MainMenu.mainMenuCustomizeGuy = null;
            GameManager.isInChallengeMode = false;
            TurnManager.activePlayers.Clear();
            if (pop && menu != null && Top() == gm.customiseCharacterScreen)
            {
                PopMenu();
                CameraIndex(menu) = MenuStack(menu).Count - 1;
                menu.MoveCameraRight();
            }
        }

        private static void BuildBrowser()
        {
            browserPanel = CloneLocalMenu("Online Browser Menu", "FIND GAMES", new Vector2(6000f, 2950f), 330f, out var content);
            for (int i = 0; i < 7; i++)
            {
                var row = MakeButtonRow(content, "Game " + i, new Vector2(-120f, 1020f - i * 330f), 4900f);
                int index = i;
                UIKit.OnClick(row, () => JoinListing(index));
                browserRows.Add(row);
            }
            browserStatus = MakeLine(content, new Vector2(-2200f, -1230f), 4400f);
            UIKit.SetActive(browserPanel.transform, "Container/Panel/Column", false);
            UIKit.ConfigureChip(browserPanel.transform.Find("Container/Bottom Menu/Options"), GlurpControllerManager.ButtonLocation.North, "REFRESH", RefreshBrowser);
            RenderBrowser();
        }

        private static void OpenBrowser()
        {
            if (!MenuIsIdle()) return;
            if (!Session.SteamAvailable)
            {
                Info("Steam isn't running, so games can't be listed. Use Direct Connect instead.");
                return;
            }
            ShowMenu(browserPanel);
            RefreshBrowser();
        }

        private static void RefreshBrowser()
        {
            if (!Session.SteamAvailable) return;
            EnsureSteamCallbacks();
            searching = true;
            nextBrowserRefresh = Time.unscaledTime + 10f;
            listings.RemoveAll(l => !l.Friend);

            SteamMatchmaking.AddRequestLobbyListDistanceFilter(ELobbyDistanceFilter.k_ELobbyDistanceFilterWorldwide);
            SteamMatchmaking.AddRequestLobbyListResultCountFilter(50);
            lobbyListCall.Set(SteamMatchmaking.RequestLobbyList());

            var app = SteamUtils.GetAppID();
            int friends = SteamFriends.GetFriendCount(EFriendFlags.k_EFriendFlagImmediate);
            var seen = new HashSet<ulong>();
            for (int i = 0; i < friends; i++)
            {
                var friend = SteamFriends.GetFriendByIndex(i, EFriendFlags.k_EFriendFlagImmediate);
                if (!SteamFriends.GetFriendGamePlayed(friend, out var info)) continue;
                if (info.m_gameID.AppID() != app || !info.m_steamIDLobby.IsValid()) continue;
                seen.Add(info.m_steamIDLobby.m_SteamID);
                var listing = listings.FirstOrDefault(l => l.Id == info.m_steamIDLobby);
                if (listing == null)
                {
                    listing = new Listing { Id = info.m_steamIDLobby, Friend = true, Name = SteamFriends.GetFriendPersonaName(friend) + "'s game", Compatible = false };
                    listings.Add(listing);
                }
                listing.Friend = true;
                SteamMatchmaking.RequestLobbyData(info.m_steamIDLobby);
            }
            listings.RemoveAll(l => l.Friend && !seen.Contains(l.Id.m_SteamID));
            RenderBrowser();
        }

        private static void EnsureSteamCallbacks()
        {
            if (!Session.SteamAvailable) return;
            if (lobbyListCall == null) lobbyListCall = CallResult<LobbyMatchList_t>.Create(OnLobbyList);
            if (lobbyDataCallback == null) lobbyDataCallback = Callback<LobbyDataUpdate_t>.Create(OnLobbyData);
            if (joinRequestedCallback == null)
            {
                joinRequestedCallback = Callback<GameLobbyJoinRequested_t>.Create(r =>
                {
                    Plugin.Log.LogInfo($"Join requested via Steam for lobby {r.m_steamIDLobby}");
                    RequestJoin(r.m_steamIDLobby);
                });
            }
            if (richJoinCallback == null)
            {
                richJoinCallback = Callback<GameRichPresenceJoinRequested_t>.Create(r => HandleConnectString(r.m_rgchConnect));
            }
        }

        private static Callback<GameRichPresenceJoinRequested_t> richJoinCallback;
        private static string pendingDirectJoin;

        private static void HandleConnectString(string text)
        {
            if (string.IsNullOrEmpty(text)) return;
            var parts = text.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            for (int i = 0; i < parts.Length - 1; i++)
            {
                if (parts[i] == "+connect_lobby" && ulong.TryParse(parts[i + 1], out var id)) RequestJoin(new CSteamID(id));
                else if (parts[i] == "+glurp_direct")
                {
                    pendingDirectJoin = parts[i + 1];
                    if (GameManager.isPlaying) Toasts.Show("You'll join after this game");
                }
            }
        }

        private static void OnLobbyList(LobbyMatchList_t result, bool ioFailure)
        {
            searching = false;
            if (!ioFailure)
            {
                for (int i = 0; i < result.m_nLobbiesMatching; i++)
                {
                    var id = SteamMatchmaking.GetLobbyByIndex(i);
                    var listing = listings.FirstOrDefault(l => l.Id == id);
                    if (listing == null) listings.Add(listing = new Listing { Id = id });
                    ReadListing(listing);
                }
            }
            RenderBrowser();
        }

        private static void OnLobbyData(LobbyDataUpdate_t update)
        {
            if (update.m_ulSteamIDLobby != update.m_ulSteamIDMember) return;
            var listing = listings.FirstOrDefault(l => l.Id.m_SteamID == update.m_ulSteamIDLobby);
            if (listing == null) return;
            if (update.m_bSuccess == 0) listings.Remove(listing);
            else ReadListing(listing);
            RenderBrowser();
        }

        private static void ReadListing(Listing l)
        {
            l.Compatible = !string.IsNullOrEmpty(SteamMatchmaking.GetLobbyData(l.Id, "glurponline"));
            l.Version = SteamMatchmaking.GetLobbyData(l.Id, "modver");
            string name = SteamMatchmaking.GetLobbyData(l.Id, "name");
            if (!string.IsNullOrEmpty(name)) l.Name = name;
            int.TryParse(SteamMatchmaking.GetLobbyData(l.Id, "players"), out l.Players);
            int.TryParse(SteamMatchmaking.GetLobbyData(l.Id, "max"), out l.Max);
            if (l.Players == 0) l.Players = SteamMatchmaking.GetNumLobbyMembers(l.Id);
            if (l.Max == 0) l.Max = SteamMatchmaking.GetLobbyMemberLimit(l.Id);
            l.Playing = SteamMatchmaking.GetLobbyData(l.Id, "phase") == "playing";
        }

        private static List<Listing> SortedListings() => listings
            .Where(l => l.Compatible || l.Friend)
            .OrderByDescending(l => l.Friend)
            .ThenBy(l => l.Playing)
            .ThenByDescending(l => l.Players)
            .ToList();

        private static void RenderBrowser()
        {
            if (browserPanel == null) return;
            var sorted = SortedListings();
            var selectables = new List<Selectable>();
            for (int i = 0; i < browserRows.Count; i++)
            {
                var row = browserRows[i];
                bool visible = i < sorted.Count;
                row.gameObject.SetActive(visible);
                if (!visible) continue;
                var l = sorted[i];
                string label = OnlineGame.CleanName(l.Name.Replace("'s game", ""), 0).ToUpperInvariant() + "'S GAME" + (l.Friend ? " · FRIEND" : "");
                string value = !l.Compatible ? "OLD VERSION" : l.Playing ? "PLAYING" : l.Version != Plugin.Version ? "V" + l.Version
                    : l.Max > 0 ? $"{l.Players}/{l.Max}" : $"{l.Players}";
                SetButtonRow(row, label, value);
                row.interactable = l.Compatible && !l.Playing;
                if (row.interactable) selectables.Add(row);
            }
            UIKit.LinkVertical(selectables);
            browserPanel.SetDefaultOption(selectables.Count > 0 ? selectables[0] : null);
            string status = searching ? "Searching for games…"
                : sorted.Count == 0 ? "No games found right now. Why not host one?"
                : $"{sorted.Count} game{(sorted.Count == 1 ? "" : "s")} found. Friends' games are listed first.";
            if (browserStatus.text != status) UIKit.SetText(browserStatus, status);
            if (Top() == browserPanel && MenuIsIdle() && selectables.Count > 0)
            {
                var selected = EventSystem.current != null ? EventSystem.current.currentSelectedGameObject : null;
                if (selected == null || !selected.activeInHierarchy || !selected.transform.IsChildOf(browserPanel.transform)) selectables[0].Select();
            }
        }

        private static void JoinListing(int index)
        {
            var sorted = SortedListings();
            if (index >= sorted.Count || !MenuIsIdle()) return;
            var l = sorted[index];
            if (!l.Compatible || l.Playing) { SoundEffectManager.PlayUploadFailSound(); return; }
            Session.JoinSteam(l.Id);
            ShowLobby();
        }

        public static void RequestJoin(CSteamID lobby)
        {
            pendingJoin = lobby;
            if (GameManager.isPlaying) Toasts.Show("You'll join after this game");
        }

        private static void TryPendingJoin()
        {
            if (menu == null || GameManager.isPlaying || !MenuIsIdle() || customising || keyboardOpen) return;
            if (pendingDirectJoin != null)
            {
                string address = pendingDirectJoin;
                pendingDirectJoin = null;
                Session.Current?.Leave();
                JoinAddressString(address);
                return;
            }
            if (!pendingJoin.IsValid()) return;
            var lobby = pendingJoin;
            pendingJoin = CSteamID.Nil;
            var s = Session.Current;
            if (s != null && s.LobbyId == lobby)
            {
                ShowLobby();
                return;
            }
            s?.Leave();
            Session.JoinSteam(lobby);
            ShowLobby();
        }

        private static void CheckCommandLine()
        {
            if (commandLineChecked || !Session.SteamAvailable) return;
            commandLineChecked = true;
            HandleConnectString(string.Join(" ", Environment.GetCommandLineArgs()));
        }

        private static readonly List<UIButtonOption> networkRows = new List<UIButtonOption>();
        private static readonly List<Action> networkActions = new List<Action>();
        private static TMP_Text directListTitle;

        private static void BuildDirect()
        {
            directPanel = CloneLocalMenu("Online Direct Menu", "DIRECT CONNECT", new Vector2(5800f, 2950f), 330f, out var content);
            directListTitle = MakeLine(content, new Vector2(-2350f, 1300f), 4400f);
            networkRows.Clear();
            for (int i = 0; i < 3; i++)
            {
                var row = MakeButtonRow(content, "Network " + i, new Vector2(0f, 990f - i * 320f), 4800f);
                int index = i;
                UIKit.OnClick(row, () => { if (index < networkActions.Count) networkActions[index]?.Invoke(); });
                networkRows.Add(row);
            }
            directAddressRow = MakeButtonRow(content, "Address", new Vector2(0f, -50f), 4800f);
            UIKit.OnClick(directAddressRow, OpenKeyboard);
            directHostRow = MakeButtonRow(content, "Host", new Vector2(0f, -370f), 4800f);
            UIKit.OnClick(directHostRow, HostDirectGame);
            directJoinArrow = MakeArrow(content, "JOIN!", new Vector2(2700f, -790f));
            UIKit.OnClick(directJoinArrow, JoinDirectGame);
            directStatus = MakeLine(content, new Vector2(-2350f, -1090f), 4000f);
            directHint = MakeLine(content, new Vector2(-2350f, -1210f), 4000f);
            UIKit.SetActive(directPanel.transform, "Container/Panel/Column", false);
            UIKit.ConfigureChip(directPanel.transform.Find("Container/Bottom Menu/Options"), GlurpControllerManager.ButtonLocation.North, "PASTE ADDRESS", PasteAddress);
            RefreshDirect();
        }

        private static void RefreshDirect()
        {
            if (directPanel == null) return;
            int port = Plugin.DirectPort.Value;

            var rows = new List<KeyValuePair<string[], Action>>();
            var lan = LanDiscovery.Games.OrderByDescending(g => g.Compatible && !g.Playing).ThenBy(g => g.Name).ToList();
            foreach (var g in lan)
            {
                string value = !g.Compatible ? "OTHER VERSION" : g.Playing ? "PLAYING" : g.Version != Plugin.Version ? "LAN · V" + g.Version
                    : g.Max > 0 ? $"LAN · {g.Players}/{g.Max}" : "LAN";
                var game = g;
                rows.Add(new KeyValuePair<string[], Action>(
                    new[] { OnlineGame.CleanName(g.Name).ToUpperInvariant() + "'S GAME", value },
                    g.Compatible && !g.Playing ? () => JoinAddress(game.Address, game.Port) : (Action)null));
            }
            foreach (var recent in RecentAddresses())
            {
                if (lan.Any(g => g.Endpoint == recent || g.Address == recent)) continue;
                string recentAddress = recent;
                rows.Add(new KeyValuePair<string[], Action>(new[] { "RECENT", recentAddress }, () => JoinAddressString(recentAddress)));
            }

            networkActions.Clear();
            var nav = new List<Selectable>();
            for (int i = 0; i < networkRows.Count; i++)
            {
                var row = networkRows[i];
                bool visible = i < rows.Count;
                row.gameObject.SetActive(visible);
                if (!visible) continue;
                SetButtonRow(row, rows[i].Key[0], rows[i].Key[1]);
                networkActions.Add(rows[i].Value);
                row.interactable = rows[i].Value != null;
                if (row.interactable) nav.Add(row);
            }

            string title = lan.Count > 0 ? "Games on your network:"
                : LanDiscovery.Listening ? "Looking for games on your network…"
                : rows.Count > 0 ? "Recently joined:" : "";
            if (directListTitle.text != title) UIKit.SetText(directListTitle, title);

            string address = Plugin.LastDirectAddress.Value.Trim();
            SetButtonRow(directAddressRow, "JOIN ADDRESS", address.Length > 0 ? address : "TYPE ONE");
            SetButtonRow(directHostRow, "HOST A GAME", $"PORT {port}");
            UIKit.SetText(directStatus, $"Your address is {Session.LocalAddressHint()}. Copied an address? Paste it with the button below.");
            UIKit.SetText(directHint, $"For play over the internet, the host forwards TCP port {port} on their router.");

            nav.Add(directAddressRow);
            nav.Add(directHostRow);
            nav.Add(directJoinArrow);
            UIKit.LinkVertical(nav);
            directPanel.SetDefaultOption(nav[0]);
        }

        private static void OpenDirect()
        {
            if (!MenuIsIdle()) return;
            LanDiscovery.StartListening();
            RefreshDirect();
            ShowMenu(directPanel);
        }

        private static void HostDirectGame()
        {
            if (!MenuIsIdle()) return;
            if (Session.HostDirect(SavedSettings(), Plugin.DirectPort.Value) != null) ShowLobby();
        }

        private static void JoinDirectGame()
        {
            if (!MenuIsIdle()) return;
            if (Plugin.LastDirectAddress.Value.Trim().Length == 0)
            {
                OpenKeyboard();
                return;
            }
            JoinAddressString(Plugin.LastDirectAddress.Value);
        }

        private static void JoinAddressString(string text)
        {
            string address = text.Trim();
            int port = Plugin.DirectPort.Value;
            int colon = address.LastIndexOf(':');
            if (colon > 0 && address.IndexOf(':') == colon && int.TryParse(address.Substring(colon + 1), out var p))
            {
                port = p;
                address = address.Substring(0, colon);
            }
            JoinAddress(address, port);
        }

        private static void JoinAddress(string address, int port)
        {
            if (!MenuIsIdle() || string.IsNullOrEmpty(address)) return;
            RememberAddress(port == Plugin.DirectPort.Value ? address : $"{address}:{port}");
            if (Session.JoinDirect(address, port) != null) ShowLobby();
        }

        private static List<string> RecentAddresses() =>
            Plugin.RecentAddresses.Value.Split(',').Select(a => a.Trim()).Where(a => a.Length > 0).ToList();

        private static void RememberAddress(string address)
        {
            if (IPAddressIsLoopback(address)) return;
            var list = RecentAddresses();
            list.Remove(address);
            list.Insert(0, address);
            Plugin.RecentAddresses.Value = string.Join(",", list.Take(4).ToArray());
        }

        private static bool IPAddressIsLoopback(string address) =>
            System.Net.IPAddress.TryParse(address.Split(':')[0], out var ip) && System.Net.IPAddress.IsLoopback(ip);

        private static void PasteAddress()
        {
            string text = (GUIUtility.systemCopyBuffer ?? "").Trim();
            if (text.Length == 0 || text.Length > 64 || text.Any(char.IsWhiteSpace))
            {
                SoundEffectManager.PlayUploadFailSound();
                Toasts.Show("Nothing to paste – copy an address first");
                return;
            }
            Plugin.LastDirectAddress.Value = text;
            SoundEffectManager.PlayMenuSelectSound();
            RefreshDirect();
            directJoinArrow.Select();
        }

        private static void OpenKeyboard()
        {
            if (!MenuIsIdle() || keyboardOpen) return;
            var gm = GameMenu.singleton;
            keyboardOpen = true;
            gm.keyboard.Configure(email: true);
            gm.keyboard.SetMaxStringLength(40);
            gm.keyboard.RevertCaps();
            UIKit.SetText(gm.nameEntryTitle, "HOST ADDRESS");
            gm.keyboard.inputField.text = Plugin.LastDirectAddress.Value;
            gm.nameResultText.text = "";
            gm.nameEntryBackButton.gameObject.SetActive(true);
            gm.nameSuggestionPicker.gameObject.SetActive(false);
            gm.nameEntryScreen.SetDefaultOption(gm.keyboard.emailStartKey);
            gm.keyboard.OnSubmit.AddListener(OnAddressSubmitted);
            gm.keyboard.OnCancel.AddListener(OnAddressCancelled);
            ShowMenu(gm.nameEntryScreen);
            menu.state = MenuScreen.Online;
        }

        private static void OnAddressSubmitted()
        {
            var gm = GameMenu.singleton;
            string text = gm.keyboard.inputField.text.Trim();
            if (text.Length == 0)
            {
                SoundEffectManager.PlayUploadFailSound();
                return;
            }
            Plugin.LastDirectAddress.Value = text;
            CloseKeyboard(pop: true);
            RefreshDirect();
        }

        private static void OnAddressCancelled() => CloseKeyboard(pop: true);

        private static void CloseKeyboard(bool pop)
        {
            if (!keyboardOpen) return;
            keyboardOpen = false;
            var gm = GameMenu.singleton;
            gm.keyboard.OnSubmit.RemoveListener(OnAddressSubmitted);
            gm.keyboard.OnCancel.RemoveListener(OnAddressCancelled);
            gm.keyboard.Configure(email: false);
            if (pop && menu != null && Top() == gm.nameEntryScreen) PopMenu();
        }

        public static bool OnBackPressed()
        {
            if (menu == null || !MenuIsIdle()) return true;
            var top = Top();
            if (top == null) return true;
            if (customising && top == GameMenu.singleton.customiseCharacterScreen) { EndCustomise(pop: true); return false; }
            if (keyboardOpen && top == GameMenu.singleton.nameEntryScreen) { CloseKeyboard(pop: true); return false; }
            if (top == lobbyPanel) { ConfirmLeaveLobby(); return false; }
            return true;
        }

        public static bool OnCustomiseBack()
        {
            if (!customising) return true;
            if (MenuIsIdle()) EndCustomise(pop: true);
            return false;
        }

        public static void Update()
        {
            EnsureSteamCallbacks();
            CheckCommandLine();
            Toasts.Update();
            if (Session.Current != null) MainMenuIdleReplayManager.enableIdleReplays = false;
            if (menu == null) return;

            TryPendingJoin();
            bool wantLan = InStack(directPanel) && (Top() == directPanel || keyboardOpen);
            if (wantLan && !LanDiscovery.Listening) { LanDiscovery.StartListening(); RefreshDirect(); }
            else if (!wantLan && LanDiscovery.Listening) LanDiscovery.StopListening();
            if (lobbyPanel != null && Time.unscaledTime > nextLobbyRefresh && InStack(lobbyPanel)) RefreshLobby();
            if (Top() == browserPanel && !searching && Time.unscaledTime > nextBrowserRefresh) RefreshBrowser();

            if (!MenuIsIdle()) return;
            if (popLobbyWhenIdle && !PopupShowing())
            {
                popLobbyWhenIdle = false;
                if (Session.Current == null && InStack(lobbyPanel)) PopTo(lobbyPanel);
                return;
            }
            var player = ControllerManager.GetControllingPlayer();
            if (player == null) return;
            var top = Top();
            if (keyboardOpen && top == GameMenu.singleton.nameEntryScreen)
            {
                var kb = GameMenu.singleton.keyboard;
                if (player.GetButtonDown("UIOption1")) kb.OnKeyPressed("backspace");
                else if (player.GetButtonDown("UIOption2")) kb.ToggleCaps();
                else if (player.GetButtonDown("UIMenu")) SelectOrClick(kb.GetArrow());
                return;
            }
            if (menu.state != MenuScreen.Online) return;
            if (top == lobbyPanel)
            {
                if (player.GetButtonDown("UIOption2")) Invite();
                else if (player.GetButtonDown("UIOption1")) BeginCustomise();
                else if (player.GetButtonDown("UIMenu")) SelectOrClick(lobbyArrow);
            }
            else if (top == browserPanel)
            {
                if (player.GetButtonDown("UIOption2")) RefreshBrowser();
            }
            else if (top == directPanel)
            {
                if (player.GetButtonDown("UIOption2")) PasteAddress();
                else if (player.GetButtonDown("UIMenu")) SelectOrClick(directJoinArrow);
            }
            else if (top == invitePanel)
            {
                if (player.GetButtonDown("UINextPage")) TurnInvitePage(1);
                else if (player.GetButtonDown("UIPreviousPage")) TurnInvitePage(-1);
            }
        }

        private static void SelectOrClick(Button b)
        {
            if (b == null || !b.interactable) return;
            if (EventSystem.current != null && EventSystem.current.currentSelectedGameObject == b.gameObject) b.onClick.Invoke();
            else b.Select();
        }
    }

    [HarmonyPatch(typeof(MainMenu), "Start")]
    internal static class MainMenuStartPatch
    {
        private static void Postfix(MainMenu __instance)
        {
            if (MainMenu.singleton == __instance) OnlineUI.OnMainMenuStart(__instance);
        }
    }

    [HarmonyPatch(typeof(MainMenu), nameof(MainMenu.OnBackPressed))]
    internal static class MainMenuBackPatch
    {
        private static bool Prefix() => OnlineUI.OnBackPressed();
    }

    [HarmonyPatch(typeof(MainMenu), nameof(MainMenu.OnCustomizeChallengeCharacterBackPressed))]
    internal static class CustomiseBackPatch
    {
        private static bool Prefix() => OnlineUI.OnCustomiseBack();
    }
}
