using System;
using System.Collections.Generic;
using System.Reflection;
using GlurpAPI;
using TMPro;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.UI;

namespace GlurpOnline.UI
{
    internal static class UIKit
    {
        public static readonly Color Pink = new Color(1f, 0.2f, 0.4f, 1f);
        public static readonly Color Mint = new Color(0f, 1f, 0.8f, 1f);
        public static readonly Color Purple = new Color(0.431f, 0.431f, 0.831f, 1f);
        public static readonly Color Grey = new Color(0.302f, 0.302f, 0.302f, 1f);
        public static readonly Color TabPink = new Color(0.973f, 0.792f, 0.973f, 1f);

        public static GameObject Clone(GameObject template, Transform parent, string name)
        {
            var go = UnityEngine.Object.Instantiate(template, parent, false);
            go.name = name;
            Detach(go);
            return go;
        }

        public static T Clone<T>(T template, Transform parent, string name) where T : Component
        {
            return Clone(template.gameObject, parent, name).GetComponent<T>();
        }

        public static void Detach(GameObject root)
        {
            foreach (var c in root.GetComponentsInChildren<Component>(true))
            {
                if (c == null) continue;
                if (c.GetType().Name.StartsWith("LeanLocalized", StringComparison.Ordinal))
                {
                    UnityEngine.Object.DestroyImmediate(c);
                }
            }
            foreach (var c in root.GetComponentsInChildren<Component>(true))
            {
                if (c != null) DisableExternalListeners(c, root.transform);
            }
        }

        private static readonly Dictionary<Type, List<FieldInfo>> eventFields = new Dictionary<Type, List<FieldInfo>>();

        private static List<FieldInfo> EventFields(Type type)
        {
            if (eventFields.TryGetValue(type, out var list)) return list;
            list = new List<FieldInfo>();
            for (var t = type; t != null && t != typeof(MonoBehaviour) && t != typeof(Component); t = t.BaseType)
            {
                foreach (var f in t.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
                {
                    if (typeof(UnityEventBase).IsAssignableFrom(f.FieldType)) list.Add(f);
                }
            }
            eventFields[type] = list;
            return list;
        }

        private static void DisableExternalListeners(Component c, Transform root)
        {
            foreach (var f in EventFields(c.GetType()))
            {
                if (!(f.GetValue(c) is UnityEventBase ev)) continue;
                for (int i = 0; i < ev.GetPersistentEventCount(); i++)
                {
                    var target = ev.GetPersistentTarget(i);
                    Transform t = target is Component tc ? tc.transform : target is GameObject tg ? tg.transform : null;
                    if (t == null || !t.IsChildOf(root)) ev.SetPersistentListenerState(i, UnityEventCallState.Off);
                }
            }
        }

        public static Transform Find(Transform root, string path)
        {
            var t = root.Find(path);
            if (t == null) Plugin.Log.LogWarning($"UI: '{path}' not found under {root.name}");
            return t;
        }

        public static Transform FindDeep(Transform root, string name)
        {
            if (root.name == name) return root;
            for (int i = 0; i < root.childCount; i++)
            {
                var r = FindDeep(root.GetChild(i), name);
                if (r != null) return r;
            }
            return null;
        }

        public static void SetText(Transform root, string path, string text)
        {
            var t = path == null ? root : Find(root, path);
            var tmp = t != null ? t.GetComponent<TMP_Text>() : null;
            if (tmp != null) SetText(tmp, text);
        }

        public static void SetText(TMP_Text tmp, string text)
        {
            if (tmp == null) return;
            tmp.text = "";
            tmp.text = text;
        }

        public static void SetActive(Transform root, string path, bool active)
        {
            var t = root.Find(path);
            if (t != null) t.gameObject.SetActive(active);
        }

        public static RectTransform Rect(Component c) => (RectTransform)c.transform;

        public static void Place(Component c, Vector2 anchor, Vector2 pivot, Vector2 pos)
        {
            var rt = Rect(c);
            rt.anchorMin = anchor;
            rt.anchorMax = anchor;
            rt.pivot = pivot;
            rt.anchoredPosition = pos;
        }

        public static void ClearListeners(Button b)
        {
            b.onClick.RemoveAllListeners();
            for (int i = 0; i < b.onClick.GetPersistentEventCount(); i++) b.onClick.SetPersistentListenerState(i, UnityEventCallState.Off);
        }

        public static void OnClick(Button b, Action action)
        {
            ClearListeners(b);
            b.onClick.AddListener(() =>
            {
                try { action(); }
                catch (Exception e) { Plugin.Log.LogError(e); }
            });
        }

        public static void ConfigureChip(Transform chip, GlurpControllerManager.ButtonLocation button, string label, Action onClick)
        {
            if (chip == null) return;
            chip.gameObject.SetActive(true);
            var icon = chip.GetComponentInChildren<GlurpControllerButtonIcon>(true);
            if (icon != null && icon.buttonType != button)
            {
                icon.setType(button);
            }
            SetText(chip, "Text", label);
            var b = chip.GetComponent<Button>();
            if (b != null)
            {
                if (onClick != null) OnClick(b, onClick);
                else ClearListeners(b);
            }
            LayoutRebuilder.MarkLayoutForRebuild(Rect(chip));
        }

        public static void LinkVertical(IList<Selectable> items)
        {
            for (int i = 0; i < items.Count; i++)
            {
                var nav = new Navigation
                {
                    mode = Navigation.Mode.Explicit,
                    selectOnUp = i > 0 ? items[i - 1] : null,
                    selectOnDown = i < items.Count - 1 ? items[i + 1] : null,
                };
                items[i].navigation = nav;
            }
        }

        public static void LinkHorizontal(IList<Selectable> items)
        {
            for (int i = 0; i < items.Count; i++)
            {
                var nav = items[i].navigation;
                nav.mode = Navigation.Mode.Explicit;
                nav.selectOnLeft = i > 0 ? items[i - 1] : null;
                nav.selectOnRight = i < items.Count - 1 ? items[i + 1] : null;
                items[i].navigation = nav;
            }
        }

        public static Sprite LoadEventIcon(string type, bool large = false)
        {
            try
            {
                return SyncAddressables.LoadAsset<Sprite>($"Assets/Addressable/Sprites/Event Icons/eventicon_{type}{(large ? "_lg" : "")}.png");
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning("Icon load failed: " + e.Message);
                return null;
            }
        }
    }
}
