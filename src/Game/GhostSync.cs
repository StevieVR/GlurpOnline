using System;
using System.Collections.Generic;
using System.Linq;
using GlurpOnline.Net;
using TMPro;
using UnityEngine;

namespace GlurpOnline.Game
{
    internal static class GhostSync
    {
        private const string GuyPrefabPath = "Assets/Addressable/Prefabs/Guy/Guy.prefab";
        private const float SendInterval = 1f / 15f;
        private const float RenderDelay = 0.12f;
        private const float HideAfter = 3f;
        private const float GhostAlpha = 0.55f;
        private const float GhostPastel = 0.35f;

        private sealed class Snapshot
        {
            public float Time;
            public Vector3[] Poses;
        }

        private sealed class Ghost
        {
            public GameObject Root;
            public Guy Guy;
            public Transform[] Bodies;
            public Collider2D[] Colliders;
            public bool Solid;
            public bool CollidersOn;
            public TextMeshPro Tag;
            public Transform Head;
            public int ColorIndex = -1;
            public int Player = -1;
            public readonly List<Snapshot> Snapshots = new List<Snapshot>();
        }

        private const float PushStrength = 30f;
        private const float MaxOverlap = 0.35f;
        private static int isolatedLayer = -1;
        private static Guy pushedGuy;
        private static Collider2D[] pushedColliders;
        private static readonly Dictionary<Rigidbody2D, Vector2> pushes = new Dictionary<Rigidbody2D, Vector2>();

        private const short FirstGhostId = -32000;
        private static short nextGhostId = FirstGhostId;

        private static readonly Dictionary<ulong, Ghost> ghosts = new Dictionary<ulong, Ghost>();
        private static readonly List<UnityEngine.Object> loadedAssets = new List<UnityEngine.Object>();
        private static GameObject guyPrefab;
        private static List<string> bodyPaths;

        private static Guy sendingGuy;
        private static Transform[] sendingBodies;
        private static float nextSend;

        private static bool Enabled => OnlineGame.Active && OnlineGame.AllAtOnce && OnlineGame.SeeEachOther;

        private static bool EnsurePrefab()
        {
            if (guyPrefab != null) return true;
            try
            {
                guyPrefab = SyncAddressables.LoadAsset<GameObject>(GuyPrefabPath);
                if (guyPrefab != null) loadedAssets.Add(guyPrefab);
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning("Ghosts unavailable, couldn't load the guy prefab: " + e.Message);
            }
            if (guyPrefab == null) return false;
            bodyPaths = new List<string>();
            foreach (var rb in guyPrefab.GetComponentsInChildren<Rigidbody2D>(true)) bodyPaths.Add(PathOf(rb.transform, guyPrefab.transform));
            return true;
        }

        private static string PathOf(Transform t, Transform root)
        {
            var parts = new List<string>();
            for (var c = t; c != null && c != root; c = c.parent) parts.Add(c.name);
            parts.Reverse();
            return string.Join("/", parts.ToArray());
        }

        private static Transform[] MapBodies(Transform root)
        {
            var result = new Transform[bodyPaths.Count];
            for (int i = 0; i < bodyPaths.Count; i++) result[i] = bodyPaths[i].Length == 0 ? root : root.Find(bodyPaths[i]);
            return result;
        }

        public static void OnSceneLoading()
        {
            ghosts.Clear();
            nextGhostId = FirstGhostId;
            sendingGuy = null;
            sendingBodies = null;
            foreach (var a in loadedAssets) SyncAddressables.ReleaseLoadedAsset(a);
            loadedAssets.Clear();
            guyPrefab = null;
        }

        public static void OnLocalTurnFinished() { }

        public static void Update()
        {
            if (!Enabled) return;
            UpdateSending();
            float now = Time.realtimeSinceStartup;
            foreach (var g in ghosts.Values) Animate(g, now);
        }

        private static void UpdateSending()
        {
            var s = Session.Current;
            var em = GameManager.currentEvent;
            if (s == null || em == null || em.guy == null || !OnlineGame.IsLocalTurn || !OnlineGame.SceneLoaded) return;
            float now = Time.realtimeSinceStartup;
            if (now < nextSend) return;
            nextSend = now + SendInterval;
            if (!EnsurePrefab()) return;
            if (sendingGuy != em.guy)
            {
                sendingGuy = em.guy;
                sendingBodies = MapBodies(em.guy.transform);
            }

            var p = TurnManager.CurrentPlayer();
            var w = new PacketWriter(Msg.GhostState, s.LocalId);
            w.Write((short)OnlineGame.TurnRound);
            w.Write((short)(p?.playerNumber ?? -1));
            w.Write((short)(p != null ? Array.IndexOf(GameManager.singleton.playerColors, p.colorScheme) : -1));
            w.Write((byte)sendingBodies.Length);
            foreach (var t in sendingBodies)
            {
                var pos = t != null ? t.position : Vector3.zero;
                w.Write(pos.x);
                w.Write(pos.y);
                w.Write(t != null ? t.eulerAngles.z : 0f);
            }
            s.Broadcast(w);
        }

        public static void OnGhostState(ulong origin, PacketReader r)
        {
            if (!Enabled || !OnlineGame.SceneLoaded || GameManager.currentEvent == null) return;
            int round = r.ReadShort();
            int player = r.ReadShort();
            int colorIndex = r.ReadShort();
            int count = r.ReadByte();
            if (round != OnlineGame.TurnRound || !EnsurePrefab() || count != bodyPaths.Count) return;
            var poses = new Vector3[count];
            for (int i = 0; i < count; i++) poses[i] = new Vector3(r.ReadFloat(), r.ReadFloat(), r.ReadFloat());

            if (!ghosts.TryGetValue(origin, out var ghost) || ghost.Root == null)
            {
                ghost = Spawn(poses[0]);
                if (ghost == null) return;
                ghosts[origin] = ghost;
            }
            if (ghost.Player != player || ghost.ColorIndex != colorIndex) Dress(ghost, player, colorIndex);
            var snaps = ghost.Snapshots;
            if (ghost.CollidersOn && snaps.Count > 0 && (snaps[snaps.Count - 1].Poses[0] - poses[0]).sqrMagnitude > 9f) SetColliders(ghost, false);
            snaps.Add(new Snapshot { Time = Time.realtimeSinceStartup, Poses = poses });
            if (snaps.Count > 12) snaps.RemoveAt(0);
            if (!ghost.Root.activeSelf) ghost.Root.SetActive(true);
        }

        private static Ghost Spawn(Vector3 at)
        {
            var holder = new GameObject("Glurp Online Ghost Holder");
            holder.SetActive(false);
            var root = UnityEngine.Object.Instantiate(guyPrefab, new Vector3(at.x, at.y, 0f), Quaternion.identity, holder.transform);
            root.name = "Glurp Online Ghost";
            foreach (var d in root.GetComponentsInChildren<GlurpAPI.GlurpDynamicObject>(true))
            {
                if (d.sceneID != 0) continue;
                d.sceneID = nextGhostId;
                nextGhostId = nextGhostId > short.MinValue + 1 ? (short)(nextGhostId - 1) : FirstGhostId;
            }
            root.transform.SetParent(null, true);
            UnityEngine.Object.Destroy(holder);

            var guy = root.GetComponent<Guy>();
            if (guy != null)
            {
                guy.isInputEnabled = false;
                guy.shouldDisableInputOnStart = false;
                guy.disableEmotes = true;
            }
            bool solid = OnlineGame.Collide;
            var bodies = root.GetComponentsInChildren<Rigidbody2D>(true);
            var colliders = root.GetComponentsInChildren<Collider2D>(true).Where(c => !c.isTrigger).ToArray();
            foreach (var rb in bodies)
            {
                rb.simulated = solid;
                if (solid) rb.bodyType = RigidbodyType2D.Kinematic;
            }
            foreach (var c in root.GetComponentsInChildren<Collider2D>(true)) c.enabled = false;
            foreach (var a in root.GetComponentsInChildren<AudioSource>(true)) a.enabled = false;
            foreach (var rend in root.GetComponentsInChildren<Renderer>(true)) rend.sortingOrder -= 20;

            var ghost = new Ghost { Root = root, Guy = guy, Bodies = MapBodies(root.transform), Solid = solid, Colliders = colliders };
            if (solid)
            {
                int layer = IsolatedLayer();
                foreach (var c in colliders) c.gameObject.layer = layer;
            }
            ghost.Head = guy != null && guy.headRenderer != null ? guy.headRenderer.transform : root.transform;
            ghost.Tag = MakeNameTag(root.transform);
            return ghost;
        }

        private static int IsolatedLayer()
        {
            if (isolatedLayer >= 0) return isolatedLayer;
            for (int l = 31; l >= 8 && isolatedLayer < 0; l--) if (string.IsNullOrEmpty(LayerMask.LayerToName(l))) isolatedLayer = l;
            if (isolatedLayer < 0) isolatedLayer = 31;
            for (int l = 0; l < 32; l++) Physics2D.IgnoreLayerCollision(isolatedLayer, l, true);
            return isolatedLayer;
        }

        private static void SetColliders(Ghost ghost, bool on)
        {
            ghost.CollidersOn = on;
            foreach (var c in ghost.Colliders) if (c != null) c.enabled = on;
        }

        private static void UpdateSolid(Ghost ghost)
        {
            if (!ghost.Solid || ghost.CollidersOn || !ghost.Root.activeSelf) return;
            var guy = GameManager.currentEvent != null ? GameManager.currentEvent.guy : null;
            if (guy == null) return;
            if (!Bounds(guy.gameObject, out var mine) || !Bounds(ghost.Root, out var theirs)) return;
            mine.Expand(0.5f);
            if (!mine.Intersects(theirs)) SetColliders(ghost, true);
        }

        private static bool Bounds(GameObject go, out Bounds bounds)
        {
            bounds = default;
            bool any = false;
            foreach (var r in go.GetComponentsInChildren<SpriteRenderer>())
            {
                if (!r.enabled) continue;
                if (any) bounds.Encapsulate(r.bounds); else { bounds = r.bounds; any = true; }
            }
            return any;
        }

        private static TextMeshPro MakeNameTag(Transform parent)
        {
            var go = new GameObject("Ghost Name");
            go.transform.SetParent(parent, false);
            var tag = go.AddComponent<TextMeshPro>();
            var template = GameMenu.singleton != null ? GameMenu.singleton.playerReadyNameText : null;
            if (template != null) tag.font = template.font;
            tag.fontSize = 3.2f;
            tag.alignment = TextAlignmentOptions.Center;
            tag.enableWordWrapping = false;
            tag.fontStyle = FontStyles.UpperCase;
            tag.rectTransform.sizeDelta = new Vector2(12f, 2f);
            tag.sortingOrder = 500;
            return tag;
        }

        private static void Dress(Ghost ghost, int player, int colorIndex)
        {
            ghost.Player = player;
            ghost.ColorIndex = colorIndex;
            var colors = GameManager.singleton.playerColors;
            var source = colorIndex >= 0 && colorIndex < colors.Length ? colors[colorIndex] : colors[0];
            bool real = OnlineGame.Sharing == SharedObjects.Mode.Everything;
            var scheme = real ? source : new GameManager.PlayerColorScheme
            {
                primary = Ghostly(source.primary),
                secondary = Ghostly(source.secondary),
                skin = Ghostly(source.skin),
                skinTrailModifier = source.skinTrailModifier,
            };
            if (ghost.Guy != null)
            {
                if (ghost.Guy.player == null) ghost.Guy.player = new GameManager.Player();
                ghost.Guy.player.colorScheme = scheme;
                ghost.Guy.ApplyColorScheme(scheme);
            }
            if (!real)
            {
                foreach (var sr in ghost.Root.GetComponentsInChildren<SpriteRenderer>(true))
                {
                    var c = sr.color;
                    if (c.a > GhostAlpha) sr.color = new Color(c.r, c.g, c.b, GhostAlpha);
                }
            }
            var entry = OnlineGame.Entry(player);
            if (ghost.Tag != null)
            {
                ghost.Tag.text = entry != null ? entry.Name : "";
                ghost.Tag.color = new Color(source.primary.r, source.primary.g, source.primary.b, 0.9f);
            }
        }

        private static Color Ghostly(Color c)
        {
            var pastel = Color.Lerp(c, Color.white, GhostPastel);
            pastel.a = GhostAlpha;
            return pastel;
        }

        public static void FixedUpdate()
        {
            if (!Enabled || !OnlineGame.Collide) return;
            var guy = GameManager.currentEvent != null ? GameManager.currentEvent.guy : null;
            if (guy == null) return;
            if (pushedGuy != guy)
            {
                pushedGuy = guy;
                pushedColliders = guy.GetComponentsInChildren<Collider2D>().Where(c => !c.isTrigger).ToArray();
            }
            pushes.Clear();
            foreach (var g in ghosts.Values)
            {
                if (!g.Solid || !g.CollidersOn || g.Root == null || !g.Root.activeSelf) continue;
                foreach (var mine in pushedColliders)
                {
                    var body = mine != null && mine.enabled ? mine.attachedRigidbody : null;
                    if (body == null || body.bodyType != RigidbodyType2D.Dynamic) continue;
                    foreach (var theirs in g.Colliders)
                    {
                        if (theirs == null || !theirs.enabled) continue;
                        var d = Physics2D.Distance(mine, theirs);
                        if (!d.isValid || !d.isOverlapped) continue;
                        var away = d.normal;
                        if (Vector2.Dot(away, mine.bounds.center - theirs.bounds.center) < 0f) away = -away;
                        pushes.TryGetValue(body, out var sum);
                        pushes[body] = sum + away * Mathf.Min(-d.distance, MaxOverlap);
                    }
                }
            }
            foreach (var p in pushes)
            {
                var push = Vector2.ClampMagnitude(p.Value, MaxOverlap);
                p.Key.AddForce(push * (PushStrength * p.Key.mass));
            }
        }

        private static void Animate(Ghost g, float now)
        {
            if (g.Root == null || g.Snapshots.Count == 0) return;
            var last = g.Snapshots[g.Snapshots.Count - 1];
            if (now - last.Time > HideAfter)
            {
                if (g.Root.activeSelf) g.Root.SetActive(false);
                return;
            }

            float t = now - RenderDelay;
            Snapshot a = g.Snapshots[0], b = last;
            for (int i = g.Snapshots.Count - 1; i > 0; i--)
            {
                if (g.Snapshots[i - 1].Time <= t)
                {
                    a = g.Snapshots[i - 1];
                    b = g.Snapshots[i];
                    break;
                }
            }
            float f = b.Time > a.Time ? Mathf.Clamp01((t - a.Time) / (b.Time - a.Time)) : 1f;
            for (int i = 0; i < g.Bodies.Length; i++)
            {
                var body = g.Bodies[i];
                if (body == null) continue;
                var pa = a.Poses[i];
                var pb = b.Poses[i];
                var pose = new Vector3(Mathf.Lerp(pa.x, pb.x, f), Mathf.Lerp(pa.y, pb.y, f), Mathf.LerpAngle(pa.z, pb.z, f));
                body.position = new Vector3(pose.x, pose.y, body.position.z);
                body.rotation = Quaternion.Euler(0f, 0f, pose.z);
            }
            UpdateSolid(g);
            if (g.Tag != null && g.Head != null)
            {
                g.Tag.transform.position = g.Head.position + new Vector3(0f, 1.6f, -0.1f);
                g.Tag.transform.rotation = Quaternion.identity;
            }
        }
    }
}
