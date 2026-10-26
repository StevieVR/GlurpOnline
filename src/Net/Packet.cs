using System;
using System.IO;
using System.IO.Compression;
using System.Text;

namespace GlurpOnline.Net
{
    internal sealed class PacketWriter
    {
        private byte[] buffer = new byte[256];
        private int length;

        public int Length => length;
        public byte[] Buffer => buffer;

        public PacketWriter() { }

        public PacketWriter(Msg type, ulong origin)
        {
            Write((byte)type);
            Write(origin);
        }

        private void Ensure(int extra)
        {
            if (length + extra <= buffer.Length) return;
            int size = buffer.Length;
            while (size < length + extra) size *= 2;
            Array.Resize(ref buffer, size);
        }

        public void Write(byte v) { Ensure(1); buffer[length++] = v; }
        public void Write(bool v) => Write(v ? (byte)1 : (byte)0);
        public void Write(short v) { Ensure(2); buffer[length++] = (byte)v; buffer[length++] = (byte)(v >> 8); }
        public void Write(ushort v) => Write((short)v);
        public void Write(int v)
        {
            Ensure(4);
            buffer[length++] = (byte)v; buffer[length++] = (byte)(v >> 8);
            buffer[length++] = (byte)(v >> 16); buffer[length++] = (byte)(v >> 24);
        }
        public void Write(uint v) => Write((int)v);
        public void Write(long v) { Write((int)v); Write((int)(v >> 32)); }
        public void Write(ulong v) => Write((long)v);
        public void Write(float v) => Write(BitConverter.ToInt32(BitConverter.GetBytes(v), 0));

        public void Write(string v)
        {
            if (v == null) { Write((short)-1); return; }
            var bytes = Encoding.UTF8.GetBytes(v);
            Write((short)bytes.Length);
            WriteRaw(bytes, 0, bytes.Length);
        }

        public void WriteBytes(byte[] data) => WriteBytes(data, 0, data?.Length ?? 0);

        public void WriteBytes(byte[] data, int offset, int count)
        {
            Write(count);
            WriteRaw(data, offset, count);
        }

        public void WriteRaw(byte[] data, int offset, int count)
        {
            if (count <= 0) return;
            Ensure(count);
            System.Buffer.BlockCopy(data, offset, buffer, length, count);
            length += count;
        }

        public byte[] ToArray()
        {
            var result = new byte[length];
            System.Buffer.BlockCopy(buffer, 0, result, 0, length);
            return result;
        }
    }

    internal sealed class PacketReader
    {
        private readonly byte[] data;
        private readonly int end;
        private int pos;

        public PacketReader(byte[] data, int offset, int count)
        {
            this.data = data;
            pos = offset;
            end = offset + count;
        }

        public int Remaining => end - pos;

        private void Need(int n)
        {
            if (pos + n > end) throw new EndOfStreamException("packet truncated");
        }

        public byte ReadByte() { Need(1); return data[pos++]; }
        public bool ReadBool() => ReadByte() != 0;
        public short ReadShort() { Need(2); short v = (short)(data[pos] | (data[pos + 1] << 8)); pos += 2; return v; }
        public int ReadInt()
        {
            Need(4);
            int v = data[pos] | (data[pos + 1] << 8) | (data[pos + 2] << 16) | (data[pos + 3] << 24);
            pos += 4;
            return v;
        }
        public long ReadLong() { uint lo = (uint)ReadInt(); uint hi = (uint)ReadInt(); return (long)(((ulong)hi << 32) | lo); }
        public ulong ReadULong() => (ulong)ReadLong();
        public float ReadFloat() => BitConverter.ToSingle(BitConverter.GetBytes(ReadInt()), 0);

        public string ReadString()
        {
            short len = ReadShort();
            if (len < 0) return null;
            Need(len);
            var s = Encoding.UTF8.GetString(data, pos, len);
            pos += len;
            return s;
        }

        public byte[] ReadBytes()
        {
            int len = ReadInt();
            if (len < 0) throw new InvalidDataException("negative length");
            Need(len);
            var result = new byte[len];
            System.Buffer.BlockCopy(data, pos, result, 0, len);
            pos += len;
            return result;
        }
    }

    internal static class Compression
    {
        public static byte[] Deflate(byte[] data, int offset, int count)
        {
            using (var ms = new MemoryStream())
            {
                using (var ds = new DeflateStream(ms, CompressionMode.Compress, true))
                {
                    ds.Write(data, offset, count);
                }
                return ms.ToArray();
            }
        }

        public static byte[] Inflate(byte[] data)
        {
            using (var input = new MemoryStream(data))
            using (var ds = new DeflateStream(input, CompressionMode.Decompress))
            using (var output = new MemoryStream())
            {
                var buf = new byte[8192];
                int n;
                while ((n = ds.Read(buf, 0, buf.Length)) > 0) output.Write(buf, 0, n);
                return output.ToArray();
            }
        }
    }
}
