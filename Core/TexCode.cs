using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace IpfbTool.Core
{
    internal static class T8aImageCodec
    {
        const int MagicT1 = 1113665876;
        const int MagicT4 = 1113666644;
        const int MagicT8 = 1113667668;

        const int OldT8 = 540160852;
        const int OldT4 = 875836468;
        const int OldT1 = 892679473;

        const int MaxDim = 16384;

        public static Image<Rgba32> Decode(byte[] data)
        {
            if (data.Length < 0x2C) throw new InvalidDataException("File too small");

            string magic = Encoding.ASCII.GetString(data, 0, 4);
            int bpp = GetBpp(magic);
            if (bpp == 0) throw new InvalidDataException($"Unknown magic: {magic}");

            if (IsLegacyMagic(magic))
                return DecodeLegacyLe(data);

            return DecodeCurrentBe(data, magic, bpp);
        }

        public static byte[] Encode(Image<Rgba32> img, byte[] originalHeader)
        {
            if (originalHeader == null || originalHeader.Length < 0x2C)
                throw new InvalidDataException("Header too small");

            string magic = Encoding.ASCII.GetString(originalHeader, 0, 4);

            if (IsLegacyMagic(magic))
                return EncodeLegacyLe(img, originalHeader);

            return EncodeCurrentBe(img, originalHeader);
        }

        static Image<Rgba32> DecodeCurrentBe(byte[] data, string magic, int bpp)
        {
            int width = BeBinary.ReadInt32(data, 0x14);
            int height = BeBinary.ReadInt32(data, 0x18);
            int parts = BeBinary.ReadInt32(data, 0x1C);

            ValidateSize(magic, width, height, parts, data.Length);

            var img = new Image<Rgba32>(width, height);

            if (parts <= 0)
            {
                DecodeBlockBe(data, magic, bpp, 0, 0, width, height, 0x2C, img);
                return img;
            }

            int tableBytes = checked(parts * 4);
            if (0x2C + tableBytes > data.Length)
                throw new InvalidDataException($"Bad table magic={magic} parts={parts} len={data.Length}");

            for (int i = 0; i < parts; i++)
            {
                int blockOfs = BeBinary.ReadInt32(data, 0x2C + i * 4);
                if (blockOfs < 0 || blockOfs + 0x10 > data.Length) continue;

                int x = BeBinary.ReadInt32(data, blockOfs);
                int y = BeBinary.ReadInt32(data, blockOfs + 4);
                int w = BeBinary.ReadInt32(data, blockOfs + 8);
                int h = BeBinary.ReadInt32(data, blockOfs + 12);
                if (w <= 0 || h <= 0) continue;

                DecodeBlockBe(data, magic, bpp, x, y, w, h, blockOfs + 0x10, img);
            }

            return img;
        }

        static Image<Rgba32> DecodeLegacyLe(byte[] data)
        {
            if (data.Length < 0x24) throw new InvalidDataException("Legacy file too small");

            int raw = BeBinary.ReadInt32(data, 0, true);

            int magicInt;
            bool old;

            switch (raw)
            {
                case MagicT1:
                case MagicT4:
                case MagicT8:
                    magicInt = raw;
                    old = false;
                    break;
                case OldT8:
                    magicInt = MagicT8;
                    old = true;
                    break;
                case OldT4:
                    magicInt = MagicT4;
                    old = true;
                    break;
                case OldT1:
                    magicInt = MagicT1;
                    old = true;
                    break;
                default:
                    throw new InvalidDataException($"Legacy magic mismatch: 0x{unchecked((uint)raw):X8}");
            }

            int baseOffset = old ? 32 : 36;

            int width = BeBinary.ReadInt32(data, 20, true);
            int height = BeBinary.ReadInt32(data, 24, true);
            int parts = BeBinary.ReadInt32(data, 28, true);

            ValidateSize("LEG", width, height, parts, data.Length);

            int tableBytes = checked(parts * 4);
            if (baseOffset < 0 || baseOffset + tableBytes > data.Length)
                throw new InvalidDataException($"Legacy table out of range base={baseOffset} parts={parts} len={data.Length}");

            int bpp = magicInt == MagicT8 ? 4 : 2;
            bool is1555 = magicInt == MagicT1;

            var img = new Image<Rgba32>(width, height);

            for (int i = 0; i < parts; i++)
            {
                int ofs = BeBinary.ReadInt32(data, baseOffset + i * 4, true);
                if (ofs < 0 || ofs + 16 > data.Length)
                    throw new InvalidDataException($"Legacy block ofs out of range i={i} ofs={ofs} len={data.Length}");

                int px = BeBinary.ReadInt32(data, ofs + 0, true);
                int py = BeBinary.ReadInt32(data, ofs + 4, true);
                int pw = BeBinary.ReadInt32(data, ofs + 8, true);
                int ph = BeBinary.ReadInt32(data, ofs + 12, true);

                if (pw <= 0 || ph <= 0) continue;
                if (px < 0 || py < 0) throw new InvalidDataException("Legacy block pos < 0");
                if (px >= width || py >= height) continue;

                int cw = pw;
                int ch = ph;
                if (px + cw > width) cw = width - px;
                if (py + ch > height) ch = height - py;
                if (cw <= 0 || ch <= 0) continue;

                int pitch = Align4(checked(pw * bpp));
                long start = (long)ofs + 16;
                long total = (long)pitch * ph;
                if (start < 0 || start + total > data.Length)
                    throw new InvalidDataException($"Legacy pixel out of range i={i} start={start} total={total} len={data.Length}");

                DecodeBlockLegacyLe(data, img, px, py, cw, ch, pitch, (int)start, bpp, is1555);
            }

            return img;
        }

        static byte[] EncodeCurrentBe(Image<Rgba32> img, byte[] header)
        {
            string magic = Encoding.ASCII.GetString(header, 0, 4);
            int bpp = GetBpp(magic);
            if (bpp == 0) bpp = 4;
            bool is1555 = magic is "1555" or "T1aD";

            int width = img.Width, height = img.Height;
            const int tile = 256;
            int cols = (width + tile - 1) / tile, rows = (height + tile - 1) / tile;

            var blocks = new List<(int X, int Y, int W, int H, int Pitch, int Offset)>();
            int offset = 0x2C + cols * rows * 4;

            for (int iy = 0; iy < rows; iy++)
            for (int ix = 0; ix < cols; ix++)
            {
                int x = ix * tile, y = iy * tile;
                int w = Math.Min(tile, width - x), h = Math.Min(tile, height - y);
                if (w <= 0 || h <= 0) continue;
                int pitch = bpp == 4 ? w * 4 : ((w * 2 + 3) & ~3);
                blocks.Add((x, y, w, h, pitch, offset));
                offset += 0x10 + pitch * h;
            }

            byte[] result = new byte[offset];
            Array.Copy(header, 0, result, 0, 0x2C);

            BeBinary.WriteInt32(result, 0x14, width);
            BeBinary.WriteInt32(result, 0x18, height);
            BeBinary.WriteInt32(result, 0x1C, blocks.Count);

            for (int i = 0; i < blocks.Count; i++)
                BeBinary.WriteInt32(result, 0x2C + i * 4, blocks[i].Offset);

            foreach (var b in blocks)
            {
                BeBinary.WriteInt32(result, b.Offset + 0x00, b.X);
                BeBinary.WriteInt32(result, b.Offset + 0x04, b.Y);
                BeBinary.WriteInt32(result, b.Offset + 0x08, b.W);
                BeBinary.WriteInt32(result, b.Offset + 0x0C, b.H);
                EncodeBlockBe(img, result, b.X, b.Y, b.W, b.H, b.Offset + 0x10, b.Pitch, bpp, is1555);
            }

            return result;
        }

        static byte[] EncodeLegacyLe(Image<Rgba32> img, byte[] header)
        {
            uint tagRaw = BeBinary.ReadUInt32(header, 0x00, true);
            bool old = tagRaw is (uint)OldT8 or (uint)OldT4 or (uint)OldT1;
            int baseOffset = old ? 32 : 36;

            int magicInt;
            if (tagRaw == (uint)OldT8 || tagRaw == (uint)MagicT8) magicInt = MagicT8;
            else if (tagRaw == (uint)OldT4 || tagRaw == (uint)MagicT4) magicInt = MagicT4;
            else if (tagRaw == (uint)OldT1 || tagRaw == (uint)MagicT1) magicInt = MagicT1;
            else magicInt = MagicT8;

            int bpp = magicInt == MagicT8 ? 4 : 2;
            bool is1555 = magicInt == MagicT1;

            int width = img.Width, height = img.Height;
            const int tile = 256;
            int cols = (width + tile - 1) / tile, rows = (height + tile - 1) / tile;

            var blocks = new List<(int X, int Y, int W, int H, int Pitch, int Offset)>(cols * rows);

            int dataOfs = baseOffset + cols * rows * 4;

            for (int iy = 0; iy < rows; iy++)
            for (int ix = 0; ix < cols; ix++)
            {
                int x = ix * tile, y = iy * tile;
                int w = Math.Min(tile, width - x), h = Math.Min(tile, height - y);
                if (w <= 0 || h <= 0) continue;

                int pitch = Align4(checked(w * bpp));
                blocks.Add((x, y, w, h, pitch, dataOfs));
                dataOfs += 0x10 + pitch * h;
            }

            byte[] result = new byte[dataOfs];
            Array.Copy(header, 0, result, 0, 0x2C);

            BeBinary.WriteInt32(result, 0x14, width, true);
            BeBinary.WriteInt32(result, 0x18, height, true);
            BeBinary.WriteInt32(result, 0x1C, blocks.Count, true);

            for (int i = 0; i < blocks.Count; i++)
                BeBinary.WriteInt32(result, baseOffset + i * 4, blocks[i].Offset, true);

            foreach (var b in blocks)
            {
                BeBinary.WriteInt32(result, b.Offset + 0x00, b.X, true);
                BeBinary.WriteInt32(result, b.Offset + 0x04, b.Y, true);
                BeBinary.WriteInt32(result, b.Offset + 0x08, b.W, true);
                BeBinary.WriteInt32(result, b.Offset + 0x0C, b.H, true);
                EncodeBlockLegacyLe(img, result, b.X, b.Y, b.W, b.H, b.Offset + 0x10, b.Pitch, bpp, is1555);
            }

            return result;
        }

        static void DecodeBlockBe(byte[] data, string magic, int bpp, int dx, int dy, int w, int h, int start, Image<Rgba32> img)
        {
            int pitch = bpp == 4 ? w * 4 : ((w * 2) + 3) & ~3;
            bool is1555 = magic is "1555" or "T1aD";

            for (int y = 0; y < h; y++)
            {
                int sy = dy + y;
                if ((uint)sy >= (uint)img.Height) continue;
                int rowBase = start + y * pitch;

                for (int x = 0; x < w; x++)
                {
                    int sx = dx + x;
                    if ((uint)sx >= (uint)img.Width) continue;

                    if (bpp == 4)
                    {
                        int off = rowBase + x * 4;
                        if (off + 3 >= data.Length) return;
                        img[sx, sy] = new Rgba32(data[off + 1], data[off + 2], data[off + 3], data[off]);
                    }
                    else
                    {
                        int off = rowBase + x * 2;
                        if (off + 1 >= data.Length) return;
                        ushort v = (ushort)((data[off] << 8) | data[off + 1]);
                        img[sx, sy] = is1555
                            ? new Rgba32((byte)(((v >> 10) & 0x1F) << 3), (byte)(((v >> 5) & 0x1F) << 3),
                                (byte)((v & 0x1F) << 3), (byte)(((v >> 15) & 1) * 255))
                            : new Rgba32((byte)(((v >> 8) & 0xF) * 17), (byte)(((v >> 4) & 0xF) * 17),
                                (byte)((v & 0xF) * 17), (byte)(((v >> 12) & 0xF) * 17));
                    }
                }
            }
        }

        static void DecodeBlockLegacyLe(byte[] data, Image<Rgba32> img, int px, int py, int cw, int ch, int pitch, int start, int bpp, bool is1555)
        {
            if (bpp == 4)
            {
                for (int row = 0; row < ch; row++)
                {
                    int rowBase = start + row * pitch;
                    for (int x = 0; x < cw; x++)
                    {
                        int p = rowBase + x * 4;
                        byte b = data[p + 0], g = data[p + 1], r = data[p + 2], a = data[p + 3];
                        img[px + x, py + row] = new Rgba32(r, g, b, a);
                    }
                }
            }
            else
            {
                for (int row = 0; row < ch; row++)
                {
                    int rowBase = start + row * pitch;
                    for (int x = 0; x < cw; x++)
                    {
                        int p = rowBase + x * 2;
                        ushort v = (ushort)(data[p] | (data[p + 1] << 8));
                        img[px + x, py + row] = is1555
                            ? new Rgba32((byte)(((v >> 10) & 0x1F) << 3), (byte)(((v >> 5) & 0x1F) << 3),
                                (byte)((v & 0x1F) << 3), (byte)(((v >> 15) & 1) * 255))
                            : new Rgba32((byte)(((v >> 8) & 0xF) * 17), (byte)(((v >> 4) & 0xF) * 17),
                                (byte)((v & 0xF) * 17), (byte)(((v >> 12) & 0xF) * 17));
                    }
                }
            }
        }

        static void EncodeBlockBe(Image<Rgba32> img, byte[] dst, int bx, int by, int w, int h, int start, int pitch, int bpp, bool is1555)
        {
            for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                int sx = bx + x, sy = by + y;
                if (sx >= img.Width || sy >= img.Height) continue;
                var px = img[sx, sy];

                if (bpp == 4)
                {
                    int off = start + y * pitch + x * 4;
                    dst[off] = px.A; dst[off + 1] = px.R; dst[off + 2] = px.G; dst[off + 3] = px.B;
                }
                else
                {
                    ushort v = is1555
                        ? (ushort)(((px.A > 127 ? 1 : 0) << 15) | ((px.R >> 3) << 10) | ((px.G >> 3) << 5) | (px.B >> 3))
                        : (ushort)(((px.A >> 4) << 12) | ((px.R >> 4) << 8) | ((px.G >> 4) << 4) | (px.B >> 4));
                    int off = start + y * pitch + x * 2;
                    dst[off] = (byte)(v >> 8); dst[off + 1] = (byte)v;
                }
            }
        }

        static void EncodeBlockLegacyLe(Image<Rgba32> img, byte[] dst, int bx, int by, int w, int h, int start, int pitch, int bpp, bool is1555)
        {
            for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                int sx = bx + x, sy = by + y;
                if (sx >= img.Width || sy >= img.Height) continue;
                var px = img[sx, sy];

                if (bpp == 4)
                {
                    int off = start + y * pitch + x * 4;
                    dst[off + 0] = px.B;
                    dst[off + 1] = px.G;
                    dst[off + 2] = px.R;
                    dst[off + 3] = px.A;
                }
                else
                {
                    ushort v = is1555
                        ? (ushort)(((px.A > 127 ? 1 : 0) << 15) | ((px.R >> 3) << 10) | ((px.G >> 3) << 5) | (px.B >> 3))
                        : (ushort)(((px.A >> 4) << 12) | ((px.R >> 4) << 8) | ((px.G >> 4) << 4) | (px.B >> 4));
                    int off = start + y * pitch + x * 2;
                    dst[off + 0] = (byte)v;
                    dst[off + 1] = (byte)(v >> 8);
                }
            }
        }

        static void ValidateSize(string magic, int w, int h, int parts, int len)
        {
            if (w <= 0 || h <= 0 || w > MaxDim || h > MaxDim)
                throw new InvalidDataException($"Bad size magic={magic} w={w} h={h} parts={parts} len={len}");
            if (parts < 0 || parts > 1_000_000)
                throw new InvalidDataException($"Bad parts magic={magic} parts={parts} len={len}");
        }

        static bool IsLegacyMagic(string m) =>
            m is "T32 " or "T8aB" or "T4aB" or "T1aB" or "4444" or "1555";

        static int Align4(int x) => (x + 3) & ~3;

        static int GetBpp(string m) => m switch
        {
            "T8aD" or "T8aB" or "T8aC" or "T32 " => 4,
            "T4aD" or "T4aB" or "T4aC" or "4444" or "T1aD" or "T1aB" or "T1aC" or "1555" => 2,
            _ => 0
        };
    }
}