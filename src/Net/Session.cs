using System;
using System.Collections.Generic;
using System.Linq;
using GlurpOnline.Update;
using Steamworks;
using UnityEngine;

namespace GlurpOnline.Net
{
    internal enum SessionKind { Steam, Direct }

    internal enum SessionPhase { Connecting, Lobby, InGame }

    internal sealed class Session
    {
        public static Session Current { get; private set; }

        public static event Action LobbyChanged;
        public static event Action<string> SessionClosed;
        public static event Action SessionJoined;
        public static event Action<Msg, ulong, PacketReader> GameMessage;
        public static event Action<Member> MemberJoined;
        public static event Action<Member> MemberLeft;

        public SessionKind Kind { get; private set; }
        public SessionPhase Phase { get; private set; }
        public bool Creating { get; private set; }
        public ulong HostId { get; private set; }
        public ulong LocalId => transport.LocalId;
        public bool IsHost => HostId == LocalId;
        public CSteamID LobbyId { get; private set; }
        public string DirectAddress { get; private set; }
        public readonly List<Member> Members = new List<Member>();
        public LobbySettings Settings = new LobbySettings();

        public Member Self => Members.FirstOrDefault(m => m.Id == LocalId);
        public Member Host => Members.FirstOrDefault(m => m.Id == HostId);
        public int TotalPlayers => Members.Sum(m => Math.Max(1, m.LocalPlayers));

        private ITransport transport;
        private CallResult<LobbyCreated_t> lobbyCreated;
        private CallResult<LobbyEnter_t> lobbyEnter;
        private Callback<LobbyChatUpdate_t> chatUpdate;
        private float connectDeadline;
        private float nextPing;
        private bool closed;
        private Member pendingSelf;
        private readonly HashSet<ulong> updateRequesters = new HashSet<ulong>();
        private readonly Dictionary<ulong, PeerTransfer> transfers = new Dictionary<ulong, PeerTransfer>();
        private PeerTransfer download;

        private static ulong directId;

        private static readonly System.Reflection.FieldInfo SteamManagerInstance = HarmonyLib.AccessTools.Field(typeof(SteamManager), "s_instance");

        public static bool SteamAvailable
        {
            get
            {
                try
                {
                    if (GameManager.singleton == null || SteamManagerInstance?.GetValue(null) == null) return false;
                    return SteamManager.Initialized;
                }
                catch { return false; }
            }
        }

        public static Session HostSteam(LobbySettings settings)
        {
            var s = new Session { Kind = SessionKind.Steam, Settings = settings.Clone(), Creating = true };
            s.Begin(new SteamTransport(s.IsAllowedPeer));
            s.lobbyCreated = CallResult<LobbyCreated_t>.Create(s.OnLobbyCreated);
            s.lobbyCreated.Set(SteamMatchmaking.CreateLobby(PrivacyToLobbyType(settings.Privacy), Mathf.Clamp(settings.MaxPlayers, 2, 16)));
            return s;
        }

        public static Session JoinSteam(CSteamID lobby)
        {
            var s = new Session { Kind = SessionKind.Steam };
            s.Begin(new SteamTransport(s.IsAllowedPeer));
            s.LobbyId = lobby;
            s.lobbyEnter = CallResult<LobbyEnter_t>.Create(s.OnLobbyEntered);
            s.lobbyEnter.Set(SteamMatchmaking.JoinLobby(lobby));
            return s;
        }

        public static Session HostDirect(LobbySettings settings, int port)
        {
            var s = new Session { Kind = SessionKind.Direct, Settings = settings.Clone() };
            TcpTransport t;
            try
            {
                t = TcpTransport.Listen(port, DirectId());
            }
            catch (Exception e)
            {
                Plugin.Log.LogError("Could not listen: " + e);
                SessionClosed?.Invoke($"Couldn't host on port {port}.\n{e.Message}");
                return null;
            }
            s.Begin(t);
            s.DirectAddress = LocalAddressHint() + ":" + port;
            s.BecomeLobbyHost();
            return s;
        }

        public static Session JoinDirect(string address, int port)
        {
            var s = new Session { Kind = SessionKind.Direct };
            TcpTransport t;
            try
            {
                t = TcpTransport.Connect(address, port, DirectId(), Time.realtimeSinceStartup);
            }
            catch (Exception e)
            {
                SessionClosed?.Invoke($"Couldn't reach {address}.\n{e.Message}");
                return null;
            }
            t.Connected += s.SendHello;
            t.ConnectFailed += () => s.Close($"Couldn't connect to {address}:{port}.");
            s.HostId = TcpTransport.HostAlias;
            s.DirectAddress = address + ":" + port;
            s.Begin(t);
            return s;
        }

        private void Begin(ITransport t)
        {
            Current?.Leave();
            Current = this;
            Application.runInBackground = true;
            transport = t;
            transport.PeerDisconnected += OnPeerDisconnected;
            Phase = SessionPhase.Connecting;
            connectDeadline = Time.realtimeSinceStartup + 20f;
            pendingSelf = CreateSelf();
            if (Kind == SessionKind.Steam) chatUpdate = Callback<LobbyChatUpdate_t>.Create(OnLobbyChatUpdate);
        }

        private Member CreateSelf()
        {
            return new Member
            {
                Id = transport.LocalId,
                Name = LocalName(),
                ColorIndex = Plugin.LastColorIndex.Value,
                HatId = string.IsNullOrEmpty(Plugin.LastHatId.Value) ? null : Plugin.LastHatId.Value,
                LocalPlayers = 1,
            };
        }

        public static string LocalName()
        {
            if (SteamAvailable)
            {
                try { return SteamFriends.GetPersonaName(); } catch { }
            }
            return Environment.UserName;
        }

        private static ulong DirectId()
        {
            if (directId == 0)
            {
                var bytes = new byte[8];
                new System.Random(Environment.TickCount ^ System.Diagnostics.Process.GetCurrentProcess().Id).NextBytes(bytes);
                directId = BitConverter.ToUInt64(bytes, 0) | 0x8000000000000000UL;
            }
            return directId;
        }

        public static string LocalAddressHint()
        {
            try
            {
                foreach (var a in System.Net.Dns.GetHostAddresses(System.Net.Dns.GetHostName()))
                {
                    if (a.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork && !System.Net.IPAddress.IsLoopback(a)) return a.ToString();
                }
            }
            catch { }
            return "127.0.0.1";
        }

        private static ELobbyType PrivacyToLobbyType(int privacy)
        {
            switch (privacy)
            {
                case 1: return ELobbyType.k_ELobbyTypePublic;
                case 2: return ELobbyType.k_ELobbyTypePrivate;
                default: return ELobbyType.k_ELobbyTypeFriendsOnly;
            }
        }

        private void OnLobbyCreated(LobbyCreated_t result, bool ioFailure)
        {
            if (closed) return;
            if (ioFailure || result.m_eResult != EResult.k_EResultOK)
            {
                Close("Steam couldn't create a lobby (" + result.m_eResult + ").");
                return;
            }
            LobbyId = new CSteamID(result.m_ulSteamIDLobby);
            BecomeLobbyHost();
        }

        private void OnLobbyEntered(LobbyEnter_t result, bool ioFailure)
        {
            if (closed) return;
            if (ioFailure || result.m_EChatRoomEnterResponse != (uint)EChatRoomEnterResponse.k_EChatRoomEnterResponseSuccess)
            {
                Close(EnterFailureReason((EChatRoomEnterResponse)result.m_EChatRoomEnterResponse));
                return;
            }
            LobbyId = new CSteamID(result.m_ulSteamIDLobby);
            if (string.IsNullOrEmpty(SteamMatchmaking.GetLobbyData(LobbyId, "glurponline")))
            {
                Close("That lobby isn't a Glurp Online game.");
                return;
            }
            HostId = SteamMatchmaking.GetLobbyOwner(LobbyId).m_SteamID;
            if (HostId == LocalId)
            {
                BecomeLobbyHost();
                return;
            }
            SendHello();
        }

        private static string EnterFailureReason(EChatRoomEnterResponse r)
        {
            switch (r)
            {
                case EChatRoomEnterResponse.k_EChatRoomEnterResponseDoesntExist: return "That game doesn't exist any more.";
                case EChatRoomEnterResponse.k_EChatRoomEnterResponseFull: return "That game is full.";
                case EChatRoomEnterResponse.k_EChatRoomEnterResponseNotAllowed: return "You're not allowed to join that game.";
                case EChatRoomEnterResponse.k_EChatRoomEnterResponseBanned: return "You've been removed from that game.";
                default: return "Couldn't join the game (" + r + ").";
            }
        }

        private void OnLobbyChatUpdate(LobbyChatUpdate_t update)
        {
            if (closed || update.m_ulSteamIDLobby != LobbyId.m_SteamID) return;
            const uint leftMask = (uint)(EChatMemberStateChange.k_EChatMemberStateChangeLeft | EChatMemberStateChange.k_EChatMemberStateChangeDisconnected
                                        | EChatMemberStateChange.k_EChatMemberStateChangeKicked | EChatMemberStateChange.k_EChatMemberStateChangeBanned);
            if ((update.m_rgfChatMemberStateChange & leftMask) == 0) return;
            ulong who = update.m_ulSteamIDUserChanged;
            if (IsHost)
            {
                RemoveMember(who);
            }
            else if (who == HostId)
            {
                MigrateHost();
            }
        }

        private void MigrateHost()
        {
            ulong oldHost = HostId;
            ulong newHost = SteamMatchmaking.GetLobbyOwner(LobbyId).m_SteamID;
            var departed = Members.FirstOrDefault(m => m.Id == oldHost);
            Members.RemoveAll(m => m.Id == oldHost);
            HostId = newHost;
            foreach (var m in Members) m.IsHost = m.Id == newHost;
            Plugin.Log.LogInfo($"Host {oldHost} left; new host is {newHost}");
            if (departed != null) MemberLeft?.Invoke(departed);
            if (IsHost)
            {
                WriteLobbyData();
                SetRichPresence();
                BroadcastLobbyState();
                if (Phase == SessionPhase.InGame) BroadcastPlayerDropped(oldHost);
            }
            LobbyChanged?.Invoke();
        }

        private void BecomeLobbyHost()
        {
            HostId = LocalId;
            Phase = SessionPhase.Lobby;
            Creating = false;
            var self = pendingSelf ?? CreateSelf();
            self.IsHost = true;
            self.Ready = true;
            Members.Clear();
            Members.Add(self);
            AssignColors();
            WriteLobbyData();
            SetRichPresence();
            Plugin.Log.LogInfo($"Hosting {Kind} lobby {(Kind == SessionKind.Steam ? LobbyId.ToString() : DirectAddress)}");
            SessionJoined?.Invoke();
            LobbyChanged?.Invoke();
        }

        public void ApplySettings(LobbySettings settings)
        {
            if (!IsHost) return;
            bool privacyChanged = settings.Privacy != Settings.Privacy;
            bool maxChanged = settings.MaxPlayers != Settings.MaxPlayers;
            Settings = settings.Clone();
            if (Kind == SessionKind.Steam && LobbyId.IsValid())
            {
                if (privacyChanged) SteamMatchmaking.SetLobbyType(LobbyId, PrivacyToLobbyType(Settings.Privacy));
                if (maxChanged) SteamMatchmaking.SetLobbyMemberLimit(LobbyId, Mathf.Clamp(Settings.MaxPlayers, 2, 16));
            }
            WriteLobbyData();
            BroadcastLobbyState();
            LobbyChanged?.Invoke();
        }

        private void WriteLobbyData()
        {
            if (Kind != SessionKind.Steam || !LobbyId.IsValid() || !IsHost) return;
            SteamMatchmaking.SetLobbyData(LobbyId, "glurponline", Plugin.ProtocolVersion.ToString());
            SteamMatchmaking.SetLobbyData(LobbyId, "modver", Plugin.Version);
            SteamMatchmaking.SetLobbyData(LobbyId, "gamever", GameManager.singleton != null ? GameManager.singleton.glurpVersion : "");
            SteamMatchmaking.SetLobbyData(LobbyId, "name", (Self?.Name ?? LocalName()) + "'s game");
            SteamMatchmaking.SetLobbyData(LobbyId, "players", TotalPlayers.ToString());
            SteamMatchmaking.SetLobbyData(LobbyId, "max", Settings.MaxPlayers.ToString());
            SteamMatchmaking.SetLobbyData(LobbyId, "rounds", Settings.Rounds.ToString());
            SteamMatchmaking.SetLobbyData(LobbyId, "difficulty", Settings.Difficulty.ToString());
            SteamMatchmaking.SetLobbyData(LobbyId, "mode", Settings.Mode.ToString());
            SteamMatchmaking.SetLobbyData(LobbyId, "phase", Phase == SessionPhase.InGame ? "playing" : "lobby");
            SteamMatchmaking.SetLobbyJoinable(LobbyId, Phase != SessionPhase.InGame);
        }

        private void SetRichPresence()
        {
            if (!SteamAvailable) return;
            SteamFriends.SetRichPresence("status", Phase == SessionPhase.InGame ? "Playing Glurp Online" : "In a Glurp Online lobby");
            if (Kind == SessionKind.Steam && LobbyId.IsValid()) SteamFriends.SetRichPresence("connect", "+connect_lobby " + LobbyId.m_SteamID);
            else if (Kind == SessionKind.Direct && IsHost && DirectAddress != null) SteamFriends.SetRichPresence("connect", "+glurp_direct " + DirectAddress);
        }

        public void SetPhase(SessionPhase phase)
        {
            Phase = phase;
            if (phase == SessionPhase.Lobby)
            {
                foreach (var m in Members) m.Ready = m.IsHost;
            }
            if (IsHost)
            {
                WriteLobbyData();
                BroadcastLobbyState();
            }
            SetRichPresence();
            LobbyChanged?.Invoke();
        }

        public void Kick(ulong id)
        {
            if (!IsHost || id == LocalId) return;
            Send(id, new PacketWriter(Msg.Kick, LocalId));
            RemoveMember(id);
        }

        private void RemoveMember(ulong id)
        {
            var m = Members.FirstOrDefault(x => x.Id == id);
            if (m == null) return;
            Members.Remove(m);
            transport.Disconnect(id);
            Plugin.Log.LogInfo($"{m.Name} left");
            WriteLobbyData();
            BroadcastLobbyState();
            if (Phase == SessionPhase.InGame) BroadcastPlayerDropped(id);
            MemberLeft?.Invoke(m);
            LobbyChanged?.Invoke();
        }

        private void BroadcastPlayerDropped(ulong id)
        {
            var w = new PacketWriter(Msg.PlayerDropped, LocalId);
            w.Write(id);
            Broadcast(w);
            GameMessage?.Invoke(Msg.PlayerDropped, LocalId, new PacketReader(w.Buffer, 9, w.Length - 9));
        }

        private void AssignColors()
        {
            if (!IsHost || GameManager.singleton == null) return;
            int count = GameManager.singleton.playerColors.Length;
            var used = new HashSet<int>();
            foreach (var m in Members)
            {
                if (m.ColorIndex < 0 || m.ColorIndex >= count || !used.Add(m.ColorIndex)) m.ColorIndex = -1;
            }
            var rng = new System.Random();
            foreach (var m in Members)
            {
                if (m.ColorIndex >= 0) continue;
                var free = Enumerable.Range(0, count).Where(c => !used.Contains(c)).ToList();
                m.ColorIndex = free.Count > 0 ? free[rng.Next(free.Count)] : rng.Next(count);
                used.Add(m.ColorIndex);
            }
        }

        private void BroadcastLobbyState()
        {
            if (!IsHost) return;
            AssignColors();
            var w = new PacketWriter(Msg.LobbyState, LocalId);
            WriteLobbyState(w);
            Broadcast(w);
        }

        private void WriteLobbyState(PacketWriter w)
        {
            w.Write((byte)Phase);
            Settings.Write(w);
            w.Write((byte)Members.Count);
            foreach (var m in Members) m.Write(w);
        }

        private void SendHello()
        {
            var self = pendingSelf ?? CreateSelf();
            var w = new PacketWriter(Msg.Hello, LocalId);
            w.Write(Plugin.ProtocolVersion);
            w.Write(Plugin.Version);
            w.Write(Updater.CanShare);
            w.Write(self.Name);
            w.Write(GameManager.singleton != null ? GameManager.singleton.glurpVersion : "");
            w.Write(ContentSignature.Compute());
            self.Write(w);
            Send(HostId, w);
        }

        private void HandleHello(ulong sender, PacketReader r)
        {
            int protocol = r.ReadInt();
            string modVersion = r.ReadString();
            bool theyCanShare = r.ReadBool();
            string theirName = r.ReadString();
            if (protocol != Plugin.ProtocolVersion || modVersion != Plugin.Version)
            {
                HandleVersionMismatch(sender, modVersion, theyCanShare, theirName);
                return;
            }

            string gameVersion = r.ReadString();
            string content = r.ReadString();
            var member = Member.Read(r);
            member.Id = sender;
            member.IsHost = false;
            member.Ready = false;
            member.LocalPlayers = Mathf.Clamp(member.LocalPlayers, 1, 4);

            string reject = null;
            if (GameManager.singleton != null && gameVersion != GameManager.singleton.glurpVersion) reject = $"Game version mismatch: host has Drink More Glurp {GameManager.singleton.glurpVersion}, you have {gameVersion}.";
            else if (content != ContentSignature.Compute()) reject = "Your installed events/brands don't match the host's. Make sure you both have the same mods.";
            else if (Phase == SessionPhase.InGame) reject = "That game has already started.";
            else if (TotalPlayers + member.LocalPlayers > Settings.MaxPlayers && !Members.Any(m => m.Id == sender)) reject = "That game is full.";

            if (reject != null)
            {
                Reject(sender, reject);
                return;
            }

            bool isNew = true;
            int existing = Members.FindIndex(m => m.Id == sender);
            if (existing >= 0) { Members[existing] = member; isNew = false; }
            else Members.Add(member);

            Send(sender, new PacketWriter(Msg.Welcome, LocalId));
            WriteLobbyData();
            BroadcastLobbyState();
            if (isNew) MemberJoined?.Invoke(member);
            LobbyChanged?.Invoke();
        }

        private void Reject(ulong peer, string reason)
        {
            var w = new PacketWriter(Msg.Reject, LocalId);
            w.Write(reason);
            w.Write(Plugin.Version);
            w.Write(Updater.CanShare);
            Send(peer, w);
            Plugin.Log.LogInfo($"Rejected {peer}: {reason}");
        }

        private void HandleVersionMismatch(ulong sender, string theirVersion, bool theyCanShare, string theirName)
        {
            string name = string.IsNullOrEmpty(theirName) ? "A player" : theirName.Length > 24 ? theirName.Substring(0, 24) : theirName;
            if (!Updater.IsNewer(theirVersion))
            {
                if (Updater.CanShare) updateRequesters.Add(sender);
                Reject(sender, $"The host has Glurp Online {Plugin.Version} and you have {theirVersion}.");
                return;
            }
            if (theyCanShare && Updater.PeerUpdates && Phase == SessionPhase.Lobby)
            {
                if (!transfers.ContainsKey(sender))
                {
                    transfers[sender] = new PeerTransfer { SourceName = name };
                    Send(sender, new PacketWriter(Msg.UpdateRequest, LocalId));
                }
                return;
            }
            Updater.OnNewerPeer(name, theirVersion);
            Reject(sender, $"The host needs to update Glurp Online: they have {Plugin.Version} and you have {theirVersion}.");
        }

        private void HandleUpdateDataAsHost(ulong sender, PacketReader r)
        {
            if (!transfers.TryGetValue(sender, out var transfer) || !transfer.Receive(r, out var package)) return;
            transfers.Remove(sender);
            if (package != null && package.Version > Updater.Current)
            {
                Reject(sender, $"The host needs to update to Glurp Online {package.VersionText}. Try again once they've restarted.");
                Updater.OfferFromPeer(package, transfer.SourceName, null);
            }
            else
            {
                Reject(sender, $"The host needs to update Glurp Online: they have {Plugin.Version}.");
            }
        }

        private void HandleRejectAsClient(PacketReader r)
        {
            string reason = r.ReadString();
            string hostVersion = r.Remaining > 0 ? r.ReadString() : null;
            bool hostCanShare = r.Remaining > 0 && r.ReadBool();
            if (Phase == SessionPhase.Connecting && download == null && hostCanShare && Updater.PeerUpdates && Updater.IsNewer(hostVersion))
            {
                download = new PeerTransfer { SourceName = "The host" };
                connectDeadline = Time.realtimeSinceStartup + 120f;
                Send(HostId, new PacketWriter(Msg.UpdateRequest, LocalId));
                Updater.OnDownloadingFromPeer(hostVersion);
                return;
            }
            Close(reason);
        }

        private void HandleUpdateDataAsClient(PacketReader r)
        {
            if (download == null || !download.Receive(r, out var package)) return;
            var transfer = download;
            download = null;
            if (package == null)
            {
                Close("The host's update didn't pass the signature check, so it wasn't installed.");
                return;
            }
            string rejoin = Kind == SessionKind.Steam ? "+connect_lobby " + LobbyId.m_SteamID : "+glurp_direct " + DirectAddress;
            Leave();
            Updater.OfferFromPeer(package, transfer.SourceName, rejoin);
        }

        public void UpdateSelf(Action<Member> change)
        {
            var self = Self ?? pendingSelf;
            if (self == null) return;
            change(self);
            if (IsHost)
            {
                self.Ready = true;
                WriteLobbyData();
                BroadcastLobbyState();
            }
            else
            {
                var w = new PacketWriter(Msg.MemberUpdate, LocalId);
                self.Write(w);
                Send(HostId, w);
            }
            LobbyChanged?.Invoke();
        }

        private bool IsAllowedPeer(ulong id)
        {
            if (closed) return false;
            if (Kind != SessionKind.Steam) return true;
            if (!LobbyId.IsValid()) return id == HostId;
            int n = SteamMatchmaking.GetNumLobbyMembers(LobbyId);
            for (int i = 0; i < n; i++)
            {
                if (SteamMatchmaking.GetLobbyMemberByIndex(LobbyId, i).m_SteamID == id) return true;
            }
            return false;
        }

        public void Send(ulong peer, PacketWriter w)
        {
            if (closed || peer == LocalId) return;
            transport.Send(peer, w.Buffer, w.Length);
        }

        public void Broadcast(PacketWriter w)
        {
            if (closed) return;
            if (IsHost)
            {
                foreach (var m in Members)
                {
                    if (m.Id != LocalId) transport.Send(m.Id, w.Buffer, w.Length);
                }
            }
            else
            {
                transport.Send(HostId, w.Buffer, w.Length);
            }
        }

        public void Update()
        {
            if (closed) return;
            try
            {
                transport.Poll(OnPacket);
            }
            catch (Exception e)
            {
                Plugin.Log.LogError("Network poll failed: " + e);
            }
            if (closed) return;

            float now = Time.realtimeSinceStartup;
            if (download != null && now - download.LastActivity > PeerTransfer.Timeout)
            {
                Close("Timed out downloading the update from the host.");
                return;
            }
            if (Phase == SessionPhase.Connecting && now > connectDeadline)
            {
                Close("Timed out joining the game.");
                return;
            }
            foreach (var stalled in transfers.Where(t => now - t.Value.LastActivity > PeerTransfer.Timeout).Select(t => t.Key).ToList())
            {
                transfers.Remove(stalled);
                Reject(stalled, $"The host needs to update Glurp Online: they have {Plugin.Version}.");
            }
            if (IsHost && Phase != SessionPhase.Connecting && now > nextPing)
            {
                nextPing = now + 2f;
                foreach (var m in Members)
                {
                    if (m.Id == LocalId) continue;
                    var w = new PacketWriter(Msg.Ping, LocalId);
                    w.Write(now);
                    Send(m.Id, w);
                }
                if (Phase == SessionPhase.Lobby && Members.Count > 1) BroadcastLobbyState();
            }
        }

        private void OnPacket(ulong sender, byte[] data, int length)
        {
            if (length < 9) return;
            var r = new PacketReader(data, 0, length);
            var type = (Msg)r.ReadByte();
            ulong origin = r.ReadULong();
            try
            {
                if (IsHost) HandleAsHost(type, sender, origin, r, data, length);
                else HandleAsClient(type, origin, r);
            }
            catch (Exception e)
            {
                Plugin.Log.LogError($"Error handling {type} from {origin}: {e}");
            }
        }

        private void HandleAsHost(Msg type, ulong sender, ulong origin, PacketReader r, byte[] raw, int length)
        {
            if (origin != sender) return;
            switch (type)
            {
                case Msg.Hello:
                    HandleHello(sender, r);
                    return;
                case Msg.UpdateRequest:
                    if (updateRequesters.Remove(sender) && Updater.CanShare) PeerTransfer.Send(this, sender);
                    return;
                case Msg.UpdateData:
                    HandleUpdateDataAsHost(sender, r);
                    return;
            }
            var member = Members.FirstOrDefault(m => m.Id == sender);
            if (member == null) return;

            switch (type)
            {
                case Msg.MemberUpdate:
                    {
                        var u = Member.Read(r);
                        member.Name = u.Name;
                        member.ColorIndex = u.ColorIndex;
                        member.HatId = u.HatId;
                        member.LocalPlayers = Mathf.Clamp(u.LocalPlayers, 1, 4);
                        member.Ready = u.Ready;
                        WriteLobbyData();
                        BroadcastLobbyState();
                        LobbyChanged?.Invoke();
                        break;
                    }
                case Msg.Pong:
                    member.PingMs = Mathf.RoundToInt((Time.realtimeSinceStartup - r.ReadFloat()) * 1000f);
                    break;
                case Msg.Bye:
                    RemoveMember(sender);
                    break;
                default:
                    if (MsgInfo.IsRelayed(type))
                    {
                        foreach (var m in Members)
                        {
                            if (m.Id != LocalId && m.Id != sender) transport.Send(m.Id, raw, length);
                        }
                        GameMessage?.Invoke(type, origin, r);
                    }
                    else if (MsgInfo.IsForHostOnly(type))
                    {
                        GameMessage?.Invoke(type, origin, r);
                    }
                    break;
            }
        }

        private void HandleAsClient(Msg type, ulong origin, PacketReader r)
        {
            switch (type)
            {
                case Msg.Welcome:
                    if (Kind == SessionKind.Direct) HostId = origin;
                    if (origin != HostId) return;
                    if (Phase == SessionPhase.Connecting)
                    {
                        Phase = SessionPhase.Lobby;
                        SetRichPresence();
                        SessionJoined?.Invoke();
                    }
                    break;
                case Msg.Reject:
                    HandleRejectAsClient(r);
                    break;
                case Msg.UpdateRequest:
                    if (Phase == SessionPhase.Connecting && Updater.CanShare) PeerTransfer.Send(this, HostId);
                    break;
                case Msg.UpdateData:
                    HandleUpdateDataAsClient(r);
                    break;
                case Msg.Kick:
                    Close("The host removed you from the game.");
                    break;
                case Msg.LobbyState:
                    {
                        if (Kind == SessionKind.Direct && HostId == TcpTransport.HostAlias) HostId = origin;
                        if (origin != HostId) return;
                        var phase = (SessionPhase)r.ReadByte();
                        Settings = LobbySettings.Read(r);
                        int n = r.ReadByte();
                        var previous = Members.ToList();
                        Members.Clear();
                        for (int i = 0; i < n; i++) Members.Add(Member.Read(r));
                        if (Phase != SessionPhase.Connecting) Phase = phase;
                        foreach (var m in Members) if (!previous.Any(p => p.Id == m.Id) && previous.Count > 0) MemberJoined?.Invoke(m);
                        foreach (var p in previous) if (!Members.Any(m => m.Id == p.Id)) MemberLeft?.Invoke(p);
                        LobbyChanged?.Invoke();
                        break;
                    }
                case Msg.Ping:
                    {
                        var w = new PacketWriter(Msg.Pong, LocalId);
                        w.Write(r.ReadFloat());
                        Send(HostId, w);
                        break;
                    }
                case Msg.Bye:
                    if (origin == HostId && Kind == SessionKind.Direct) Close("The host closed the game.");
                    break;
                default:
                    if ((int)type >= 20) GameMessage?.Invoke(type, origin, r);
                    break;
            }
        }

        private void OnPeerDisconnected(ulong id)
        {
            if (closed) return;
            if (IsHost)
            {
                updateRequesters.Remove(id);
                transfers.Remove(id);
                RemoveMember(id);
            }
            else if (id == HostId || Kind == SessionKind.Direct)
            {
                if (Kind == SessionKind.Steam && LobbyId.IsValid() && SteamMatchmaking.GetLobbyOwner(LobbyId).m_SteamID != HostId)
                {
                    MigrateHost();
                }
                else
                {
                    Close("Lost connection to the host.");
                }
            }
        }

        public void Leave()
        {
            if (closed) return;
            try
            {
                var bye = new PacketWriter(Msg.Bye, LocalId);
                if (IsHost) Broadcast(bye);
                else if (Phase != SessionPhase.Connecting) Send(HostId, bye);
                transport.Poll((a, b, c) => { });
            }
            catch { }
            Shutdown(null);
        }

        public void Close(string reason)
        {
            if (closed) return;
            Plugin.Log.LogWarning("Session closed: " + reason);
            Shutdown(reason);
        }

        private void Shutdown(string reason)
        {
            closed = true;
            try
            {
                if (Kind == SessionKind.Steam && LobbyId.IsValid()) SteamMatchmaking.LeaveLobby(LobbyId);
                if (SteamAvailable) SteamFriends.ClearRichPresence();
            }
            catch (Exception e) { Plugin.Log.LogWarning(e.ToString()); }
            try { transport?.Shutdown(); } catch (Exception e) { Plugin.Log.LogWarning(e.ToString()); }
            lobbyCreated?.Dispose();
            lobbyEnter?.Dispose();
            chatUpdate?.Dispose();
            if (Current == this) Current = null;
            SessionClosed?.Invoke(reason);
            LobbyChanged?.Invoke();
        }
    }

    internal static class ContentSignature
    {
        public static string Compute()
        {
            var ids = new List<string>();
            foreach (var e in EventRandomiser.eventList) ids.Add("e:" + e.id);
            foreach (var m in BrandManager.mutators) ids.Add("m:" + m.id);
            ids.Sort(StringComparer.Ordinal);
            ulong hash = 14695981039346656037UL;
            foreach (var s in ids)
            {
                foreach (char c in s) { hash ^= c; hash *= 1099511628211UL; }
                hash ^= '|'; hash *= 1099511628211UL;
            }
            return hash.ToString("x16");
        }
    }
}
