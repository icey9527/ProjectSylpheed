using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Threading.Tasks;
using IpfbTool.Core;

namespace IpfbTool.Archive
{
    internal static class IpfbUnpack
    {
        static Manifest _manifest = null!;
        public static Manifest CurrentManifest => _manifest;

        public static void Unpack(string pakPath, string outDir)
        {
            _manifest = new Manifest();

            using var fs = new FileStream(
                pakPath, FileMode.Open, FileAccess.Read, FileShare.Read,
                1 << 20, FileOptions.RandomAccess);

            using var br = new BinaryReader(fs, Encoding.ASCII, leaveOpen: true);

            fs.Position = 4;
            uint idxCount = BeBinary.ReadUInt32(br);

            fs.Position = 0x10;
            var entries = new List<Entry>((int)idxCount);

            for (int i = 0; i < idxCount; i++)
            {
                int hash = BeBinary.ReadInt32(br);
                int offset = BeBinary.ReadInt32(br);
                int size = BeBinary.ReadInt32(br);

                if (hash == 0) break;

                entries.Add(new Entry
                {
                    Hash = unchecked((uint)hash),
                    Offset = offset,
                    Size = size
                });
            }

            string pakDir = Path.GetDirectoryName(pakPath) ?? "";
            string baseName = Path.GetFileNameWithoutExtension(pakPath);
            PackContext.CurrentOutputDir = outDir;

            var byPart = GroupByPart(entries);

            int maxPartDop = Math.Clamp(Environment.ProcessorCount / 2, 1, 8);
            var po = new ParallelOptions { MaxDegreeOfParallelism = Math.Min(maxPartDop, byPart.Count) };

            Parallel.ForEach(byPart, po, kvp =>
            {
                int partIndex = kvp.Key;
                var list = kvp.Value;

                list.Sort(static (a, b) => a.InnerOffset.CompareTo(b.InnerOffset));

                string partPath = Path.Combine(pakDir, $"{baseName}.p{partIndex:00}");

                using var partFs = new FileStream(
                    partPath, FileMode.Open, FileAccess.Read, FileShare.Read,
                    1 << 20, FileOptions.SequentialScan);

                foreach (var e in list)
                    ExtractEntryFromOpenPart(partFs, e, outDir);
            });

            _manifest.Save(Path.Combine(outDir, "list.xml"));
        }

        static Dictionary<int, List<Entry>> GroupByPart(List<Entry> entries)
        {
            var d = new Dictionary<int, List<Entry>>(capacity: 32);
            foreach (var e in entries)
            {
                uint encoded = unchecked((uint)e.Offset);
                int partIndex = (int)(encoded >> 28);
                e.InnerOffset = encoded & 0x0FFFFFFF;

                if (!d.TryGetValue(partIndex, out var list))
                    d[partIndex] = list = new List<Entry>();

                list.Add(e);
            }
            return d;
        }

        static void ExtractEntryFromOpenPart(FileStream partFs, Entry e, string outRoot)
        {
            int size = unchecked((int)(uint)e.Size);
            long innerOffset = e.InnerOffset;

            if (size < 0)
                throw new InvalidDataException("非法大小");

            if (innerOffset + size > partFs.Length)
                throw new InvalidDataException("条目越界");

            partFs.Position = innerOffset;

            byte[] raw = ArrayPool<byte>.Shared.Rent(size);
            try
            {
                ReadExactly(partFs, raw, size);

                byte[] payload = DecodePayload(raw, size);

                string name;
                if (!NameTable.TryGet(e.Hash, out name) || string.IsNullOrWhiteSpace(name))
                {
                    name = "$" + e.Hash.ToString("X8");
                    name = TryAppendPrefixExtension(name, payload);
                }
                Console.WriteLine(name);

                string outPath = Path.Combine(outRoot, name);
                string? dir = Path.GetDirectoryName(outPath);
                if (!string.IsNullOrEmpty(dir))
                    Directory.CreateDirectory(dir);

                Transformers.ProcessExtract(name, outPath, payload, _manifest);
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(raw);
            }
        }

        static byte[] DecodePayload(byte[] raw, int rawLen)
        {
            if (rawLen > 16 && raw[0] == (byte)'Z' && raw[1] == (byte)'1')
            {
                using var ms = new MemoryStream(raw, 12, rawLen - 16, writable: false);
                using var ds = new DeflateStream(ms, CompressionMode.Decompress);

                using var vb = new ValueBuffer(initialCapacity: Math.Max(4096, rawLen * 2));
                ds.CopyTo(vb);
                return vb.ToArray();
            }

            var payload = new byte[rawLen];
            Buffer.BlockCopy(raw, 0, payload, 0, rawLen);
            return payload;
        }

        static void ReadExactly(Stream s, byte[] buffer, int count)
        {
            int read = 0;
            while (read < count)
            {
                int r = s.Read(buffer, read, count - read);
                if (r <= 0) throw new EndOfStreamException();
                read += r;
            }
        }

        static string TryAppendPrefixExtension(string name, byte[] payload)
        {
            if (payload.Length < 3) return name;

            int n = Math.Min(4, payload.Length);
            Span<char> tmp = stackalloc char[4];

            int len = 0;
            for (int i = 0; i < n; i++)
            {
                byte b = payload[i];
                if (b == 0) continue;

                bool ok =
                    (b >= (byte)'0' && b <= (byte)'9') ||
                    (b >= (byte)'A' && b <= (byte)'Z') ||
                    (b >= (byte)'a' && b <= (byte)'z');

                if (!ok) return name;

                tmp[len++] = (char)b;
            }

            if (len < 3) return name;
            return name + "." + new string(tmp.Slice(0, len));
        }

        sealed class Entry
        {
            public uint Hash;
            public int Offset;
            public int Size;
            public long InnerOffset;
        }

        sealed class ValueBuffer : Stream
        {
            byte[] _buf;
            int _len;
            bool _disposed;

            public ValueBuffer(int initialCapacity)
            {
                _buf = ArrayPool<byte>.Shared.Rent(Math.Max(256, initialCapacity));
                _len = 0;
            }

            public byte[] ToArray()
            {
                var result = new byte[_len];
                Buffer.BlockCopy(_buf, 0, result, 0, _len);
                return result;
            }

            void Ensure(int more)
            {
                int need = _len + more;
                if (need <= _buf.Length) return;

                int newSize = _buf.Length;
                while (newSize < need) newSize = checked(newSize * 2);

                var nb = ArrayPool<byte>.Shared.Rent(newSize);
                Buffer.BlockCopy(_buf, 0, nb, 0, _len);
                ArrayPool<byte>.Shared.Return(_buf);
                _buf = nb;
            }

            public override void Write(byte[] buffer, int offset, int count)
            {
                if (_disposed) throw new ObjectDisposedException(nameof(ValueBuffer));
                if (count <= 0) return;

                Ensure(count);
                Buffer.BlockCopy(buffer, offset, _buf, _len, count);
                _len += count;
            }

            public override void Write(ReadOnlySpan<byte> buffer)
            {
                if (_disposed) throw new ObjectDisposedException(nameof(ValueBuffer));
                if (buffer.Length == 0) return;

                Ensure(buffer.Length);
                buffer.CopyTo(_buf.AsSpan(_len));
                _len += buffer.Length;
            }

            protected override void Dispose(bool disposing)
            {
                if (_disposed) return;
                _disposed = true;
                ArrayPool<byte>.Shared.Return(_buf);
                _buf = Array.Empty<byte>();
                base.Dispose(disposing);
            }

            public override bool CanRead => false;
            public override bool CanSeek => false;
            public override bool CanWrite => true;
            public override long Length => _len;
            public override long Position { get => _len; set => throw new NotSupportedException(); }
            public override void Flush() { }
            public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
            public override void SetLength(long value) => throw new NotSupportedException();
        }
    }
}