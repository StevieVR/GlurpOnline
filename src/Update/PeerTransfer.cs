using System;
using GlurpOnline.Net;
using UnityEngine;

namespace GlurpOnline.Update
{
    internal sealed class PeerTransfer
    {
        private const int ChunkSize = 32 * 1024;
        public const float Timeout = 20f;

        private byte[] buffer;
        private int received;
        public float LastActivity { get; private set; } = Time.realtimeSinceStartup;
        public string SourceName;

        public static void Send(Session session, ulong peer)
        {
            var bytes = Updater.Own?.Bytes;
            if (bytes == null) return;
            for (int offset = 0; offset < bytes.Length; offset += ChunkSize)
            {
                var w = new PacketWriter(Msg.UpdateData, session.LocalId);
                w.Write(bytes.Length);
                w.Write(offset);
                w.WriteBytes(bytes, offset, Math.Min(ChunkSize, bytes.Length - offset));
                session.Send(peer, w);
            }
            Plugin.Log.LogInfo($"Sent Glurp Online {Plugin.Version} to {peer}");
        }

        public bool Receive(PacketReader r, out UpdatePackage package)
        {
            package = null;
            LastActivity = Time.realtimeSinceStartup;
            int total = r.ReadInt();
            int offset = r.ReadInt();
            var chunk = r.ReadBytes();
            if (total <= 0 || total > UpdatePackage.MaxSize || (buffer != null && buffer.Length != total)
                || offset != received || offset + chunk.Length > total)
            {
                return true;
            }
            if (buffer == null) buffer = new byte[total];
            Buffer.BlockCopy(chunk, 0, buffer, offset, chunk.Length);
            received += chunk.Length;
            if (received < total) return false;
            package = UpdatePackage.Parse(buffer);
            return true;
        }
    }
}
