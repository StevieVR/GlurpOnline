using System.Collections;
using System.Collections.Generic;
using TMPro;
using UnityEngine;

namespace GlurpOnline.UI
{
    internal static class Toasts
    {
        private const float Duration = 3.2f;
        private const float RowHeight = 340f;

        private sealed class Toast
        {
            public RectTransform Rect;
            public CanvasGroup Group;
            public float Born;
        }

        private static GameObject template;
        private static readonly List<Toast> live = new List<Toast>();
        private static readonly Queue<string> queued = new Queue<string>();

        public static void Show(string message)
        {
            Plugin.Log.LogInfo("[toast] " + message);
            queued.Enqueue(message);
        }

        private static bool EnsureTemplate()
        {
            if (template != null) return true;
            var menu = GameMenu.singleton;
            var popup = Popup.singleton;
            if (menu == null || menu.replayUI == null || popup == null) return false;
            var bar = menu.replayUI.transform.Find("Replay Text Bar");
            if (bar == null) return false;
            template = UIKit.Clone(bar.gameObject, popup.transform.parent, "Glurp Online Toast");
            var anim = template.GetComponent<Animator>();
            if (anim != null) Object.DestroyImmediate(anim);
            template.SetActive(false);
            return true;
        }

        public static void Update()
        {
            float now = Time.unscaledTime;
            while (queued.Count > 0 && live.Count < 4 && EnsureTemplate())
            {
                var go = Object.Instantiate(template, template.transform.parent, false);
                go.name = "Toast";
                go.SetActive(true);
                var text = go.GetComponentInChildren<TMP_Text>(true);
                UIKit.SetText(text, queued.Dequeue().ToUpperInvariant());
                var rt = (RectTransform)go.transform;
                rt.anchorMin = rt.anchorMax = new Vector2(1f, 1f);
                rt.pivot = new Vector2(1f, 1f);
                var group = go.GetComponent<CanvasGroup>() ?? go.AddComponent<CanvasGroup>();
                group.alpha = 0f;
                group.blocksRaycasts = false;
                live.Add(new Toast { Rect = rt, Group = group, Born = now });
            }

            for (int i = live.Count - 1; i >= 0; i--)
            {
                var t = live[i];
                if (t.Rect == null) { live.RemoveAt(i); continue; }
                float age = now - t.Born;
                if (age > Duration + 0.3f)
                {
                    Object.Destroy(t.Rect.gameObject);
                    live.RemoveAt(i);
                    continue;
                }
                float fadeIn = Mathf.Clamp01(age / 0.2f);
                float fadeOut = Mathf.Clamp01((Duration + 0.3f - age) / 0.3f);
                t.Group.alpha = Mathf.Min(fadeIn, fadeOut);
            }
            for (int i = 0; i < live.Count; i++)
            {
                var target = new Vector2(-208f + (1f - live[i].Group.alpha) * 120f, -200f - i * RowHeight);
                live[i].Rect.anchoredPosition = Vector2.Lerp(live[i].Rect.anchoredPosition, target, 0.35f);
            }
        }
    }
}
