using System;
using System.IO;

namespace IpfbTool.Core
{
    internal static class BeBinary
    {
        public static int ReadInt32(BinaryReader br)
        {
            Span<byte> b = stackalloc byte[4];
            if (br.Read(b) != 4) throw new EndOfStreamException();
            return (b[0] << 24) | (b[1] << 16) | (b[2] << 8) | b[3];
        }

        public static uint ReadUInt32(BinaryReader br)
        {
            Span<byte> b = stackalloc byte[4];
            if (br.Read(b) != 4) throw new EndOfStreamException();
            return (uint)((b[0] << 24) | (b[1] << 16) | (b[2] << 8) | b[3]);
        }

        public static int ReadInt32(byte[] d, int o) =>
            (d[o] << 24) | (d[o + 1] << 16) | (d[o + 2] << 8) | d[o + 3];

        public static void WriteInt32(BinaryWriter bw, int v)
        {
            bw.Write((byte)(v >> 24)); bw.Write((byte)(v >> 16));
            bw.Write((byte)(v >> 8)); bw.Write((byte)v);
        }

        public static void WriteUInt32(BinaryWriter bw, uint v)
        {
            bw.Write((byte)(v >> 24)); bw.Write((byte)(v >> 16));
            bw.Write((byte)(v >> 8)); bw.Write((byte)v);
        }

        public static void WriteInt32(byte[] d, int o, int v)
        {
            d[o] = (byte)(v >> 24); d[o + 1] = (byte)(v >> 16);
            d[o + 2] = (byte)(v >> 8); d[o + 3] = (byte)v;
        }

        public static void WriteInt32(Stream s, int v)
        {
            s.WriteByte((byte)(v >> 24)); s.WriteByte((byte)(v >> 16));
            s.WriteByte((byte)(v >> 8)); s.WriteByte((byte)v);
        }

        // -------- 新增：可选小端（默认仍是大端）--------

        public static int ReadInt32(BinaryReader br, bool le = false)
        {
            if (!le) return ReadInt32(br);
            Span<byte> b = stackalloc byte[4];
            if (br.Read(b) != 4) throw new EndOfStreamException();
            return b[0] | (b[1] << 8) | (b[2] << 16) | (b[3] << 24);
        }

        public static uint ReadUInt32(BinaryReader br, bool le = false)
        {
            if (!le) return ReadUInt32(br);
            Span<byte> b = stackalloc byte[4];
            if (br.Read(b) != 4) throw new EndOfStreamException();
            return (uint)(b[0] | (b[1] << 8) | (b[2] << 16) | (b[3] << 24));
        }

        public static int ReadInt32(byte[] d, int o, bool le = false)
        {
            if (!le) return ReadInt32(d, o);
            return d[o] | (d[o + 1] << 8) | (d[o + 2] << 16) | (d[o + 3] << 24);
        }

        public static uint ReadUInt32(byte[] d, int o, bool le = false)
        {
            if (!le) return unchecked((uint)ReadInt32(d, o));
            return (uint)(d[o] | (d[o + 1] << 8) | (d[o + 2] << 16) | (d[o + 3] << 24));
        }

        public static void WriteInt32(BinaryWriter bw, int v, bool le = false)
        {
            if (!le) { WriteInt32(bw, v); return; }
            bw.Write((byte)v); bw.Write((byte)(v >> 8));
            bw.Write((byte)(v >> 16)); bw.Write((byte)(v >> 24));
        }

        public static void WriteUInt32(BinaryWriter bw, uint v, bool le = false) =>
            WriteInt32(bw, unchecked((int)v), le);

        public static void WriteInt32(byte[] d, int o, int v, bool le = false)
        {
            if (!le) { WriteInt32(d, o, v); return; }
            d[o] = (byte)v;
            d[o + 1] = (byte)(v >> 8);
            d[o + 2] = (byte)(v >> 16);
            d[o + 3] = (byte)(v >> 24);
        }

        public static void WriteUInt32(byte[] d, int o, uint v, bool le = false) =>
            WriteInt32(d, o, unchecked((int)v), le);

        public static void WriteInt32(Stream s, int v, bool le = false)
        {
            if (!le) { WriteInt32(s, v); return; }
            s.WriteByte((byte)v);
            s.WriteByte((byte)(v >> 8));
            s.WriteByte((byte)(v >> 16));
            s.WriteByte((byte)(v >> 24));
        }
    }
}