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
        private const float TagHeight = 0.75f;

        private const float FollowGain = 15f;
        private const float MaxFollowSpeed = 30f;
        private const float SnapDistance = 3f;

        private sealed class Snapshot
        {
            public float Time;
            public Vector3[] Poses;
            public Dictionary<int, Vector3[]> Own;
        }

        private sealed class OwnObject
        {
            public int Key;
            public Transform Root;
            public Rigidbody2D[] Bodies;
            public GameObject Template;
        }

        private sealed class OwnCopy
        {
            public GameObject Root;
            public Transform[] Bodies;
        }

        private sealed class Ghost
        {
            public ulong Owner;
            public GameObject Root;
            public Guy Guy;
            public Transform[] Bodies;
            public Rigidbody2D[] Rigidbodies;
            public Collider2D[] Colliders;
            public bool Solid;
            public bool CollidersOn;
            public Vector3[] Target;
            public Vector2[] TargetVelocity;
            public bool HasTarget;
            public int SpawnFrame;
            public bool SponsorApplied;
            public bool HatApplied;
            public GlurpAPI.Hat.GlurpHat Hat;
            public readonly List<KeyValuePair<Collider2D, Collider2D>> Pending = new List<KeyValuePair<Collider2D, Collider2D>>();
            public readonly HashSet<Collider2D> KeptOff = new HashSet<Collider2D>();
            public int KeptOffBy;
            public GameObject OwnHolder;
            public readonly Dictionary<int, OwnCopy> OwnCopies = new Dictionary<int, OwnCopy>();
            public TextMeshPro Tag;
            public SpriteRenderer SpeakingDot;
            public Transform Head;
            public GameManager.PlayerColorScheme Scheme;
            public int ColorIndex = -1;
            public int Player = -1;
            public readonly List<Snapshot> Snapshots = new List<Snapshot>();
        }

        private const short FirstGhostId = -32000;
        private static short nextGhostId = FirstGhostId;

        private static readonly Dictionary<ulong, Ghost> ghosts = new Dictionary<ulong, Ghost>();
        private static readonly Dictionary<Rigidbody2D, KeyValuePair<Ghost, int>> ghostBodies = new Dictionary<Rigidbody2D, KeyValuePair<Ghost, int>>();
        private static readonly List<UnityEngine.Object> loadedAssets = new List<UnityEngine.Object>();
        private static readonly Dictionary<string, GlurpAPI.Hat.HatList> hatLists = new Dictionary<string, GlurpAPI.Hat.HatList>();
        private static GameObject guyPrefab;
        private static List<string> bodyPaths;
        private static int physicalLayer = -1;

        private static Guy sendingGuy;
        private static Transform[] sendingBodies;
        private static float nextSend;

        private sealed class Hold
        {
            public ulong Target;
            public int Body;
        }

        private static readonly Hold[] holds = { new Hold(), new Hold() };
        private static readonly Dictionary<long, FixedJoint2D> heldBy = new Dictionary<long, FixedJoint2D>();

        private const float OwnObjectAlpha = 0.6f;
        private const float OwnLookFor = 5f;
        private static List<OwnObject> ownObjects;
        private static float ownLookStarted = -1f, nextOwnLook;

        private const float OwnObjectsInterval = 0.25f;
        private static float nextOwnObjects;
        private static readonly HashSet<Collider2D> ownColliders = new HashSet<Collider2D>();
        private static readonly Dictionary<Collider2D, bool> playerParts = new Dictionary<Collider2D, bool>();

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
            ghostBodies.Clear();
            heldBy.Clear();
            ownColliders.Clear();
            playerParts.Clear();
            ownObjects = null;
            ownLookStarted = -1f;
            nextOwnLook = 0f;
            foreach (var h in holds) { h.Target = 0; h.Body = -1; }
            nextGhostId = FirstGhostId;
            sendingGuy = null;
            sendingBodies = null;
            physicalLayer = -1;
            foreach (var a in loadedAssets) SyncAddressables.ReleaseLoadedAsset(a);
            loadedAssets.Clear();
            hatLists.Clear();
            guyPrefab = null;
        }

        public static void OnLocalTurnFinished() { }

        public static void Update()
        {
            if (!Enabled) return;
            UpdateSending();
            float now = Time.realtimeSinceStartup;
            foreach (var g in ghosts.Values) Animate(g, now);
            KeepOffOwnObjects(now);
        }

        private static void KeepOffOwnObjects(float now)
        {
            if (now < nextOwnObjects || ghosts.Count == 0) return;
            nextOwnObjects = now + OwnObjectsInterval;
            ownColliders.Clear();
            foreach (var c in UnityEngine.Object.FindObjectsOfType<Collider2D>())
            {
                if (c.isTrigger || !c.isActiveAndEnabled) continue;
                var rb = c.attachedRigidbody;
                if (rb == null || rb.bodyType != RigidbodyType2D.Dynamic || !rb.simulated || SharedObjects.IsShared(rb)) continue;
                if (!playerParts.TryGetValue(c, out bool part)) playerParts[c] = part = SharedObjects.IsPlayerPart(c.transform);
                if (!part) ownColliders.Add(c);
            }
            foreach (var g in ghosts.Values)
            {
                if (g.Root == null || !g.Root.activeInHierarchy) continue;
                var theirs = g.Root.GetComponentsInChildren<Collider2D>().Where(c => c.enabled && !c.isTrigger).ToArray();
                if (theirs.Length != g.KeptOffBy)
                {
                    g.KeptOff.Clear();
                    g.KeptOffBy = theirs.Length;
                }
                foreach (var c in g.KeptOff.ToList())
                {
                    if (c != null && ownColliders.Contains(c)) continue;
                    g.KeptOff.Remove(c);
                    if (c != null && c.isActiveAndEnabled && SharedObjects.IsShared(c.attachedRigidbody)) foreach (var t in theirs) Physics2D.IgnoreCollision(c, t, false);
                }
                foreach (var c in ownColliders)
                {
                    if (!g.KeptOff.Add(c)) continue;
                    foreach (var t in theirs) Physics2D.IgnoreCollision(c, t, true);
                }
            }
        }

        private static List<OwnObject> OwnObjects()
        {
            if (ownObjects != null && ownObjects.Count > 0) return ownObjects;
            if (!OnlineGame.TurnBegun) return ownObjects;
            float now = Time.realtimeSinceStartup;
            if (ownLookStarted < 0f) ownLookStarted = now;
            if (ownObjects != null && (now < nextOwnLook || now - ownLookStarted > OwnLookFor)) return ownObjects;
            nextOwnLook = now + 0.5f;

            var found = new List<OwnObject>();
            var holder = new GameObject("Glurp Online Own Objects");
            holder.SetActive(false);
            var taken = new HashSet<Rigidbody2D>();
            var types = SharedObjects.OwnTypes;
            for (int t = 0; t < types.Length; t++)
            {
                var roots = UnityEngine.Object.FindObjectsOfType(types[t]).Cast<Component>()
                    .Where(c => !SharedObjects.IsPlayerPart(c.transform))
                    .OrderBy(c => PathOf(c.transform, null), StringComparer.Ordinal)
                    .ThenBy(c => Mathf.Round(c.transform.position.x * 100f)).ThenBy(c => Mathf.Round(c.transform.position.y * 100f))
                    .ToList();
                for (int i = 0; i < roots.Count; i++)
                {
                    var bodies = roots[i].GetComponentsInChildren<Rigidbody2D>(true);
                    if (bodies.Length == 0 || bodies.Any(taken.Contains)) continue;
                    foreach (var rb in bodies) taken.Add(rb);
                    found.Add(new OwnObject { Key = (t << 16) | i, Root = roots[i].transform, Bodies = bodies, Template = MakeTemplate(roots[i], holder.transform) });
                }
            }
            if (found.Count == 0) UnityEngine.Object.Destroy(holder);
            ownObjects = found;
            return ownObjects;
        }

        private static GameObject MakeTemplate(Component source, Transform holder)
        {
            var copy = UnityEngine.Object.Instantiate(source.gameObject, source.transform.position, source.transform.rotation, holder);
            copy.name = source.gameObject.name + " (other player)";
            foreach (var field in source.GetType().GetFields(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic))
            {
                if (field.FieldType != typeof(GameObject) || !(field.GetValue(source) is GameObject marker) || marker == null) continue;
                if (marker.transform == source.transform || !marker.transform.IsChildOf(source.transform) || marker.GetComponentInChildren<Rigidbody2D>(true) != null) continue;
                var twin = copy.transform.Find(PathOf(marker.transform, source.transform));
                if (twin != null) twin.gameObject.SetActive(false);
            }
            for (int pass = 0; pass < 4; pass++)
            {
                var scripts = copy.GetComponentsInChildren<MonoBehaviour>(true);
                if (scripts.Length == 0) break;
                for (int i = scripts.Length - 1; i >= 0; i--) if (scripts[i] != null) UnityEngine.Object.DestroyImmediate(scripts[i]);
            }
            foreach (var j in copy.GetComponentsInChildren<Joint2D>(true)) UnityEngine.Object.DestroyImmediate(j);
            foreach (var c in copy.GetComponentsInChildren<Collider2D>(true)) c.enabled = false;
            foreach (var rb in copy.GetComponentsInChildren<Rigidbody2D>(true)) rb.simulated = false;
            foreach (var a in copy.GetComponentsInChildren<AudioSource>(true)) a.enabled = false;
            foreach (var rend in copy.GetComponentsInChildren<Renderer>(true))
            {
                rend.sortingOrder -= 20;
                if (rend is SpriteRenderer sr) sr.color = SeeThrough(sr.color);
                else if (rend is LineRenderer lr) { lr.startColor = SeeThrough(lr.startColor); lr.endColor = SeeThrough(lr.endColor); }
                else if (rend.sharedMaterial != null && rend.sharedMaterial.HasProperty("_Color")) rend.material.color = SeeThrough(rend.material.color);
            }
            return copy;
        }

        private static Color SeeThrough(Color c) => new Color(c.r, c.g, c.b, c.a * OwnObjectAlpha);

        private static OwnCopy CopyOwnObject(Ghost g, int key)
        {
            var source = OwnObjects()?.FirstOrDefault(o => o.Key == key);
            if (source == null || source.Template == null) return null;
            if (g.OwnHolder == null) g.OwnHolder = new GameObject("Glurp Online Ghost Objects");
            var t = source.Template.transform;
            var root = UnityEngine.Object.Instantiate(source.Template, t.position, t.rotation, g.OwnHolder.transform);
            root.name = source.Template.name;
            return new OwnCopy { Root = root, Bodies = root.GetComponentsInChildren<Rigidbody2D>(true).Select(rb => rb.transform).ToArray() };
        }

        private static void WriteOwnObjects(PacketWriter w)
        {
            var own = OwnObjects();
            w.Write((byte)(own?.Count ?? 0));
            if (own == null) return;
            foreach (var o in own)
            {
                w.Write(o.Key);
                w.Write(o.Root != null && o.Root.gameObject.activeInHierarchy);
                w.Write((byte)o.Bodies.Length);
                foreach (var rb in o.Bodies)
                {
                    var t = rb != null ? rb.transform : null;
                    var pos = t != null ? t.position : Vector3.zero;
                    w.Write(pos.x);
                    w.Write(pos.y);
                    w.Write(t != null ? t.eulerAngles.z : 0f);
                }
            }
        }

        private static Dictionary<int, Vector3[]> ReadOwnObjects(PacketReader r)
        {
            if (r.Remaining <= 0) return null;
            int count = r.ReadByte();
            var own = new Dictionary<int, Vector3[]>(count);
            for (int i = 0; i < count; i++)
            {
                int key = r.ReadInt();
                bool shown = r.ReadBool();
                var poses = new Vector3[r.ReadByte()];
                for (int j = 0; j < poses.Length; j++) poses[j] = new Vector3(r.ReadFloat(), r.ReadFloat(), r.ReadFloat());
                if (shown) own[key] = poses;
            }
            return own;
        }

        private static void AnimateOwnObjects(Ghost g, Snapshot a, Snapshot b, float f)
        {
            foreach (var pair in g.OwnCopies)
            {
                var copy = pair.Value;
                if (copy.Root == null) continue;
                Vector3[] pb = null, pa = null;
                bool shown = b.Own != null && b.Own.TryGetValue(pair.Key, out pb);
                if (copy.Root.activeSelf != shown) copy.Root.SetActive(shown);
                if (!shown) continue;
                if (a.Own == null || !a.Own.TryGetValue(pair.Key, out pa) || pa.Length != pb.Length) pa = pb;
                int count = Mathf.Min(copy.Bodies.Length, pb.Length);
                for (int i = 0; i < count; i++)
                {
                    var body = copy.Bodies[i];
                    if (body == null) continue;
                    body.position = new Vector3(Mathf.Lerp(pa[i].x, pb[i].x, f), Mathf.Lerp(pa[i].y, pb[i].y, f), body.position.z);
                    body.rotation = Quaternion.Euler(0f, 0f, Mathf.LerpAngle(pa[i].z, pb[i].z, f));
                }
            }
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
            WriteOwnObjects(w);
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
            var own = ReadOwnObjects(r);

            if (!ghosts.TryGetValue(origin, out var ghost) || ghost.Root == null)
            {
                ghost = Spawn(origin, poses[0]);
                if (ghost == null) return;
                ghosts[origin] = ghost;
            }
            if (ghost.Player != player || ghost.ColorIndex != colorIndex) Dress(ghost, player, colorIndex);
            if (own != null)
            {
                foreach (var key in own.Keys)
                {
                    if (ghost.OwnCopies.TryGetValue(key, out var existing) && existing.Root != null) continue;
                    var made = CopyOwnObject(ghost, key);
                    if (made != null) ghost.OwnCopies[key] = made;
                }
            }
            var snaps = ghost.Snapshots;
            bool jumped = snaps.Count > 0 && (snaps[snaps.Count - 1].Poses[0] - poses[0]).sqrMagnitude > 9f;
            snaps.Add(new Snapshot { Time = Time.realtimeSinceStartup, Poses = poses, Own = own });
            if (snaps.Count > 12) snaps.RemoveAt(0);
            if (!ghost.Root.activeSelf)
            {
                ghost.Root.SetActive(true);
                if (ghost.OwnHolder != null) ghost.OwnHolder.SetActive(true);
                ghost.KeptOff.Clear();
                if (ghost.Solid) SetColliders(ghost, true);
            }
            else if (jumped && ghost.Solid)
            {
                ReleaseGrabsOf(ghost);
                KeepApartUntilClear(ghost);
            }
        }

        private static Ghost Spawn(ulong owner, Vector3 at)
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
            var colliders = root.GetComponentsInChildren<Collider2D>(true).Where(c => !c.isTrigger).ToArray();
            foreach (var rb in root.GetComponentsInChildren<Rigidbody2D>(true)) rb.simulated = solid;
            foreach (var c in root.GetComponentsInChildren<Collider2D>(true)) c.enabled = false;
            foreach (var a in root.GetComponentsInChildren<AudioSource>(true)) a.enabled = false;
            foreach (var rend in root.GetComponentsInChildren<Renderer>(true)) rend.sortingOrder -= 20;

            var ghost = new Ghost { Owner = owner, Root = root, Guy = guy, Bodies = MapBodies(root.transform), Solid = solid, Colliders = colliders, SpawnFrame = Time.frameCount };
            ghost.Rigidbodies = ghost.Bodies.Select(b => b != null ? b.GetComponent<Rigidbody2D>() : null).ToArray();
            ghost.Target = new Vector3[ghost.Bodies.Length];
            ghost.TargetVelocity = new Vector2[ghost.Bodies.Length];
            for (int i = 0; i < ghost.Rigidbodies.Length; i++)
            {
                if (ghost.Rigidbodies[i] != null) ghostBodies[ghost.Rigidbodies[i]] = new KeyValuePair<Ghost, int>(ghost, i);
            }
            if (solid)
            {
                MakeTouchable(ghost);
                SetColliders(ghost, true);
            }
            ghost.Head = root.transform.Find("Body") ?? root.transform;
            ghost.Tag = MakeNameTag(root.transform);
            ghost.SpeakingDot = MakeSpeakingDot(ghost.Tag.transform);
            return ghost;
        }

        private static void MakeTouchable(Ghost ghost)
        {
            int layer = PhysicalLayer();
            for (int i = 0; i < ghost.Colliders.Length; i++)
            {
                var c = ghost.Colliders[i];
                if (layer >= 0) c.gameObject.layer = layer;
                try { c.gameObject.tag = "Untagged"; } catch { }
                for (int j = i + 1; j < ghost.Colliders.Length; j++) Physics2D.IgnoreCollision(c, ghost.Colliders[j], true);
            }
        }

        private static int PhysicalLayer()
        {
            if (physicalLayer >= 0) return physicalLayer;
            var guy = GameManager.currentEvent != null ? GameManager.currentEvent.guy : null;
            if (guy == null) return -1;
            var bodyLayers = guy.GetComponentsInChildren<Collider2D>().Where(c => !c.isTrigger).Select(c => c.gameObject.layer).Distinct().ToList();
            int mainLayer = guy.bodyBody != null ? guy.bodyBody.gameObject.layer : bodyLayers.FirstOrDefault();
            int grabMask = guy.frontArm != null ? guy.frontArm.grabMask.value : ~0;
            int best = -1, bestHits = -1;
            for (int l = 0; l < 31; l++)
            {
                if ((grabMask & (1 << l)) == 0 || bodyLayers.Contains(l) || Physics2D.GetIgnoreLayerCollision(l, mainLayer)) continue;
                int hits = bodyLayers.Count(b => !Physics2D.GetIgnoreLayerCollision(l, b));
                if (hits > bestHits) { best = l; bestHits = hits; }
            }
            physicalLayer = best;
            Plugin.Log.LogInfo($"Players collide on layer {best} ({(best >= 0 ? LayerMask.LayerToName(best) : "none")}, hits {bestHits} of the player's layers "
                             + $"{string.Join(",", bodyLayers.Select(b => b.ToString()).ToArray())}; main {mainLayer}; grab mask {grabMask})");
            return physicalLayer;
        }

        private static void SetColliders(Ghost ghost, bool on)
        {
            ghost.CollidersOn = on;
            foreach (var c in ghost.Colliders) if (c != null) c.enabled = on;
            if (on) KeepApartUntilClear(ghost);
            else ReleaseGrabsOf(ghost);
        }

        private static void KeepApartUntilClear(Ghost ghost)
        {
            ghost.Pending.Clear();
            var guy = GameManager.currentEvent != null ? GameManager.currentEvent.guy : null;
            if (guy == null) return;
            foreach (var mine in guy.GetComponentsInChildren<Collider2D>().Where(c => !c.isTrigger))
            {
                foreach (var theirs in ghost.Colliders)
                {
                    if (theirs == null) continue;
                    Physics2D.IgnoreCollision(mine, theirs, true);
                    ghost.Pending.Add(new KeyValuePair<Collider2D, Collider2D>(mine, theirs));
                }
            }
        }

        private static void ReleaseClearPairs(Ghost ghost)
        {
            for (int i = ghost.Pending.Count - 1; i >= 0; i--)
            {
                var pair = ghost.Pending[i];
                if (pair.Key == null || pair.Value == null) { ghost.Pending.RemoveAt(i); continue; }
                if (!pair.Key.enabled || !pair.Value.enabled) continue;
                var d = Physics2D.Distance(pair.Key, pair.Value);
                if (!d.isValid || d.distance < 0.05f) continue;
                Physics2D.IgnoreCollision(pair.Key, pair.Value, false);
                ghost.Pending.RemoveAt(i);
            }
        }

        private static TextMeshPro MakeNameTag(Transform parent)
        {
            var go = new GameObject("Ghost Name");
            go.transform.SetParent(parent, false);
            var tag = go.AddComponent<TextMeshPro>();
            var template = GameMenu.singleton != null ? GameMenu.singleton.playerReadyNameText : null;
            if (template != null) tag.font = template.font;
            tag.fontSize = 3.2f;
            tag.alignment = TextAlignmentOptions.Bottom;
            tag.enableWordWrapping = false;
            tag.fontStyle = FontStyles.UpperCase;
            tag.rectTransform.pivot = new Vector2(0.5f, 0f);
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
            ghost.Scheme = scheme;
            if (ghost.Guy != null)
            {
                if (ghost.Guy.player == null) ghost.Guy.player = new GameManager.Player();
                ghost.Guy.player.colorScheme = scheme;
                ghost.Guy.ApplyColorScheme(scheme);
            }
            Fade(ghost);
            var entry = OnlineGame.Entry(player);
            if (ghost.Tag != null)
            {
                ghost.Tag.text = entry != null ? entry.Name : "";
                ghost.Tag.color = new Color(source.primary.r, source.primary.g, source.primary.b, 0.9f);
            }
            if (ghost.SpeakingDot != null) ghost.SpeakingDot.color = new Color(source.primary.r, source.primary.g, source.primary.b, 1f);
        }

        private static void Fade(Ghost ghost)
        {
            if (OnlineGame.Sharing == SharedObjects.Mode.Everything || ghost.Root == null) return;
            foreach (var sr in ghost.Root.GetComponentsInChildren<SpriteRenderer>(true))
            {
                var c = sr.color;
                if (c.a > GhostAlpha) sr.color = new Color(c.r, c.g, c.b, GhostAlpha);
            }
        }

        public static void KeepGhostColors(Guy guy, ref GameManager.PlayerColorScheme scheme)
        {
            if (guy == null || !Enabled) return;
            foreach (var g in ghosts.Values)
            {
                if (g.Guy != guy || g.Scheme == null) continue;
                scheme = g.Scheme;
                if (guy.player != null) guy.player.colorScheme = g.Scheme;
                return;
            }
        }

        public static void AfterRecolor(Guy guy)
        {
            if (guy == null || !Enabled) return;
            foreach (var g in ghosts.Values) if (g.Guy == guy) Fade(g);
        }

        private static Color Ghostly(Color c)
        {
            var pastel = Color.Lerp(c, Color.white, GhostPastel);
            pastel.a = GhostAlpha;
            return pastel;
        }

        private static void ApplySponsor(Ghost g)
        {
            g.SponsorApplied = true;
            if (g.Guy == null) return;
            g.Guy.mutator = null;
            var modifier = EventRandomiser.currentMutator != null && !EventRandomiser.currentMutator.isNoMod ? EventRandomiser.currentMutator.modifier : null;
            if (modifier == null) return;
            var apply = modifier.GetType().GetMethod("Apply", new[] { typeof(GlurpAPI.APIGuy) });
            if (apply == null) return;
            var fields = new List<KeyValuePair<System.Reflection.FieldInfo, object>>();
            for (var type = modifier.GetType(); type != null && type != typeof(object); type = type.BaseType)
            {
                foreach (var f in type.GetFields(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public
                                                 | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.DeclaredOnly))
                {
                    fields.Add(new KeyValuePair<System.Reflection.FieldInfo, object>(f, f.GetValue(modifier)));
                }
            }
            try { apply.Invoke(modifier, new object[] { g.Guy }); }
            catch (Exception e) { Plugin.Log.LogWarning($"Couldn't show the sponsor on another player: {e.InnerException?.Message ?? e.Message}"); }
            finally
            {
                foreach (var f in fields) f.Key.SetValue(modifier, f.Value);
            }
            BuildArmAttachments(g.Guy.frontArm?.noodle);
            BuildArmAttachments(g.Guy.backArm?.noodle);
            Fade(g);
            foreach (var c in g.Root.GetComponentsInChildren<Collider2D>(true)) if (!g.Colliders.Contains(c)) c.gameObject.tag = "Untagged";
        }

        private static readonly System.Reflection.FieldInfo AttachmentPointsField =
            typeof(GlurpAPI.APINoodle).GetField("attachmentPoints", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);

        private static void BuildArmAttachments(GlurpAPI.APINoodle noodle)
        {
            if (noodle == null || noodle.attachmentPrefabs == null || AttachmentPointsField == null) return;
            var old = AttachmentPointsField.GetValue(noodle) as Transform[];
            if ((old?.Length ?? 0) == noodle.attachmentPrefabs.Length) return;
            if (old != null) foreach (var p in old) if (p != null) UnityEngine.Object.Destroy(p.gameObject);
            var color = noodle is Noodle drawn ? drawn.color : Color.white;
            var points = new Transform[noodle.attachmentPrefabs.Length];
            for (int i = 0; i < points.Length; i++)
            {
                var point = new GameObject("Attachment Point").transform;
                point.parent = noodle.transform;
                point.localPosition = Vector3.zero;
                points[i] = point;
                var prefab = noodle.attachmentPrefabs[i];
                if (prefab == null) continue;
                var attachment = UnityEngine.Object.Instantiate(prefab, Vector3.zero, Quaternion.identity, point);
                attachment.transform.localPosition = prefab.transform.position;
                attachment.layer = noodle.gameObject.layer;
                var sprite = attachment.GetComponent<SpriteRenderer>();
                if (sprite == null) continue;
                if (noodle.meshRenderer != null)
                {
                    sprite.sortingLayerName = noodle.meshRenderer.sortingLayerName;
                    sprite.sortingOrder = noodle.meshRenderer.sortingOrder + noodle.attachmentSortingOrderModifier;
                }
                sprite.color = noodle.colorAttachments ? color : Color.white;
            }
            AttachmentPointsField.SetValue(noodle, points);
            if (noodle.attachmentPointPositions != null && noodle.attachmentPointPositions.Length == 0) noodle.attachmentPointPositions = null;
        }

        private static void ApplyHat(Ghost g)
        {
            g.HatApplied = true;
            if (g.Guy == null || g.Guy.bodyBody == null || g.Guy.headBody == null) return;
            var prefab = HatPrefab(OnlineGame.Entry(g.Player)?.HatId);
            if (prefab == null) return;
            string localHat = HatManager.currentHatID;
            try
            {
                var go = UnityEngine.Object.Instantiate(prefab, g.Guy.bodyBody.transform.position, Quaternion.identity);
                go.name = prefab.name;
                var hat = go.GetComponent<GlurpAPI.Hat.GlurpHat>();
                if (hat == null)
                {
                    UnityEngine.Object.Destroy(go);
                    return;
                }
                foreach (var rend in go.GetComponentsInChildren<Renderer>(true)) rend.sortingOrder -= 20;
                HatManager.ApplyHat(hat, g.Guy);
                g.Hat = hat;
                if (g.Solid)
                {
                    foreach (var rb in hat.bodies) if (rb != null) rb.transform.SetParent(null);
                    foreach (var t in hat.removeFromParentObjects) if (t != null) t.SetParent(null);
                }
                else
                {
                    foreach (var rb in hat.GetComponentsInChildren<Rigidbody2D>(true)) rb.simulated = false;
                    foreach (var c in hat.GetComponentsInChildren<Collider2D>(true)) c.enabled = false;
                }
                Fade(g);
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning($"Couldn't show another player's hat: {e.Message}");
            }
            finally
            {
                HatManager.currentHatID = localHat;
            }
        }

        private static void RemoveHat(Ghost g)
        {
            if (g.Hat != null) UnityEngine.Object.Destroy(g.Hat.gameObject);
            if (g.Guy != null) g.Guy.hat = null;
            g.Hat = null;
            g.HatApplied = false;
        }

        private static GameObject HatPrefab(string hatId)
        {
            if (string.IsNullOrEmpty(hatId)) return null;
            int dot = hatId.IndexOf('.');
            if (dot <= 0) return null;
            string pack = hatId.Substring(0, dot), name = hatId.Substring(dot + 1);
            GlurpAPI.Hat.HatList list = null;
            if (HatManager.loadedHatPack != null && HatManager.loadedHatPack.id == pack) list = HatManager.loadedHatPack.hatList;
            if (list == null && !hatLists.TryGetValue(pack, out list))
            {
                if (HatManager.GetHatPack(pack) is InternalHatPack)
                {
                    try
                    {
                        list = SyncAddressables.LoadAsset<GlurpAPI.Hat.HatList>("Assets/Addressable/Hats/" + pack + "/HatPackAssetList.asset");
                        if (list != null) loadedAssets.Add(list);
                    }
                    catch (Exception e)
                    {
                        Plugin.Log.LogWarning($"Couldn't load the {pack} hats: {e.Message}");
                    }
                }
                hatLists[pack] = list;
            }
            return list != null && list.prefabs != null ? list.prefabs.FirstOrDefault(p => p != null && p.name == name) : null;
        }

        private static void Animate(Ghost g, float now)
        {
            if (g.Root == null || g.Snapshots.Count == 0) return;
            var last = g.Snapshots[g.Snapshots.Count - 1];
            if (now - last.Time > HideAfter)
            {
                if (g.Root.activeSelf)
                {
                    ReleaseGrabsOf(g);
                    RemoveHat(g);
                    g.Root.SetActive(false);
                    if (g.OwnHolder != null) g.OwnHolder.SetActive(false);
                }
                return;
            }
            if (!g.SponsorApplied && Time.frameCount > g.SpawnFrame + 1) ApplySponsor(g);
            if (!g.HatApplied && g.SponsorApplied) ApplyHat(g);

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
            float span = b.Time - a.Time;
            float f = span > 0f ? Mathf.Clamp01((t - a.Time) / span) : 1f;
            bool steer = g.Solid && g.CollidersOn;
            for (int i = 0; i < g.Bodies.Length; i++)
            {
                var body = g.Bodies[i];
                if (body == null) continue;
                var pa = a.Poses[i];
                var pb = b.Poses[i];
                var pose = new Vector3(Mathf.Lerp(pa.x, pb.x, f), Mathf.Lerp(pa.y, pb.y, f), Mathf.LerpAngle(pa.z, pb.z, f));
                if (steer)
                {
                    g.Target[i] = pose;
                    g.TargetVelocity[i] = span > 0f ? new Vector2(pb.x - pa.x, pb.y - pa.y) / span : Vector2.zero;
                    continue;
                }
                var rb = g.Rigidbodies[i];
                if (rb != null && rb.simulated)
                {
                    rb.position = pose;
                    rb.rotation = pose.z;
                    rb.velocity = Vector2.zero;
                    rb.angularVelocity = 0f;
                }
                body.position = new Vector3(pose.x, pose.y, body.position.z);
                body.rotation = Quaternion.Euler(0f, 0f, pose.z);
            }
            g.HasTarget = steer;
            AnimateOwnObjects(g, a, b, f);
            PlaceTag(g);
        }

        private static void PlaceTag(Ghost g)
        {
            if (g.Tag == null || g.Head == null) return;
            g.Tag.transform.position = g.Head.position + new Vector3(0f, TagHeight, -0.1f);
            g.Tag.transform.rotation = Quaternion.identity;
            var reference = g.Guy != null ? (Renderer)g.Guy.bodyPrimaryRenderer : null;
            if (reference != null && g.Tag.sortingLayerID != reference.sortingLayerID)
            {
                g.Tag.sortingLayerID = reference.sortingLayerID;
                g.Tag.sortingOrder = 1000;
            }
            PlaceSpeakingDot(g);
        }

        private static SpriteRenderer MakeSpeakingDot(Transform tag)
        {
            var go = new GameObject("Speaking");
            go.transform.SetParent(tag, false);
            var dot = go.AddComponent<SpriteRenderer>();
            dot.sprite = UI.Glyphs.Dot;
            dot.enabled = false;
            return dot;
        }

        private static void PlaceSpeakingDot(Ghost g)
        {
            var dot = g.SpeakingDot;
            if (dot == null) return;
            bool speaking = VoiceChat.IsSpeaking(g.Owner) && !string.IsNullOrEmpty(g.Tag.text);
            if (dot.enabled != speaking) dot.enabled = speaking;
            if (!speaking) return;
            var bounds = g.Tag.textBounds;
            float size = bounds.size.y * 0.7f;
            dot.transform.localScale = new Vector3(size, size, 1f);
            dot.transform.localPosition = new Vector3(bounds.min.x - size * 0.95f, bounds.center.y, 0f);
            dot.sortingLayerID = g.Tag.sortingLayerID;
            dot.sortingOrder = g.Tag.sortingOrder + 1;
        }

        public static bool TryGetPosition(ulong owner, out Vector2 position)
        {
            position = default;
            if (!Enabled || !ghosts.TryGetValue(owner, out var g) || g.Root == null || !g.Root.activeSelf || g.Head == null) return false;
            position = g.Head.position;
            return true;
        }

        public static void FixedUpdate()
        {
            if (!Enabled) return;
            foreach (var g in ghosts.Values)
            {
                if (!g.HasTarget || !g.CollidersOn || g.Root == null || !g.Root.activeSelf) continue;
                if (g.Pending.Count > 0) ReleaseClearPairs(g);
                var head = g.Guy != null ? g.Guy.headBody : null;
                var headWas = head != null ? head.position : Vector2.zero;
                for (int i = 0; i < g.Rigidbodies.Length; i++)
                {
                    var rb = g.Rigidbodies[i];
                    if (rb == null || !rb.simulated) continue;
                    var target = new Vector2(g.Target[i].x, g.Target[i].y);
                    var gap = target - rb.position;
                    if (gap.sqrMagnitude > SnapDistance * SnapDistance)
                    {
                        rb.position = target;
                        rb.rotation = g.Target[i].z;
                        rb.velocity = g.TargetVelocity[i];
                        continue;
                    }
                    rb.velocity = Vector2.ClampMagnitude(g.TargetVelocity[i] + gap * FollowGain, MaxFollowSpeed);
                    rb.angularVelocity = Mathf.DeltaAngle(rb.rotation, g.Target[i].z) * FollowGain;
                }
                if (head != null && g.Hat != null && (head.position - headWas).sqrMagnitude > SnapDistance * SnapDistance)
                {
                    var shift = head.position - headWas;
                    foreach (var rb in g.Hat.bodies)
                    {
                        if (rb == null) continue;
                        rb.position += shift;
                        rb.velocity = head.velocity;
                    }
                }
            }
            if (OnlineGame.Collide) UpdateHolds();
        }

        private static void UpdateHolds()
        {
            var guy = GameManager.currentEvent != null ? GameManager.currentEvent.guy : null;
            var s = Session.Current;
            if (guy == null || s == null) return;
            for (int arm = 0; arm < 2; arm++)
            {
                var a = arm == 0 ? guy.frontArm : guy.backArm;
                ulong target = 0;
                int body = -1;
                Vector2 anchor = Vector2.zero;
                if (a != null && a.grabbedBody != null && ghostBodies.TryGetValue(a.grabbedBody, out var hit) && hit.Key.CollidersOn)
                {
                    target = hit.Key.Owner;
                    body = hit.Value;
                    var hand = HandBody(a);
                    anchor = a.grabbedBody.transform.InverseTransformPoint(hand != null ? hand.position : a.grabbedBody.position);
                }
                var hold = holds[arm];
                if (hold.Target == target && hold.Body == body) continue;
                hold.Target = target;
                hold.Body = body;
                var w = new PacketWriter(Msg.GhostGrab, s.LocalId);
                w.Write((short)OnlineGame.TurnRound);
                w.Write((byte)arm);
                w.Write(target);
                w.Write((byte)Math.Max(body, 0));
                w.Write(anchor.x);
                w.Write(anchor.y);
                s.Broadcast(w);
            }
        }

        public static void OnGhostGrab(ulong holder, PacketReader r)
        {
            var s = Session.Current;
            if (!Enabled || s == null || r.ReadShort() != OnlineGame.TurnRound) return;
            int arm = r.ReadByte();
            ulong target = r.ReadULong();
            int body = r.ReadByte();
            var anchor = new Vector2(r.ReadFloat(), r.ReadFloat());
            long key = (long)(holder & 0x7fffffffffffffffUL) * 2 + arm;
            if (heldBy.TryGetValue(key, out var old))
            {
                if (old != null) UnityEngine.Object.Destroy(old);
                heldBy.Remove(key);
            }
            if (target != s.LocalId) return;
            if (!ghosts.TryGetValue(holder, out var ghost) || ghost.Guy == null) { Plugin.Log.LogInfo($"Grabbed by {holder}, but they aren't shown here"); return; }
            var guy = GameManager.currentEvent != null ? GameManager.currentEvent.guy : null;
            if (guy == null || !EnsurePrefab()) return;
            var mine = MapBodies(guy.transform);
            var ours = body < mine.Length && mine[body] != null ? mine[body].GetComponent<Rigidbody2D>() : null;
            var hand = HandBody(arm == 0 ? ghost.Guy.frontArm : ghost.Guy.backArm);
            if (ours == null || hand == null)
            {
                Plugin.Log.LogInfo($"Grabbed by {holder}, but couldn't join them (our body {ours != null}, their hand {hand != null})");
                return;
            }
            var joint = ours.gameObject.AddComponent<FixedJoint2D>();
            joint.connectedBody = hand;
            joint.autoConfigureConnectedAnchor = false;
            joint.anchor = anchor;
            joint.connectedAnchor = Vector2.zero;
            joint.enableCollision = false;
            heldBy[key] = joint;
        }

        private static Rigidbody2D HandBody(GlurpAPI.APIArm arm)
        {
            if (arm == null) return null;
            if (arm.handBody != null) return arm.handBody;
            if (arm.hand != null && arm.hand.body != null) return arm.hand.body;
            return arm.armHandObject != null ? arm.armHandObject.GetComponent<Rigidbody2D>() : null;
        }

        private static void ReleaseGrabsOf(Ghost g)
        {
            foreach (var pair in heldBy.ToList())
            {
                var joint = pair.Value;
                if (joint == null || joint.connectedBody == null || joint.connectedBody.transform.IsChildOf(g.Root.transform))
                {
                    if (joint != null) UnityEngine.Object.Destroy(joint);
                    heldBy.Remove(pair.Key);
                }
            }
        }
    }
}
