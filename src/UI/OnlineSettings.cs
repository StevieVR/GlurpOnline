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
        private const float RowHeight = 448f;

        private sealed class Page
        {
            public CanvasGroup Group;
            public UISliderOption Volume;
            public UIHorizontalOptions Mute;
            public int Index;
        }

        private static readonly Dictionary<SettingsMenu, Page> pages = new Dictionary<SettingsMenu, Page>();

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
            page.Volume.SetValue(Mathf.Clamp01(Plugin.VoiceVolume.Value));
            if (page.Mute.currentIndex != (Plugin.MuteMic.Value ? 1 : 0)) page.Mute.SelectOption(Plugin.MuteMic.Value ? 1 : 0);
        }

        private static Page BuildPage(SettingsMenu settings)
        {
            var basic = settings.pages[0];
            var copy = UIKit.Clone(basic.gameObject, basic.transform.parent, "Online");
            var column = copy.transform.Find("Column");
            var volume = copy.GetComponentsInChildren<UISliderOption>(true).FirstOrDefault();
            var mute = copy.GetComponentsInChildren<UIHorizontalOptions>(true).FirstOrDefault();
            if (column == null || volume == null || mute == null)
            {
                Object.Destroy(copy);
                Plugin.Log.LogWarning("Settings: couldn't build the Online page");
                return null;
            }
            var keep = new[] { RowOf(volume.transform, column), RowOf(mute.transform, column) };
            for (int i = column.childCount - 1; i >= 0; i--)
            {
                var row = column.GetChild(i);
                if (!keep.Contains(row)) Object.DestroyImmediate(row.gameObject);
            }
            var layout = column.GetComponent<VerticalLayoutGroup>();
            if (layout != null)
            {
                layout.childForceExpandHeight = false;
                layout.childAlignment = TextAnchor.UpperCenter;
            }
            foreach (var row in keep)
            {
                var element = row.GetComponent<LayoutElement>() ?? row.gameObject.AddComponent<LayoutElement>();
                element.minHeight = element.preferredHeight = RowHeight;
            }

            volume.gameObject.name = "Voice Chat";
            UIKit.SetText(volume.transform, "Label", "VOICE CHAT");
            volume.OnValueChanged.RemoveAllListeners();
            volume.OnValueChanged.AddListener(v => Plugin.VoiceVolume.Value = Mathf.Clamp01(v));

            mute.gameObject.name = "Mute Mic";
            UIKit.SetText(mute.transform, "Label", "MUTE MIC");
            mute.useIntegerValues = false;
            mute.localiseOptionNames = false;
            mute.SetOptionNames(new List<string> { "OFF", "ON" });
            mute.OnValueChanged.RemoveAllListeners();
            mute.OnValueChanged.AddListener(i => Plugin.MuteMic.Value = i == 1);

            var a = volume.selectable;
            var b = mute.GetComponent<Selectable>();
            a.navigation = new Navigation { mode = Navigation.Mode.Explicit, selectOnUp = b, selectOnDown = b };
            b.navigation = new Navigation { mode = Navigation.Mode.Explicit, selectOnUp = a, selectOnDown = a };

            var group = copy.GetComponent<CanvasGroup>();
            group.alpha = 0f;
            group.blocksRaycasts = false;
            settings.pages = settings.pages.Concat(new[] { group }).ToArray();
            return new Page { Group = group, Volume = volume, Mute = mute, Index = settings.pages.Length - 1 };
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
                        page.Volume.SetValue(1f);
                        page.Mute.SelectOption(0);
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
