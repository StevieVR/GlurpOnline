using System;
using System.Collections.Generic;
using System.Linq;
using GlurpAPI;
using GlurpAPI.Brand;
using GlurpOnline.Net;
using UnityEngine;

namespace GlurpOnline.Game
{
    internal static class SharedObjects
    {
        public enum Mode { None = 0, Props = 1, Everything = 2 }

        private const float SendInterval = 1f / 20f;
        private const float RenderDelay = 0.1f;
        private const float HoldTime = 0.5f;
        private const float ClaimRetry = 0.25f;
        private const float ScanFor = 2f;
        private const float FollowGain = 12f;
        private const float MaxFollowSpeed = 40f;
        private const float SnapDistance = 3f;
        private const int MaxPerPacket = 40;

        private static readonly HashSet<ReplayEvent.Type> SharedEventTypes = new HashSet<ReplayEvent.Type>
        {
            ReplayEvent.Type.Deactivate, ReplayEvent.Type.Enable, ReplayEvent.Type.Disable,
            ReplayEvent.Type.Start, ReplayEvent.Type.Stop, ReplayEvent.Type.ParticleStart, ReplayEvent.Type.ParticleStop,
            ReplayEvent.Type.Destroy,
        };

        private static readonly Type[] ScoringTypes =
        {
            typeof(GlurpEvents.Targets.Target), typeof(CollectObject), typeof(DestructableObject), typeof(BowlsTarget),
            typeof(TargetObject), typeof(GlurpEvents.WheelSpin.RotationScoreObject), typeof(FinishBoxAllowedObject),
        };

        internal struct Pose
        {
            public float Time;
            public Vector2 Position;
            public float Rotation;
            public Vector2 Velocity;
            public float Spin;
        }

        internal sealed class Item
        {
            public int Key;
            public Rigidbody2D Body;
            public ulong Owner;
            public float OwnerSince;
            public ulong HostOwner;
            public float HostOwnedSince;
            public float LastClaim;
            public Vector2 SentPosition;
            public float SentRotation;
            public bool SentAsleep;
            public readonly List<Pose> Poses = new List<Pose>();
        }

        private static readonly Dictionary<int, Item> items = new Dictionary<int, Item>();
        private static readonly Dictionary<Rigidbody2D, Item> byBody = new Dictionary<Rigidbody2D, Item>();
        private static readonly Dictionary<int, ReplayTracker> trackers = new Dictionary<int, ReplayTracker>();
        private static readonly Dictionary<ReplayTracker, int> trackerKeys = new Dictionary<ReplayTracker, int>();
        private static readonly Dictionary<short, int> idCounts = new Dictionary<short, int>();
        private static readonly HashSet<Transform> scoring = new HashSet<Transform>();
        private static readonly HashSet<Rigidbody2D> jointed = new HashSet<Rigidbody2D>();
        private static float startedAt = -1f;
        private static float nextScan;
        private static float nextSend;
        private static bool applyingRemote;

        private static ulong LocalId => Session.Current?.LocalId ?? 0;

        public static bool Active =>
            OnlineGame.Sharing != Mode.None && OnlineGame.AutoStart && OnlineGame.TurnBegun && GameManager.currentEvent != null;

        public static void OnSceneLoading()
        {
            items.Clear();
            byBody.Clear();
            trackers.Clear();
            trackerKeys.Clear();
            idCounts.Clear();
            scoring.Clear();
            startedAt = -1f;
        }

        public static void Update()
        {
            if (!Active) return;
            float now = Time.realtimeSinceStartup;
            if (startedAt < 0f) startedAt = now;
            if (now - startedAt < ScanFor && now >= nextScan)
            {
                nextScan = now + 0.5f;
                Scan();
            }
            if (now >= nextSend)
            {
                nextSend = now + SendInterval;
                SendStates();
            }
        }

        public static void OnPeerDropped(ulong peer)
        {
            var s = Session.Current;
            if (s == null || !s.IsHost) return;
            foreach (var item in items.Values.Where(i => i.HostOwner == peer).ToList()) HostAssign(item, s.LocalId, s);
        }

        private static void Scan()
        {
            if (scoring.Count == 0) CollectScoringObjects();
            jointed.Clear();
            foreach (var joint in UnityEngine.Object.FindObjectsOfType<Joint2D>())
            {
                var a = joint.attachedRigidbody;
                var b = joint.connectedBody;
                if (a == null || b == null || a.bodyType == RigidbodyType2D.Static || b.bodyType == RigidbodyType2D.Static) continue;
                jointed.Add(a);
                jointed.Add(b);
            }

            var fresh = UnityEngine.Object.FindObjectsOfType<ReplayTracker>()
                .Where(t => t != null && t.config != null && t.ReplayID() != 0 && !trackerKeys.ContainsKey(t) && !IsPlayerPart(t.transform))
                .Select(t => new { Tracker = t, Id = t.ReplayID(), Path = PathOf(t.transform), Position = t.transform.position })
                .OrderBy(t => t.Id).ThenBy(t => t.Path, StringComparer.Ordinal)
                .ThenBy(t => Mathf.Round(t.Position.x * 100f)).ThenBy(t => Mathf.Round(t.Position.y * 100f));
            foreach (var f in fresh)
            {
                idCounts.TryGetValue(f.Id, out int ordinal);
                idCounts[f.Id] = ordinal + 1;
                int key = (f.Id << 8) | (ordinal & 0xff);
                trackerKeys[f.Tracker] = key;
                trackers[key] = f.Tracker;

                var body = f.Tracker.trackerBody;
                if (body == null || byBody.ContainsKey(body) || !IsShareableBody(body)) continue;
                var item = new Item { Key = key, Body = body };
                items[key] = item;
                byBody[body] = item;
                var hook = body.gameObject.GetComponent<SharedBody>() ?? body.gameObject.AddComponent<SharedBody>();
                hook.Item = item;
            }
        }

        private static string PathOf(Transform t)
        {
            var parts = new List<string>();
            for (var p = t; p != null; p = p.parent) parts.Add(p.name);
            parts.Reverse();
            return string.Join("/", parts.ToArray());
        }

        private static bool IsPlayerPart(Transform t) =>
            t.GetComponentInParent<Guy>() != null || t.GetComponentInParent<MutatorManager>() != null;

        private static bool IsShareableBody(Rigidbody2D body) => WhyNotShared(body) == null;

        private static string WhyNotShared(Rigidbody2D body)
        {
            if (body.bodyType != RigidbodyType2D.Dynamic) return "not dynamic (" + body.bodyType + ")";
            if (!body.simulated) return "not simulated";
            if (jointed.Contains(body)) return "jointed";
            if (body.GetComponentInParent<GlurpPushButton>() != null) return "push button";
            if (body.GetComponentInParent<StartBoxSquidgePusher>() != null || body.GetComponentInParent<StartBoxSquidgeExpander>() != null
                || body.GetComponentInParent<FinishBoxSquidgePusher>() != null) return "start/finish box";
            for (var t = body.transform; t != null; t = t.parent)
            {
                if (t.name.StartsWith("Start Box") || t.name.StartsWith("Finish Box")) return "start/finish box";
            }
            foreach (var joint in body.GetComponents<Joint2D>())
            {
                if (joint.connectedBody != null && IsPlayerPart(joint.connectedBody.transform)) return "attached to a player";
            }
            if (IsMeasured(body.transform)) return "measured by the score";
            bool scoringType = IsScoringType(body.transform);
            if (OnlineGame.Sharing != Mode.Everything && scoringType) return "scoring object";
            if (!scoringType && ScriptOn(body.gameObject) is string script) return "driven by " + script;
            return null;
        }

        private static string ScriptOn(GameObject go)
        {
            foreach (var behaviour in go.GetComponents<MonoBehaviour>())
            {
                if (behaviour == null) continue;
                var type = behaviour.GetType();
                if (behaviour is GlurpDynamicObject || behaviour is ReplayTracker || behaviour is ReplayTrackerOffscreenUpdater || behaviour is SharedBody) continue;
                if (type.Namespace != null && (type.Namespace.StartsWith("UnityEngine") || type.Namespace.StartsWith("TMPro"))) continue;
                return type.Name;
            }
            return null;
        }

        private static void CollectScoringObjects()
        {
            foreach (var manager in UnityEngine.Object.FindObjectsOfType<GlurpEventScoreManager>())
            {
                for (var type = manager.GetType(); type != null && type != typeof(MonoBehaviour); type = type.BaseType)
                {
                    foreach (var field in type.GetFields(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public
                                                         | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.DeclaredOnly))
                    {
                        object value;
                        try { value = field.GetValue(manager); } catch { continue; }
                        if (value is Component c && c != null) scoring.Add(c.transform);
                        else if (value is GameObject go && go != null) scoring.Add(go.transform);
                    }
                }
            }
        }

        private static bool IsScoringType(Transform t)
        {
            foreach (var type in ScoringTypes)
            {
                if (t.GetComponentInParent(type) != null || t.GetComponentInChildren(type, true) != null) return true;
            }
            return false;
        }

        private static bool IsMeasured(Transform t)
        {
            for (var p = t; p != null; p = p.parent)
            {
                if (scoring.Contains(p)) return true;
            }
            return false;
        }

        public static void OnTouch(Item item, Collision2D collision)
        {
            if (!Active || item.Owner == LocalId) return;
            var other = collision.rigidbody;
            if (other == null) return;
            var guy = GameManager.currentEvent.guy;
            bool ours = guy != null && other.transform.IsChildOf(guy.transform)
                        || byBody.TryGetValue(other, out var pusher) && pusher.Owner == LocalId;
            if (!ours) return;
            float now = Time.realtimeSinceStartup;
            if (now - item.LastClaim < ClaimRetry) return;
            item.LastClaim = now;

            var s = Session.Current;
            if (s == null) return;
            if (item.Owner == 0 || now - item.OwnerSince >= HoldTime) SetOwner(item, s.LocalId);
            if (s.IsHost)
            {
                HostConsiderClaim(item, s.LocalId, s);
            }
            else
            {
                var w = new PacketWriter(Msg.ObjClaim, s.LocalId);
                w.Write((short)OnlineGame.TurnRound);
                w.Write(item.Key);
                s.Send(s.HostId, w);
            }
        }

        private static void HostConsiderClaim(Item item, ulong claimant, Session s)
        {
            float now = Time.realtimeSinceStartup;
            bool held = item.HostOwner != 0 && item.HostOwner != claimant && now - item.HostOwnedSince < HoldTime
                        && !OnlineGame.DroppedPeers.Contains(item.HostOwner);
            if (!held)
            {
                if (item.HostOwner != claimant) HostAssign(item, claimant, s);
                else item.HostOwnedSince = now;
                return;
            }
            if (claimant == s.LocalId)
            {
                SetOwner(item, item.HostOwner);
            }
            else
            {
                s.Send(claimant, OwnerMessage(item, item.HostOwner, s));
            }
        }

        private static void HostAssign(Item item, ulong owner, Session s)
        {
            item.HostOwner = owner;
            item.HostOwnedSince = Time.realtimeSinceStartup;
            s.Broadcast(OwnerMessage(item, owner, s));
            SetOwner(item, owner);
        }

        private static PacketWriter OwnerMessage(Item item, ulong owner, Session s)
        {
            var w = new PacketWriter(Msg.ObjOwner, s.LocalId);
            w.Write((short)OnlineGame.TurnRound);
            w.Write(item.Key);
            w.Write(owner);
            return w;
        }

        private static void SetOwner(Item item, ulong owner)
        {
            if (item.Owner == owner || item.Body == null) return;
            item.Owner = owner;
            item.OwnerSince = Time.realtimeSinceStartup;
            var body = item.Body;
            if (owner == LocalId)
            {
                if (item.Poses.Count > 0)
                {
                    var p = item.Poses[item.Poses.Count - 1];
                    body.position = p.Position;
                    body.rotation = p.Rotation;
                    body.velocity = p.Velocity;
                    body.angularVelocity = p.Spin;
                }
                item.Poses.Clear();
                item.SentAsleep = false;
            }
        }

        public static bool ShouldCountHit(Collision2D collision)
        {
            if (!Active || collision.rigidbody == null || !byBody.TryGetValue(collision.rigidbody, out var item)) return true;
            return item.Owner == 0 || item.Owner == LocalId;
        }

        public static bool ShouldCollect(Component collectable)
        {
            if (!Active) return true;
            var body = collectable.GetComponentInParent<Rigidbody2D>();
            return body == null || !byBody.TryGetValue(body, out var item) || item.Owner == 0 || item.Owner == LocalId;
        }

        private static void SendStates()
        {
            var s = Session.Current;
            if (s == null) return;
            var batch = new List<Item>();
            foreach (var item in items.Values)
            {
                if (item.Owner != s.LocalId || item.Body == null) continue;
                bool asleep = item.Body.IsSleeping();
                bool moved = (item.Body.position - item.SentPosition).sqrMagnitude > 1e-6f || Mathf.Abs(item.Body.rotation - item.SentRotation) > 0.01f;
                if (!moved && asleep && item.SentAsleep) continue;
                batch.Add(item);
            }
            for (int start = 0; start < batch.Count; start += MaxPerPacket)
            {
                int count = Math.Min(MaxPerPacket, batch.Count - start);
                var w = new PacketWriter(Msg.ObjState, s.LocalId);
                w.Write((short)OnlineGame.TurnRound);
                w.Write((byte)count);
                for (int i = start; i < start + count; i++)
                {
                    var item = batch[i];
                    var b = item.Body;
                    w.Write(item.Key);
                    w.Write(b.position.x);
                    w.Write(b.position.y);
                    w.Write(b.rotation);
                    w.Write(b.velocity.x);
                    w.Write(b.velocity.y);
                    w.Write(b.angularVelocity);
                    item.SentPosition = b.position;
                    item.SentRotation = b.rotation;
                    item.SentAsleep = b.IsSleeping();
                }
                s.Broadcast(w);
            }
        }

        public static void Follow(Item item)
        {
            if (!Active || item.Owner == 0 || item.Owner == LocalId || item.Poses.Count == 0 || item.Body == null) return;
            float t = Time.realtimeSinceStartup - RenderDelay;
            var poses = item.Poses;
            Pose a = poses[0], b = poses[poses.Count - 1];
            for (int i = poses.Count - 1; i > 0; i--)
            {
                if (poses[i - 1].Time <= t)
                {
                    a = poses[i - 1];
                    b = poses[i];
                    break;
                }
            }
            float f = b.Time > a.Time ? Mathf.Clamp01((t - a.Time) / (b.Time - a.Time)) : 1f;
            var body = item.Body;
            var position = Vector2.Lerp(a.Position, b.Position, f);
            float rotation = Mathf.LerpAngle(a.Rotation, b.Rotation, f);
            var gap = position - body.position;
            if (gap.sqrMagnitude > SnapDistance * SnapDistance)
            {
                body.position = position;
                body.rotation = rotation;
                body.velocity = b.Velocity;
                body.angularVelocity = b.Spin;
                return;
            }
            body.velocity = Vector2.ClampMagnitude(Vector2.Lerp(a.Velocity, b.Velocity, f) + gap * FollowGain, MaxFollowSpeed);
            body.angularVelocity = Mathf.Lerp(a.Spin, b.Spin, f) + Mathf.DeltaAngle(body.rotation, rotation) * FollowGain;
        }

        public static void OnRecordedEvent(short id, byte type, ReplayEvent.ReplayDataType dataType, Vector3 vector, short shortData, float floatData)
        {
            if (applyingRemote || OnlineGame.Sharing != Mode.Everything || !Active) return;
            if (!SharedEventTypes.Contains((ReplayEvent.Type)type) || !idCounts.TryGetValue(id, out int count) || count != 1) return;
            int key = id << 8;
            if (!trackers.TryGetValue(key, out var tracker) || tracker == null) return;
            if (tracker.GetComponentInParent<GlurpEvents.Targets.Target>() != null || tracker.GetComponentInChildren<GlurpEvents.Targets.Target>() != null) return;
            bool shared = tracker.trackerBody != null && byBody.ContainsKey(tracker.trackerBody);
            if (!shared && (!IsScoringType(tracker.transform) || IsMeasured(tracker.transform))) return;
            if (tracker.GetComponentInParent<GlurpPushButton>() != null) return;
            SendEvent(key, (ReplayEvent.Type)type, dataType, vector, shortData, floatData);
        }

        public static void OnLocalTargetHit(GlurpEvents.Targets.Target target)
        {
            if (applyingRemote || OnlineGame.Sharing != Mode.Everything || !Active || target == null) return;
            var tracker = target.GetComponentInParent<ReplayTracker>() ?? target.GetComponentInChildren<ReplayTracker>();
            if (tracker == null || !trackerKeys.TryGetValue(tracker, out int key)) return;
            SendEvent(key, ReplayEvent.Type.Destroy, ReplayEvent.ReplayDataType.None, Vector3.zero, 0, 0f);
        }

        private static void SendEvent(int key, ReplayEvent.Type type, ReplayEvent.ReplayDataType dataType, Vector3 vector, short shortData, float floatData)
        {
            var s = Session.Current;
            if (s == null) return;
            var w = new PacketWriter(Msg.ObjEvent, s.LocalId);
            w.Write((short)OnlineGame.TurnRound);
            w.Write(key);
            w.Write((byte)type);
            w.Write((byte)dataType);
            w.Write(vector.x);
            w.Write(vector.y);
            w.Write(vector.z);
            w.Write(shortData);
            w.Write(floatData);
            s.Broadcast(w);
        }

        private static void ApplyRemoteEvent(ReplayTracker tracker, ReplayEvent ev)
        {
            applyingRemote = true;
            try
            {
                tracker.HandleEvent(ev);
                if (ev.eventType == ReplayEvent.Type.Destroy) RemoteTargetHit(tracker);
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning($"Couldn't apply shared event {ev.eventType} on {tracker.name}: {e.Message}");
            }
            finally { applyingRemote = false; }
        }

        private static readonly HarmonyLib.AccessTools.FieldRef<GlurpEvents.Targets.Target, bool> TargetHasBeenHit =
            HarmonyLib.AccessTools.FieldRefAccess<GlurpEvents.Targets.Target, bool>("hasBeenHit");
        private static readonly HarmonyLib.AccessTools.FieldRef<GlurpEvents.Targets.Target, Collider2D> TargetCollider =
            HarmonyLib.AccessTools.FieldRefAccess<GlurpEvents.Targets.Target, Collider2D>("targetCollider");
        private static readonly HarmonyLib.AccessTools.FieldRef<GlurpEvents.Targets.Target, SpriteRenderer> TargetRenderer =
            HarmonyLib.AccessTools.FieldRefAccess<GlurpEvents.Targets.Target, SpriteRenderer>("spriteRenderer");

        private static void RemoteTargetHit(ReplayTracker tracker)
        {
            var target = tracker.GetComponentInParent<GlurpEvents.Targets.Target>() ?? tracker.GetComponentInChildren<GlurpEvents.Targets.Target>();
            if (target == null || TargetHasBeenHit(target)) return;
            TargetHasBeenHit(target) = true;
            if (TargetCollider(target) != null) TargetCollider(target).enabled = false;
            if (target.shouldRespawnAfterHit) Plugin.Instance.StartCoroutine(RespawnTarget(target));
        }

        private static System.Collections.IEnumerator RespawnTarget(GlurpEvents.Targets.Target target)
        {
            yield return new WaitForSeconds(target.respawnDelay);
            if (target == null) yield break;
            TargetHasBeenHit(target) = false;
            if (TargetCollider(target) != null) TargetCollider(target).enabled = true;
            if (TargetRenderer(target) != null) TargetRenderer(target).enabled = true;
        }

        public static void OnMessage(Msg type, ulong origin, PacketReader r)
        {
            var s = Session.Current;
            if (s == null || !Active) return;
            int round = r.ReadShort();
            if (round != OnlineGame.TurnRound) return;
            switch (type)
            {
                case Msg.ObjClaim:
                    {
                        if (!s.IsHost || !items.TryGetValue(r.ReadInt(), out var item)) return;
                        HostConsiderClaim(item, origin, s);
                        break;
                    }
                case Msg.ObjOwner:
                    {
                        if (origin != s.HostId) return;
                        int key = r.ReadInt();
                        ulong owner = r.ReadULong();
                        if (items.TryGetValue(key, out var item)) SetOwner(item, owner);
                        break;
                    }
                case Msg.ObjState:
                    {
                        int count = r.ReadByte();
                        float now = Time.realtimeSinceStartup;
                        for (int i = 0; i < count; i++)
                        {
                            int key = r.ReadInt();
                            var pose = new Pose
                            {
                                Time = now,
                                Position = new Vector2(r.ReadFloat(), r.ReadFloat()),
                                Rotation = r.ReadFloat(),
                                Velocity = new Vector2(r.ReadFloat(), r.ReadFloat()),
                                Spin = r.ReadFloat(),
                            };
                            if (!items.TryGetValue(key, out var item) || item.Owner != origin) continue;
                            item.Poses.Add(pose);
                            if (item.Poses.Count > 10) item.Poses.RemoveAt(0);
                        }
                        break;
                    }
                case Msg.ObjEvent:
                    {
                        if (OnlineGame.Sharing != Mode.Everything) return;
                        int key = r.ReadInt();
                        var ev = new ReplayEvent
                        {
                            replayID = (short)(key >> 8),
                            eventType = (ReplayEvent.Type)r.ReadByte(),
                            dataType = (ReplayEvent.ReplayDataType)r.ReadByte(),
                            vectorData = new Vector3(r.ReadFloat(), r.ReadFloat(), r.ReadFloat()),
                            shortData = r.ReadShort(),
                            floatData = r.ReadFloat(),
                        };
                        if (trackers.TryGetValue(key, out var tracker) && tracker != null) ApplyRemoteEvent(tracker, ev);
                        break;
                    }
            }
        }
    }

    internal sealed class SharedBody : MonoBehaviour
    {
        public SharedObjects.Item Item;

        private void OnCollisionEnter2D(Collision2D collision)
        {
            if (Item != null) SharedObjects.OnTouch(Item, collision);
        }

        private void OnCollisionStay2D(Collision2D collision)
        {
            if (Item != null) SharedObjects.OnTouch(Item, collision);
        }

        private void FixedUpdate()
        {
            if (Item != null) SharedObjects.Follow(Item);
        }
    }
}
