using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using Steamworks;

namespace GlurpOnline.Net
{
    internal interface ITransport
    {
        ulong LocalId { get; }
        bool Send(ulong peer, byte[] data, int length);
        void Poll(Action<ulong, byte[], int> onPacket);
        void Disconnect(ulong peer);
        void Shutdown();
        event Action<ulong> PeerDisconnected;
    }

    internal sealed class SteamTransport : ITransport
    {
        private const int Channel = 0;
        private readonly Func<ulong, bool> isAllowed;
        private Callback<P2PSessionRequest_t> sessionRequest;
        private Callback<P2PSessionConnectFail_t> connectFail;
        private readonly HashSet<ulong> peers = new HashSet<ulong>();
        private readonly Dictionary<ulong, float> pendingRequests = new Dictionary<ulong, float>();
        private byte[] readBuffer = new byte[64 * 1024];

        public event Action<ulong> PeerDisconnected;

        public ulong LocalId { get; }

        public SteamTransport(Func<ulong, bool> isAllowed)
        {
            this.isAllowed = isAllowed;
            LocalId = SteamUser.GetSteamID().m_SteamID;
            SteamNetworking.AllowP2PPacketRelay(true);
            sessionRequest = Callback<P2PSessionRequest_t>.Create(OnSessionRequest);
            connectFail = Callback<P2PSessionConnectFail_t>.Create(OnConnectFail);
        }

        private void OnSessionRequest(P2PSessionRequest_t req)
        {
            ulong id = req.m_steamIDRemote.m_SteamID;
            if (isAllowed(id))
            {
                SteamNetworking.AcceptP2PSessionWithUser(req.m_steamIDRemote);
                peers.Add(id);
            }
            else
            {
                pendingRequests[id] = UnityEngine.Time.realtimeSinceStartup + 10f;
            }
        }

        private void RetryPendingRequests()
        {
            if (pendingRequests.Count == 0) return;
            float now = UnityEngine.Time.realtimeSinceStartup;
            foreach (var id in new List<ulong>(pendingRequests.Keys))
            {
                if (isAllowed(id))
                {
                    SteamNetworking.AcceptP2PSessionWithUser(new CSteamID(id));
                    peers.Add(id);
                    pendingRequests.Remove(id);
                }
                else if (now > pendingRequests[id])
                {
                    Plugin.Log.LogWarning($"Ignoring P2P session request from non-member {id}");
                    pendingRequests.Remove(id);
                }
            }
        }

        private void OnConnectFail(P2PSessionConnectFail_t fail)
        {
            ulong id = fail.m_steamIDRemote.m_SteamID;
            Plugin.Log.LogWarning($"P2P session with {id} failed (error {fail.m_eP2PSessionError})");
            peers.Remove(id);
            PeerDisconnected?.Invoke(id);
        }

        public bool Send(ulong peer, byte[] data, int length)
        {
            peers.Add(peer);
            return SteamNetworking.SendP2PPacket(new CSteamID(peer), data, (uint)length, EP2PSend.k_EP2PSendReliable, Channel);
        }

        public void Poll(Action<ulong, byte[], int> onPacket)
        {
            RetryPendingRequests();
            while (SteamNetworking.IsP2PPacketAvailable(out uint size, Channel))
            {
                if (size > readBuffer.Length) readBuffer = new byte[Math.Max(size, (uint)readBuffer.Length * 2)];
                if (!SteamNetworking.ReadP2PPacket(readBuffer, (uint)readBuffer.Length, out uint read, out CSteamID remote, Channel)) break;
                if (!isAllowed(remote.m_SteamID)) continue;
                onPacket(remote.m_SteamID, readBuffer, (int)read);
            }
        }

        public void Disconnect(ulong peer)
        {
            peers.Remove(peer);
            SteamNetworking.CloseP2PSessionWithUser(new CSteamID(peer));
        }

        public void Shutdown()
        {
            foreach (var p in peers) SteamNetworking.CloseP2PSessionWithUser(new CSteamID(p));
            peers.Clear();
            sessionRequest?.Dispose();
            connectFail?.Dispose();
            sessionRequest = null;
            connectFail = null;
        }
    }

    internal sealed class TcpTransport : ITransport
    {
        private sealed class Conn
        {
            public Socket Socket;
            public ulong PeerId;
            public byte[] Recv = new byte[64 * 1024];
            public int RecvLen;
            public readonly Queue<byte[]> Outgoing = new Queue<byte[]>();
            public int OutOffset;
            public bool Dead;
        }

        private readonly bool isHost;
        private Socket listener;
        private readonly List<Conn> conns = new List<Conn>();
        private Conn pendingConnect;
        private IAsyncResult connectResult;
        private float connectDeadline;
        private readonly ulong hostAlias;
        private bool polling, shutDown;

        public event Action<ulong> PeerDisconnected;
        public event Action ConnectFailed;
        public event Action Connected;

        public ulong LocalId { get; }

        public const ulong HostAlias = 1;

        public static TcpTransport Listen(int port, ulong localId)
        {
            var t = new TcpTransport(true, localId);
            t.listener = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            t.listener.Bind(new IPEndPoint(IPAddress.Any, port));
            t.listener.Listen(16);
            t.listener.Blocking = false;
            return t;
        }

        public static TcpTransport Connect(string host, int port, ulong localId, float now)
        {
            var t = new TcpTransport(false, localId);
            var addresses = Dns.GetHostAddresses(host);
            IPAddress address = null;
            foreach (var a in addresses) if (a.AddressFamily == AddressFamily.InterNetwork) { address = a; break; }
            if (address == null) throw new Exception("Could not resolve " + host);
            var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
            t.pendingConnect = new Conn { Socket = socket, PeerId = HostAlias };
            t.connectResult = socket.BeginConnect(new IPEndPoint(address, port), null, null);
            t.connectDeadline = now + 8f;
            return t;
        }

        private TcpTransport(bool isHost, ulong localId)
        {
            this.isHost = isHost;
            LocalId = localId;
            hostAlias = HostAlias;
        }

        public bool IsConnecting => pendingConnect != null;

        public bool Send(ulong peer, byte[] data, int length)
        {
            foreach (var c in conns)
            {
                if (c.Dead || (isHost && c.PeerId != peer)) continue;
                var frame = new byte[length + 4];
                frame[0] = (byte)length; frame[1] = (byte)(length >> 8); frame[2] = (byte)(length >> 16); frame[3] = (byte)(length >> 24);
                Buffer.BlockCopy(data, 0, frame, 4, length);
                c.Outgoing.Enqueue(frame);
                Flush(c);
                return true;
            }
            return false;
        }

        public void Poll(Action<ulong, byte[], int> onPacket)
        {
            if (shutDown) return;
            if (polling)
            {
                foreach (var c in conns) if (!c.Dead) Flush(c);
                return;
            }
            polling = true;
            try { PollConnections(onPacket); }
            finally { polling = false; }
        }

        private void PollConnections(Action<ulong, byte[], int> onPacket)
        {
            if (pendingConnect != null) PollConnect();
            if (listener != null) AcceptPending();
            for (int i = 0; i < conns.Count; i++)
            {
                var c = conns[i];
                if (!c.Dead) Receive(c, onPacket);
                if (!c.Dead && !shutDown) Flush(c);
            }
            for (int i = conns.Count - 1; i >= 0; i--)
            {
                var c = conns[i];
                if (!c.Dead) continue;
                conns.RemoveAt(i);
                try { c.Socket.Close(); } catch { }
                if (c.PeerId != 0) PeerDisconnected?.Invoke(c.PeerId);
            }
        }

        private void PollConnect()
        {
            if (connectResult.IsCompleted)
            {
                var c = pendingConnect;
                pendingConnect = null;
                try
                {
                    c.Socket.EndConnect(connectResult);
                    c.Socket.Blocking = false;
                    conns.Add(c);
                    var w = new PacketWriter();
                    w.Write(LocalId);
                    Send(HostAlias, w.Buffer, w.Length);
                    Connected?.Invoke();
                }
                catch (Exception e)
                {
                    Plugin.Log.LogWarning("Direct connect failed: " + e.Message);
                    try { c.Socket.Close(); } catch { }
                    ConnectFailed?.Invoke();
                }
            }
            else if (UnityEngine.Time.realtimeSinceStartup > connectDeadline)
            {
                try { pendingConnect.Socket.Close(); } catch { }
                pendingConnect = null;
                ConnectFailed?.Invoke();
            }
        }

        private void AcceptPending()
        {
            while (true)
            {
                Socket s;
                try { s = listener.Accept(); }
                catch (SocketException) { return; }
                s.Blocking = false;
                s.NoDelay = true;
                conns.Add(new Conn { Socket = s });
            }
        }

        private void Receive(Conn c, Action<ulong, byte[], int> onPacket)
        {
            try
            {
                while (c.Socket.Available > 0)
                {
                    if (c.RecvLen == c.Recv.Length) Array.Resize(ref c.Recv, c.Recv.Length * 2);
                    int n = c.Socket.Receive(c.Recv, c.RecvLen, c.Recv.Length - c.RecvLen, SocketFlags.None);
                    if (n <= 0) { c.Dead = true; return; }
                    c.RecvLen += n;
                }
                if (c.Socket.Poll(0, SelectMode.SelectRead) && c.Socket.Available == 0)
                {
                    c.Dead = true;
                }
            }
            catch (SocketException e)
            {
                if (e.SocketErrorCode != SocketError.WouldBlock) c.Dead = true;
            }

            int offset = 0;
            while (c.RecvLen - offset >= 4)
            {
                int len = c.Recv[offset] | (c.Recv[offset + 1] << 8) | (c.Recv[offset + 2] << 16) | (c.Recv[offset + 3] << 24);
                if (len < 0 || len > 16 * 1024 * 1024) { c.Dead = true; return; }
                if (c.RecvLen - offset - 4 < len)
                {
                    if (len + 4 > c.Recv.Length) Array.Resize(ref c.Recv, len + 4);
                    break;
                }
                if (c.PeerId == 0)
                {
                    c.PeerId = new PacketReader(c.Recv, offset + 4, len).ReadULong();
                }
                else
                {
                    var payload = new byte[len];
                    Buffer.BlockCopy(c.Recv, offset + 4, payload, 0, len);
                    onPacket(isHost ? c.PeerId : hostAlias, payload, len);
                    if (c.Dead || shutDown) return;
                }
                offset += 4 + len;
            }
            if (offset > 0)
            {
                Buffer.BlockCopy(c.Recv, offset, c.Recv, 0, c.RecvLen - offset);
                c.RecvLen -= offset;
            }
        }

        private void Flush(Conn c)
        {
            try
            {
                while (c.Outgoing.Count > 0)
                {
                    var frame = c.Outgoing.Peek();
                    int n = c.Socket.Send(frame, c.OutOffset, frame.Length - c.OutOffset, SocketFlags.None);
                    c.OutOffset += n;
                    if (c.OutOffset < frame.Length) return;
                    c.Outgoing.Dequeue();
                    c.OutOffset = 0;
                }
            }
            catch (SocketException e)
            {
                if (e.SocketErrorCode != SocketError.WouldBlock) c.Dead = true;
            }
        }

        public void Disconnect(ulong peer)
        {
            foreach (var c in conns) if (c.PeerId == peer) { Flush(c); c.Dead = true; }
        }

        public void Shutdown()
        {
            shutDown = true;
            foreach (var c in conns)
            {
                try { Flush(c); c.Socket.Shutdown(SocketShutdown.Both); } catch { }
                try { c.Socket.Close(); } catch { }
            }
            conns.Clear();
            if (pendingConnect != null) { try { pendingConnect.Socket.Close(); } catch { } pendingConnect = null; }
            if (listener != null) { try { listener.Close(); } catch { } listener = null; }
        }
    }
}
