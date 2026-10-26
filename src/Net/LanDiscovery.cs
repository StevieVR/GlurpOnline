using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Text;
using UnityEngine;

namespace GlurpOnline.Net
{
    internal static class LanDiscovery
    {
        public const int BeaconPort = 27421;
        private const string Magic = "GLURPONLINE";

        public sealed class Game
        {
            public string Address;
            public int Port;
            public string Name;
            public int Players, Max;
            public bool Playing;
            public bool Compatible;
            public string Version;
            public float LastSeen;
            public string Endpoint => Address + ":" + Port;
        }

        public static readonly List<Game> Games = new List<Game>();
        public static event Action Changed;

        private static UdpClient beaconSocket;
        private static float nextBeacon;
        private static UdpClient listenSocket;

        public static bool Listening => listenSocket != null;

        public static void Update()
        {
            var s = Session.Current;
            if (s != null && s.Kind == SessionKind.Direct && s.IsHost && s.Phase != SessionPhase.Connecting) Beacon(s);
            else CloseBeacon();
            if (listenSocket != null) Receive();
        }

        private static void Beacon(Session s)
        {
            if (Time.realtimeSinceStartup < nextBeacon) return;
            nextBeacon = Time.realtimeSinceStartup + 1f;
            try
            {
                if (beaconSocket == null) beaconSocket = new UdpClient { EnableBroadcast = true };
                string name = (s.Self?.Name ?? Session.LocalName()).Replace("|", "/");
                string text = string.Join("|", new[]
                {
                    Magic, Plugin.ProtocolVersion.ToString(), Plugin.DirectPort.Value.ToString(), s.TotalPlayers.ToString(),
                    s.Settings.MaxPlayers.ToString(), s.Phase == SessionPhase.InGame ? "1" : "0", name, Plugin.Version,
                });
                var bytes = Encoding.UTF8.GetBytes(text);
                beaconSocket.Send(bytes, bytes.Length, new IPEndPoint(IPAddress.Broadcast, BeaconPort));
                beaconSocket.Send(bytes, bytes.Length, new IPEndPoint(IPAddress.Loopback, BeaconPort));
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning("LAN beacon failed: " + e.Message);
                nextBeacon = Time.realtimeSinceStartup + 10f;
            }
        }

        private static void CloseBeacon()
        {
            if (beaconSocket == null) return;
            try { beaconSocket.Close(); } catch { }
            beaconSocket = null;
        }

        public static void StartListening()
        {
            if (listenSocket != null) return;
            try
            {
                var socket = new UdpClient { ExclusiveAddressUse = false };
                socket.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
                socket.Client.Bind(new IPEndPoint(IPAddress.Any, BeaconPort));
                socket.Client.Blocking = false;
                listenSocket = socket;
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning("LAN discovery unavailable: " + e.Message);
            }
        }

        public static void StopListening()
        {
            if (listenSocket == null) return;
            try { listenSocket.Close(); } catch { }
            listenSocket = null;
            Games.Clear();
        }

        private static void Receive()
        {
            bool changed = false;
            try
            {
                while (listenSocket.Available > 0)
                {
                    var from = new IPEndPoint(IPAddress.Any, 0);
                    var bytes = listenSocket.Receive(ref from);
                    changed |= Parse(Encoding.UTF8.GetString(bytes), from.Address);
                }
            }
            catch (SocketException) { }
            catch (ObjectDisposedException) { return; }

            float now = Time.realtimeSinceStartup;
            changed |= Games.RemoveAll(g => now - g.LastSeen > 4f) > 0;
            if (changed) Changed?.Invoke();
        }

        private static bool Parse(string text, IPAddress from)
        {
            var parts = text.Split('|');
            if (parts.Length < 7 || parts[0] != Magic) return false;
            if (!int.TryParse(parts[2], out int port)) return false;
            int.TryParse(parts[3], out int players);
            int.TryParse(parts[4], out int max);
            bool loopback = IPAddress.IsLoopback(from);
            string address = from.ToString();

            Game game = Games.Find(g => g.Port == port && g.Name == parts[6] && (g.Address == address || loopback || IPAddress.TryParse(g.Address, out var a) && IPAddress.IsLoopback(a)));
            bool isNew = game == null;
            if (isNew)
            {
                game = new Game();
                Games.Add(game);
            }
            if (isNew || !loopback) game.Address = address;
            game.Port = port;
            game.Name = parts[6];
            game.Players = players;
            game.Max = max;
            game.Playing = parts[5] == "1";
            game.Compatible = true;
            game.Version = parts.Length > 7 ? parts[7] : "";
            game.LastSeen = Time.realtimeSinceStartup;
            return isNew;
        }
    }
}
