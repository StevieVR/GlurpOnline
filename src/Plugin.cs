using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;

namespace GlurpOnline
{
    [BepInPlugin(Guid, Name, Version)]
    public class Plugin : BaseUnityPlugin
    {
        public const string Guid = "com.glurponline.mod";
        public const string Name = "Glurp Online";
        public const string Version = "1.3.0";

        public const int ProtocolVersion = 7;

        public const string UpdateRepository = "StevieVR/GlurpOnline";

        internal static Plugin Instance;
        internal static ManualLogSource Log;

        internal static ConfigEntry<int> DirectPort;
        internal static ConfigEntry<string> LastDirectAddress;
        internal static ConfigEntry<int> LastColorIndex;
        internal static ConfigEntry<string> LastHatId;
        internal static ConfigEntry<int> LobbyRounds;
        internal static ConfigEntry<int> LobbyDifficulty;
        internal static ConfigEntry<int> LobbyPrivacy;
        internal static ConfigEntry<int> LobbyMaxPlayers;
        internal static ConfigEntry<int> LobbyMode;
        internal static ConfigEntry<bool> LobbySeeEachOther;
        internal static ConfigEntry<int> LobbySharing;
        internal static ConfigEntry<bool> LobbyCollide;
        internal static ConfigEntry<bool> LobbyVoice;
        internal static ConfigEntry<string> RecentAddresses;
        internal static ConfigEntry<bool> CheckForUpdates;
        internal static ConfigEntry<bool> PeerUpdates;

        private void Awake()
        {
            Instance = this;
            Log = Logger;

            DirectPort = Config.Bind("Direct Connect", "Port", 27420, "TCP port used when hosting or joining via Direct Connect (IP).");
            LastDirectAddress = Config.Bind("Direct Connect", "LastAddress", "127.0.0.1", "Last address typed into Direct Connect.");
            LastColorIndex = Config.Bind("Player", "ColorIndex", -1, "Preferred colour scheme index (-1 = pick for me).");
            LastHatId = Config.Bind("Player", "HatId", "", "Preferred hat id (empty = none).");
            LobbyRounds = Config.Bind("Lobby", "Rounds", 5, "Rounds when hosting.");
            LobbyDifficulty = Config.Bind("Lobby", "Difficulty", 1, "0 = Beginner, 1 = Intermediate, 2 = Advanced.");
            LobbyPrivacy = Config.Bind("Lobby", "Privacy", 0, "0 = Friends only, 1 = Public, 2 = Invite only.");
            LobbyMaxPlayers = Config.Bind("Lobby", "MaxPlayers", 8, "Maximum players when hosting (2-16).");
            LobbyMode = Config.Bind("Lobby", "Mode", 0, "0 = take turns (watch each other live), 1 = everyone plays at once.");
            LobbySeeEachOther = Config.Bind("Lobby", "SeeEachOther", true, "When everyone plays at once, show the other players as ghosts.");
            LobbySharing = Config.Bind("Lobby", "SharedObjects", 0, "When everyone plays at once: 0 = nothing shared, 1 = loose props, 2 = everything.");
            LobbyCollide = Config.Bind("Lobby", "PlayersCollide", false, "When sharing everything: players bump into each other.");
            LobbyVoice = Config.Bind("Lobby", "VoiceChat", true, "Voice chat when hosting: everyone in the lobby, and by distance while playing a round together.");
            RecentAddresses = Config.Bind("Direct Connect", "RecentAddresses", "", "Recently joined addresses, most recent first (comma separated).");
            CheckForUpdates = Config.Bind("Updates", "CheckForUpdates", true, "Look for a new Glurp Online release on GitHub when the game starts.");
            PeerUpdates = Config.Bind("Updates", "PeerUpdates", true,
                "Swap updates with players on a different version when joining. Only officially signed releases are ever installed.");

            Update.Updater.Init();
            new Harmony(Guid).PatchAll(typeof(Plugin).Assembly);

            Game.OnlineGame.Init();
            Game.VoiceChat.Init();
            UI.OnlineUI.Init();

            var host = new GameObject("GlurpOnline");
            DontDestroyOnLoad(host);
            host.hideFlags = HideFlags.HideAndDontSave;
            host.AddComponent<Runner>();

            Log.LogInfo($"{Name} {Version} loaded (protocol {ProtocolVersion})");
        }
    }
}
