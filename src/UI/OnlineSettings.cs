using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace GlurpOnline.UI
{
    internal static class OnlineSettings
    {
        private const string PageTitle = "Online";
        private const float RowHeight = 380f;
        private const float GameRowHeight = 448f;

        private sealed class Page
        {
            public CanvasGroup Group;
            public UISliderOption Volume;
            public UIHorizontalOptions Mute, Input, Output, Stream;
            public List<string> Inputs = new List<string>(), Outputs = new List<string>();
            public int Index;
        }

        private static readonly Dictionary<SettingsMenu, Page> pages = new Dictionary<SettingsMenu, Page>();
        private static bool refreshing;

        private static readonly AccessTools.FieldRef<SettingsMenu, int> PageIndex = AccessTools.FieldRefAccess<SettingsMenu, int>("pageIndex");

        public static void AfterBuild(SettingsMenu settings)
        {
            if (settings == null || settings.pageControl == null || settings.pages == null || settings.pages.Length == 0) return;
            if (!pages.TryGetValue(settings, out var page) || page.Group == null)
            {
                page = BuildPage(settings);
                if (page == null) return;
                pages[settings] = page;
                ExtendPageControl(settings);
            }
            Refresh(page);
        }

        private static void Refresh(Page page)
        {
            refreshing = true;
            try
            {
                page.Volume.SetValue(Mathf.Clamp01(Plugin.VoiceVolume.Value));
                Choose(page.Mute, Plugin.MuteMic.Value ? 1 : 0);
                page.Inputs = new List<string> { "" };
                page.Inputs.AddRange(Game.MicCapture.Devices());
                Names(page.Input, page.Inputs.Select(d => d.Length == 0 ? "STEAM MIC" : d.ToUpperInvariant()));
                Choose(page.Input, Mathf.Max(0, page.Inputs.IndexOf(Plugin.InputDevice.Value)));
                page.Outputs = new List<string> { "" };
                page.Outputs.AddRange(Game.WaveOutput.Devices());
                Names(page.Output, page.Outputs.Select(d => d.Length == 0 ? "GAME SOUND" : d.ToUpperInvariant()));
                Choose(page.Output, Mathf.Max(0, page.Outputs.IndexOf(Plugin.OutputDevice.Value)));
                Choose(page.Stream, Plugin.StreamMode.Value ? 1 : 0);
            }
            finally { refreshing = false; }
        }

        private static void Names(UIHorizontalOptions option, IEnumerable<string> names)
        {
            option.useIntegerValues = false;
            option.localiseOptionNames = false;
            option.SetOptionNames(names.ToList());
        }

        private static void Choose(UIHorizontalOptions option, int index)
        {
            if (option.optionNames.Count == 0) return;
            index = Mathf.Clamp(index, 0, option.optionNames.Count - 1);
            if (option.currentIndex != index) option.SelectOption(index);
        }

        private static Page BuildPage(SettingsMenu settings)
        {
            var basic = settings.pages[0];
            var basicColumn = basic.transform.Find("Column");
            var copy = UIKit.Clone(basic.gameObject, basic.transform.parent, "Online");
            var column = copy.transform.Find("Column") as RectTransform;
            var slider = copy.GetComponentsInChildren<UISliderOption>(true).FirstOrDefault();
            var option = copy.GetComponentsInChildren<UIHorizontalOptions>(true).FirstOrDefault();
            if (basicColumn == null || basicColumn.childCount < 2 || column == null || slider == null || option == null)
            {
                Object.Destroy(copy);
                Plugin.Log.LogWarning("Settings: couldn't build the Online page");
                return null;
            }
            var sliderRow = RowOf(slider.transform, column);
            var optionRow = RowOf(option.transform, column);
            for (int i = column.childCount - 1; i >= 0; i--)
            {
                var row = column.GetChild(i);
                if (row != sliderRow && row != optionRow) Object.DestroyImmediate(row.gameObject);
            }
            var rows = new List<Transform> { sliderRow, optionRow };
            for (int i = 0; i < 3; i++) rows.Add(UIKit.Clone(optionRow.gameObject, column, "Row " + (i + 2)).transform);

            var layout = column.GetComponent<VerticalLayoutGroup>();
            if (layout != null)
            {
                layout.childForceExpandHeight = false;
                layout.childAlignment = TextAnchor.UpperCenter;
            }
            float x0 = ContainerX(basicColumn.GetChild(0)), x1 = ContainerX(basicColumn.GetChild(1));
            for (int i = 0; i < rows.Count; i++)
            {
                rows[i].SetSiblingIndex(i);
                var element = rows[i].GetComponent<LayoutElement>() ?? rows[i].gameObject.AddComponent<LayoutElement>();
                element.minHeight = element.preferredHeight = RowHeight;
                if (rows[i].Find("Container") is RectTransform container)
                {
                    container.anchoredPosition = new Vector2(x0 - i * (x0 - x1) * RowHeight / GameRowHeight, container.anchoredPosition.y);
                }
            }
            float top = column.anchoredPosition.y + column.sizeDelta.y / 2f;
            float height = RowHeight * rows.Count;
            column.sizeDelta = new Vector2(column.sizeDelta.x, height);
            column.anchoredPosition = new Vector2(column.anchoredPosition.x, top - height / 2f);

            var page = new Page { Volume = slider };
            slider.gameObject.name = "Voice Chat";
            UIKit.SetText(slider.transform, "Label", "VOICE CHAT");
            slider.OnValueChanged.RemoveAllListeners();
            slider.OnValueChanged.AddListener(v => { if (!refreshing) Plugin.VoiceVolume.Value = Mathf.Clamp01(v); });

            page.Mute = MakeOption(rows[1], "Mute Mic", "MUTE MIC", i => Plugin.MuteMic.Value = i == 1);
            Names(page.Mute, new[] { "OFF", "ON" });
            page.Input = MakeOption(rows[2], "Input Device", "INPUT DEVICE", i => Plugin.InputDevice.Value = i < page.Inputs.Count ? page.Inputs[i] : "");
            page.Output = MakeOption(rows[3], "Output Device", "OUTPUT DEVICE", i => Plugin.OutputDevice.Value = i < page.Outputs.Count ? page.Outputs[i] : "");
            page.Stream = MakeOption(rows[4], "Stream Mode", "STREAM MODE", i => Plugin.StreamMode.Value = i == 1);
            Names(page.Stream, new[] { "OFF", "ON" });

            var chain = new[] { slider.selectable, page.Mute.GetComponent<Selectable>(), page.Input.GetComponent<Selectable>(),
                                page.Output.GetComponent<Selectable>(), page.Stream.GetComponent<Selectable>() };
            for (int i = 0; i < chain.Length; i++)
            {
                chain[i].navigation = new Navigation
                {
                    mode = Navigation.Mode.Explicit,
                    selectOnUp = chain[(i + chain.Length - 1) % chain.Length],
                    selectOnDown = chain[(i + 1) % chain.Length],
                };
            }

            var group = copy.GetComponent<CanvasGroup>();
            group.alpha = 0f;
            group.blocksRaycasts = false;
            settings.pages = settings.pages.Concat(new[] { group }).ToArray();
            page.Group = group;
            page.Index = settings.pages.Length - 1;
            return page;
        }

        private static float ContainerX(Transform row) => row.Find("Container") is RectTransform container ? container.anchoredPosition.x : 0f;

        private static UIHorizontalOptions MakeOption(Transform row, string name, string label, System.Action<int> changed)
        {
            var option = row.GetComponentInChildren<UIHorizontalOptions>(true);
            option.gameObject.name = name;
            UIKit.SetText(option.transform, "Label", label);
            option.OnValueChanged.RemoveAllListeners();
            option.OnValueChanged.AddListener(i => { if (!refreshing) changed(i); });
            var value = option.transform.Find("Value Window/Value Label")?.GetComponent<TMP_Text>();
            if (value != null)
            {
                value.enableWordWrapping = false;
                value.enableAutoSizing = true;
                value.fontSizeMax = value.fontSize;
                value.fontSizeMin = 40f;
                value.overflowMode = TextOverflowModes.Ellipsis;
            }
            return option;
        }

        private static Transform RowOf(Transform t, Transform column)
        {
            while (t != null && t.parent != column) t = t.parent;
            return t;
        }

        public static void ExtendPageControl(SettingsMenu settings)
        {
            var control = settings.pageControl;
            if (control == null || !pages.ContainsKey(settings) || control.pages == null || control.pages.Contains(PageTitle)) return;
            control.pages = control.pages.Concat(new[] { PageTitle }).ToArray();
            control.Build();
            control.SetPage(PageIndex(settings));
            if (settings.pageTitleText != null) settings.pageTitleText.text = control.GetCurrentPageTitle();
        }

        public static void AfterSelectDefault(SettingsMenu settings)
        {
            if (!pages.TryGetValue(settings, out var page) || PageIndex(settings) != page.Index) return;
            settings.menu.SetDefaultOption(page.Volume.selectable, page.Mute.GetComponent<Selectable>());
            page.Volume.selectable.Select();
        }

        public static bool BeforeReset(SettingsMenu settings)
        {
            if (!pages.TryGetValue(settings, out var page) || PageIndex(settings) != page.Index) return true;
            Popup.ShowPopup(Lean.Localization.LeanLocalization.GetTranslationText("Popup/Want to Restore"),
                new[] { Lean.Localization.LeanLocalization.GetTranslationText("Buttons/No"), Lean.Localization.LeanLocalization.GetTranslationText("Buttons/Yes") },
                0, 1154f, null, submit =>
                {
                    Popup.HidePopup();
                    if (submit)
                    {
                        Plugin.VoiceVolume.Value = 1f;
                        Plugin.MuteMic.Value = false;
                        Plugin.InputDevice.Value = "";
                        Plugin.OutputDevice.Value = "";
                        Plugin.StreamMode.Value = false;
                        Refresh(page);
                    }
                    AfterSelectDefault(settings);
                });
            return false;
        }
    }

    [HarmonyPatch(typeof(SettingsMenu))]
    internal static class OnlineSettingsPatches
    {
        [HarmonyPostfix, HarmonyPatch(nameof(SettingsMenu.Build))]
        private static void Build(SettingsMenu __instance) => OnlineSettings.AfterBuild(__instance);

        [HarmonyPostfix, HarmonyPatch("ConfigurePageControl")]
        private static void PageControl(SettingsMenu __instance) => OnlineSettings.ExtendPageControl(__instance);

        [HarmonyPostfix, HarmonyPatch("SelectDefaultElement")]
        private static void SelectDefault(SettingsMenu __instance) => OnlineSettings.AfterSelectDefault(__instance);

        [HarmonyPrefix, HarmonyPatch(nameof(SettingsMenu.ResetToDefaults))]
        private static bool Reset(SettingsMenu __instance) => OnlineSettings.BeforeReset(__instance);
    }
}
