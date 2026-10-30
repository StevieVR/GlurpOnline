using System.Collections;
using System.Collections.Generic;
using GlurpAPI;
using GlurpEvents.Tag;
using GlurpOnline.Net;
using HarmonyLib;
using UnityEngine;

namespace GlurpOnline.Game
{
    internal static class LevelSync
    {
        private const byte KindTagCaught = 1, KindTagLit = 2, KindRespawn = 3;
        private const float TagRelightDelay = 0.5f;

        private static readonly AccessTools.FieldRef<TagObjectManager, CatchObject[]> TagObjects =
            AccessTools.FieldRefAccess<TagObjectManager, CatchObject[]>("objects");
        private static readonly AccessTools.FieldRef<TagObjectManager, int> TagCurrent =
            AccessTools.FieldRefAccess<TagObjectManager, int>("currentObjectIndex");
        private static readonly AccessTools.FieldRef<TagObjectManager, bool> TagWaiting =
            AccessTools.FieldRefAccess<TagObjectManager, bool>("waitingForNewCatchObject");
        private static readonly AccessTools.FieldRef<TagObjectManager, GlurpIntegerScoreManager> TagScore =
            AccessTools.FieldRefAccess<TagObjectManager, GlurpIntegerScoreManager>("scoreManager");
        private static readonly AccessTools.FieldRef<TagObjectManager, APIGuy> TagGuy =
            AccessTools.FieldRefAccess<TagObjectManager, APIGuy>("guy");
        private static readonly System.Reflection.MethodInfo TagPickNext = AccessTools.Method(typeof(TagObjectManager), "PickNextObject");
        private static readonly AccessTools.FieldRef<CollectObjectSpawner, bool> SpawningEnabled =
            AccessTools.FieldRefAccess<CollectObjectSpawner, bool>("isSpawningEnabled");

        private static bool tagPicking;
        private static readonly HashSet<int> respawned = new HashSet<int>();

        private static bool Syncing => SharedObjects.Active && OnlineGame.Sharing == SharedObjects.Mode.Everything;

        public static void OnSceneLoading()
        {
            tagPicking = false;
            respawned.Clear();
        }

        private static void Send(byte kind, int key)
        {
            var s = Session.Current;
            if (s == null) return;
            var w = new PacketWriter(Msg.LevelSync, s.LocalId);
            w.Write((short)OnlineGame.TurnRound);
            w.Write(kind);
            w.Write(key);
            s.Broadcast(w);
        }

        public static void OnMessage(ulong origin, PacketReader r)
        {
            if (!Syncing || r.ReadShort() != OnlineGame.TurnRound) return;
            byte kind = r.ReadByte();
            int key = r.ReadInt();
            switch (kind)
            {
                case KindTagCaught:
                    OnTagCaught(origin, key);
                    break;
                case KindTagLit:
                    if (Session.Current != null && origin == Session.Current.HostId) LightTag(key);
                    break;
                case KindRespawn:
                    Respawn(key);
                    break;
            }
        }

        public static bool OnTagTouched(TagObjectManager manager, Collision2D collision)
        {
            if (!Syncing) return true;
            var objects = TagObjects(manager);
            if (objects == null || objects.Length == 0) return false;
            var touched = collision.otherCollider.GetComponent<CatchObject>();
            if (touched == null && collision.otherCollider.transform.parent != null) touched = collision.otherCollider.transform.parent.GetComponent<CatchObject>();
            string tag = collision.collider.tag;
            if (touched == null || touched != objects[TagCurrent(manager)] || TagWaiting(manager) || (tag != "Guy" && tag != "Hand" && tag != "Bullet")) return false;

            Glurp.soundEffectManager.PlaySoundClip(SoundClip.Tom);
            TagWaiting(manager) = true;
            TagScore(manager)?.AddScore(1);
            touched.TurnOn(false);
            Send(KindTagCaught, SharedObjects.KeyOf(touched));
            var s = Session.Current;
            if (s != null && s.IsHost) HostPickTag(manager, s.LocalId);
            return false;
        }

        private static void OnTagCaught(ulong catcher, int key)
        {
            var manager = Object.FindObjectOfType<TagObjectManager>();
            if (manager == null) return;
            var objects = TagObjects(manager);
            var lit = objects != null && objects.Length > 0 ? objects[TagCurrent(manager)] : null;
            if (lit != null && SharedObjects.KeyOf(lit) == key && !TagWaiting(manager))
            {
                TagWaiting(manager) = true;
                lit.TurnOn(false);
            }
            var s = Session.Current;
            if (s != null && s.IsHost) HostPickTag(manager, catcher);
        }

        private static void HostPickTag(TagObjectManager manager, ulong catcher)
        {
            if (tagPicking) return;
            tagPicking = true;
            manager.StartCoroutine(PickTagRoutine(manager, catcher));
        }

        private static IEnumerator PickTagRoutine(TagObjectManager manager, ulong catcher)
        {
            yield return new WaitForSeconds(TagRelightDelay);
            tagPicking = false;
            var objects = TagObjects(manager);
            if (manager == null || objects == null || objects.Length < 2) yield break;
            var s = Session.Current;
            var localGuy = GameManager.currentEvent != null ? GameManager.currentEvent.guy : null;
            var catcherGuy = s != null && catcher == s.LocalId ? localGuy : GhostSync.GuyOf(catcher) ?? localGuy;
            var guy = TagGuy(manager);
            TagGuy(manager) = catcherGuy;
            try { TagPickNext.Invoke(manager, null); }
            finally { TagGuy(manager) = guy; }
            var next = objects[TagCurrent(manager)];
            Send(KindTagLit, SharedObjects.KeyOf(next));
            LightTag(SharedObjects.KeyOf(next));
        }

        private static void LightTag(int key)
        {
            var manager = Object.FindObjectOfType<TagObjectManager>();
            var objects = manager != null ? TagObjects(manager) : null;
            if (objects == null) return;
            for (int i = 0; i < objects.Length; i++)
            {
                if (SharedObjects.KeyOf(objects[i]) != key) continue;
                var lit = objects[TagCurrent(manager)];
                if (i != TagCurrent(manager) && lit != null) lit.TurnOn(false);
                TagCurrent(manager) = i;
                objects[i].TurnOn(true);
                TagWaiting(manager) = false;
                return;
            }
        }

        public static bool OnCollectSpawn(CollectObjectSpawner spawner, GameObject collected)
        {
            if (!Syncing || collected == null) return true;
            int key = SharedObjects.KeyOf(collected.transform);
            if (key == 0) return true;
            if (respawned.Add(key))
            {
                Send(KindRespawn, key);
                spawner.StartCoroutine(RespawnRoutine(spawner, collected, key));
            }
            return false;
        }

        private static void Respawn(int key)
        {
            var collected = SharedObjects.ByKey(key);
            var spawner = Object.FindObjectOfType<CollectObjectSpawner>();
            if (collected == null || spawner == null || !respawned.Add(key)) return;
            spawner.StartCoroutine(RespawnRoutine(spawner, collected, key));
        }

        private static IEnumerator RespawnRoutine(CollectObjectSpawner spawner, GameObject collected, int key)
        {
            yield return new WaitForSeconds(spawner.spawnDelay);
            if (spawner == null || collected == null || !SpawningEnabled(spawner)) yield break;
            var source = collected.GetComponent<CollectObject>();
            if (source == null) yield break;
            var copy = Object.Instantiate(collected, source.initialLocation, Quaternion.identity).GetComponent<CollectObject>();
            copy.hasBeenTouched = false;
            copy.joint.enabled = false;
            copy.joint.connectedBody = null;
            for (int i = 0; i < source.disableOnCollect.Length; i++) copy.disableOnCollect[i].enabled = true;
            var body = copy.GetComponent<Rigidbody2D>();
            body.velocity = Vector2.zero;
            body.angularVelocity = 0f;
            copy.gameObject.tag = "UnGrabable";
            copy.gameObject.layer = 16;
            copy.initialLocation = source.initialLocation;
            SharedObjects.AddSpawned(NextKey(key), body);
        }

        private static int NextKey(int key)
        {
            uint h = (uint)key ^ 0x9e3779b9u;
            h ^= h >> 16;
            h *= 0x85ebca6bu;
            h ^= h >> 13;
            h *= 0xc2b2ae35u;
            h ^= h >> 16;
            return (int)(h | 0x40000000u) & 0x7fffffff;
        }
    }
}
