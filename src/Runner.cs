using System;
using GlurpOnline.Game;
using GlurpOnline.Net;
using GlurpOnline.UI;
using GlurpOnline.Update;
using UnityEngine;

namespace GlurpOnline
{
    internal sealed class Runner : MonoBehaviour
    {
        private void Update()
        {
            Step("session", () => Session.Current?.Update());
            Step("lan", LanDiscovery.Update);
            Step("game", OnlineGame.Update);
            Step("voice", VoiceChat.Update);
            Step("ingame ui", InGameUI.Update);
            Step("menu ui", OnlineUI.Update);
            Step("updater", Updater.Update);
        }

        private void FixedUpdate()
        {
            Step("ghost physics", GhostSync.FixedUpdate);
        }

        private static void Step(string name, Action action)
        {
            try { action(); }
            catch (Exception e) { Plugin.Log.LogError($"[{name}] {e}"); }
        }

        private void OnApplicationQuit()
        {
            Session.Current?.Leave();
        }
    }
}
