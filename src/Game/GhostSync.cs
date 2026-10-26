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
            public readonly List<KeyValuePair<Collider2D, Collider2D>> Pending = new List<KeyValuePair<Collider2D, Collider2D>>();
            public TextMeshPro Tag;
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
            foreach (var h in holds) { h.Target = 0; h.Body = -1; }
            nextGhostId = FirstGhostId;
            sendingGuy = null;
            sendingBodies = null;
            physicalLayer = -1;
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
                ghost = Spawn(origin, poses[0]);
                if (ghost == null) return;
                ghosts[origin] = ghost;
            }
            if (ghost.Player != player || ghost.ColorIndex != colorIndex) Dress(ghost, player, colorIndex);
            var snaps = ghost.Snapshots;
            bool jumped = snaps.Count > 0 && (snaps[snaps.Count - 1].Poses[0] - poses[0]).sqrMagnitude > 9f;
            snaps.Add(new Snapshot { Time = Time.realtimeSinceStartup, Poses = poses });
            if (snaps.Count > 12) snaps.RemoveAt(0);
            if (!ghost.Root.activeSelf)
            {
                ghost.Root.SetActive(true);
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
            foreach (var c in g.Root.GetComponentsInChildren<Collider2D>(true)) if (!g.Colliders.Contains(c)) c.gameObject.tag = "Untagged";
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
                    g.Root.SetActive(false);
                }
                return;
            }
            if (!g.SponsorApplied && Time.frameCount > g.SpawnFrame + 1) ApplySponsor(g);

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
        }

        public static void FixedUpdate()
        {
            if (!Enabled) return;
            foreach (var g in ghosts.Values)
            {
                if (!g.HasTarget || !g.CollidersOn || g.Root == null || !g.Root.activeSelf) continue;
                if (g.Pending.Count > 0) ReleaseClearPairs(g);
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
