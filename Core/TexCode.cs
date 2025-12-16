using System;
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
            bool is1555 = magic is "1555" or "T1aD";

            if (parts <= 0)
            {
                int w = width, h = height;
                int pitch = bpp == 4 ? checked(w * 4) : checked(((w * 2) + 3) & ~3);
                int start = 0x2C;

                long need = (long)start + (long)pitch * h;
                if ((uint)start > (uint)data.Length || need > data.Length)
                    throw new InvalidDataException($"Pixel data out of range start={start} pitch={pitch} h={h} len={data.Length}");

                img.ProcessPixelRows(accessor =>
                {
                    int imgW = accessor.Width;
                    int imgH = accessor.Height;

                    int dx = 0, dy = 0;
                    int cw = w, ch = h;
                    if (dx < 0 || dy < 0) return;
                    if (dx >= imgW || dy >= imgH) return;
                    if (dx + cw > imgW) cw = imgW - dx;
                    if (dy + ch > imgH) ch = imgH - dy;
                    if (cw <= 0 || ch <= 0) return;

                    if (bpp == 4)
                    {
                        for (int y = 0; y < ch; y++)
                        {
                            Span<Rgba32> dstRow = accessor.GetRowSpan(dy + y).Slice(dx, cw);
                            ReadOnlySpan<byte> src = data.AsSpan(start + y * pitch, cw * 4);
                            for (int x = 0, p = 0; x < cw; x++, p += 4)
                                dstRow[x] = new Rgba32(src[p + 1], src[p + 2], src[p + 3], src[p + 0]);
                        }
                    }
                    else if (is1555)
                    {
                        for (int y = 0; y < ch; y++)
                        {
                            Span<Rgba32> dstRow = accessor.GetRowSpan(dy + y).Slice(dx, cw);
                            ReadOnlySpan<byte> src = data.AsSpan(start + y * pitch, cw * 2);
                            for (int x = 0, p = 0; x < cw; x++, p += 2)
                            {
                                ushort v = (ushort)((src[p] << 8) | src[p + 1]);
                                dstRow[x] = new Rgba32(
                                    (byte)(((v >> 10) & 0x1F) << 3),
                                    (byte)(((v >> 5) & 0x1F) << 3),
                                    (byte)((v & 0x1F) << 3),
                                    (byte)(((v >> 15) & 1) * 255)
                                );
                            }
                        }
                    }
                    else
                    {
                        for (int y = 0; y < ch; y++)
                        {
                            Span<Rgba32> dstRow = accessor.GetRowSpan(dy + y).Slice(dx, cw);
                            ReadOnlySpan<byte> src = data.AsSpan(start + y * pitch, cw * 2);
                            for (int x = 0, p = 0; x < cw; x++, p += 2)
                            {
                                ushort v = (ushort)((src[p] << 8) | src[p + 1]);
                                dstRow[x] = new Rgba32(
                                    (byte)(((v >> 8) & 0xF) * 17),
                                    (byte)(((v >> 4) & 0xF) * 17),
                                    (byte)((v & 0xF) * 17),
                                    (byte)(((v >> 12) & 0xF) * 17)
                                );
                            }
                        }
                    }
                });

                return img;
            }

            int tableBytes = checked(parts * 4);
            if (0x2C + tableBytes > data.Length)
                throw new InvalidDataException($"Bad table magic={magic} parts={parts} len={data.Length}");

            int[] startArr = new int[parts];
            int[] xArr = new int[parts];
            int[] yArr = new int[parts];
            int[] wArr = new int[parts];
            int[] hArr = new int[parts];
            int[] pitchArr = new int[parts];

            for (int i = 0; i < parts; i++)
            {
                int blockOfs = BeBinary.ReadInt32(data, 0x2C + i * 4);
                if ((uint)blockOfs > (uint)(data.Length - 0x10))
                {
                    startArr[i] = -1;
                    continue;
                }

                int x = BeBinary.ReadInt32(data, blockOfs + 0);
                int y = BeBinary.ReadInt32(data, blockOfs + 4);
                int w = BeBinary.ReadInt32(data, blockOfs + 8);
                int h = BeBinary.ReadInt32(data, blockOfs + 12);

                if (w <= 0 || h <= 0)
                {
                    startArr[i] = -1;
                    continue;
                }

                int pitch = bpp == 4 ? checked(w * 4) : checked(((w * 2) + 3) & ~3);
                int start = checked(blockOfs + 0x10);

                long need = (long)start + (long)pitch * h;
                if ((uint)start > (uint)data.Length || need > data.Length)
                {
                    startArr[i] = -1;
                    continue;
                }

                startArr[i] = start;
                xArr[i] = x;
                yArr[i] = y;
                wArr[i] = w;
                hArr[i] = h;
                pitchArr[i] = pitch;
            }

            img.ProcessPixelRows(accessor =>
            {
                int imgW = accessor.Width;
                int imgH = accessor.Height;

                for (int i = 0; i < parts; i++)
                {
                    int start = startArr[i];
                    if (start < 0) continue;

                    int dx = xArr[i], dy = yArr[i], w = wArr[i], h = hArr[i], pitch = pitchArr[i];

                    if (w <= 0 || h <= 0) continue;
                    if (dx < 0 || dy < 0) continue;
                    if (dx >= imgW || dy >= imgH) continue;

                    int cw = w, ch = h;
                    if (dx + cw > imgW) cw = imgW - dx;
                    if (dy + ch > imgH) ch = imgH - dy;
                    if (cw <= 0 || ch <= 0) continue;

                    if (bpp == 4)
                    {
                        for (int y = 0; y < ch; y++)
                        {
                            Span<Rgba32> dstRow = accessor.GetRowSpan(dy + y).Slice(dx, cw);
                            ReadOnlySpan<byte> src = data.AsSpan(start + y * pitch, cw * 4);
                            for (int x = 0, p = 0; x < cw; x++, p += 4)
                                dstRow[x] = new Rgba32(src[p + 1], src[p + 2], src[p + 3], src[p + 0]);
                        }
                    }
                    else if (is1555)
                    {
                        for (int y = 0; y < ch; y++)
                        {
                            Span<Rgba32> dstRow = accessor.GetRowSpan(dy + y).Slice(dx, cw);
                            ReadOnlySpan<byte> src = data.AsSpan(start + y * pitch, cw * 2);
                            for (int x = 0, p = 0; x < cw; x++, p += 2)
                            {
                                ushort v = (ushort)((src[p] << 8) | src[p + 1]);
                                dstRow[x] = new Rgba32(
                                    (byte)(((v >> 10) & 0x1F) << 3),
                                    (byte)(((v >> 5) & 0x1F) << 3),
                                    (byte)((v & 0x1F) << 3),
                                    (byte)(((v >> 15) & 1) * 255)
                                );
                            }
                        }
                    }
                    else
                    {
                        for (int y = 0; y < ch; y++)
                        {
                            Span<Rgba32> dstRow = accessor.GetRowSpan(dy + y).Slice(dx, cw);
                            ReadOnlySpan<byte> src = data.AsSpan(start + y * pitch, cw * 2);
                            for (int x = 0, p = 0; x < cw; x++, p += 2)
                            {
                                ushort v = (ushort)((src[p] << 8) | src[p + 1]);
                                dstRow[x] = new Rgba32(
                                    (byte)(((v >> 8) & 0xF) * 17),
                                    (byte)(((v >> 4) & 0xF) * 17),
                                    (byte)((v & 0xF) * 17),
                                    (byte)(((v >> 12) & 0xF) * 17)
                                );
                            }
                        }
                    }
                }
            });

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

            int[] startArr = new int[parts];
            int[] pitchArr = new int[parts];
            int[] xArr = new int[parts];
            int[] yArr = new int[parts];
            int[] wArr = new int[parts];
            int[] hArr = new int[parts];

            for (int i = 0; i < parts; i++)
            {
                int ofs = BeBinary.ReadInt32(data, baseOffset + i * 4, true);
                if ((uint)ofs > (uint)(data.Length - 16))
                {
                    startArr[i] = -1;
                    continue;
                }

                int px = BeBinary.ReadInt32(data, ofs + 0, true);
                int py = BeBinary.ReadInt32(data, ofs + 4, true);
                int pw = BeBinary.ReadInt32(data, ofs + 8, true);
                int ph = BeBinary.ReadInt32(data, ofs + 12, true);

                if (pw <= 0 || ph <= 0)
                {
                    startArr[i] = -1;
                    continue;
                }

                if (px < 0 || py < 0)
                    throw new InvalidDataException("Legacy block pos < 0");

                if (px >= width || py >= height)
                {
                    startArr[i] = -1;
                    continue;
                }

                int cw = pw;
                int ch = ph;
                if (px + cw > width) cw = width - px;
                if (py + ch > height) ch = height - py;
                if (cw <= 0 || ch <= 0)
                {
                    startArr[i] = -1;
                    continue;
                }

                int pitch = Align4(checked(pw * bpp));
                int start = checked(ofs + 16);

                long need = (long)start + (long)pitch * ph;
                if ((uint)start > (uint)data.Length || need > data.Length)
                    throw new InvalidDataException($"Legacy pixel out of range i={i} start={start} total={(long)pitch * ph} len={data.Length}");

                startArr[i] = start;
                pitchArr[i] = pitch;
                xArr[i] = px;
                yArr[i] = py;
                wArr[i] = cw;
                hArr[i] = ch;
            }

            img.ProcessPixelRows(accessor =>
            {
                int imgW = accessor.Width;
                int imgH = accessor.Height;

                for (int i = 0; i < parts; i++)
                {
                    int start = startArr[i];
                    if (start < 0) continue;

                    int px = xArr[i], py = yArr[i], cw = wArr[i], ch = hArr[i];
                    int pitch = pitchArr[i];

                    if (cw <= 0 || ch <= 0) continue;
                    if (px < 0 || py < 0) continue;
                    if (px >= imgW || py >= imgH) continue;

                    int rw = cw, rh = ch;
                    if (px + rw > imgW) rw = imgW - px;
                    if (py + rh > imgH) rh = imgH - py;
                    if (rw <= 0 || rh <= 0) continue;

                    if (bpp == 4)
                    {
                        for (int y = 0; y < rh; y++)
                        {
                            Span<Rgba32> dstRow = accessor.GetRowSpan(py + y).Slice(px, rw);
                            ReadOnlySpan<byte> src = data.AsSpan(start + y * pitch, rw * 4);
                            for (int x = 0, p = 0; x < rw; x++, p += 4)
                                dstRow[x] = new Rgba32(src[p + 2], src[p + 1], src[p + 0], src[p + 3]);
                        }
                    }
                    else if (is1555)
                    {
                        for (int y = 0; y < rh; y++)
                        {
                            Span<Rgba32> dstRow = accessor.GetRowSpan(py + y).Slice(px, rw);
                            ReadOnlySpan<byte> src = data.AsSpan(start + y * pitch, rw * 2);
                            for (int x = 0, p = 0; x < rw; x++, p += 2)
                            {
                                ushort v = (ushort)(src[p] | (src[p + 1] << 8));
                                dstRow[x] = new Rgba32(
                                    (byte)(((v >> 10) & 0x1F) << 3),
                                    (byte)(((v >> 5) & 0x1F) << 3),
                                    (byte)((v & 0x1F) << 3),
                                    (byte)(((v >> 15) & 1) * 255)
                                );
                            }
                        }
                    }
                    else
                    {
                        for (int y = 0; y < rh; y++)
                        {
                            Span<Rgba32> dstRow = accessor.GetRowSpan(py + y).Slice(px, rw);
                            ReadOnlySpan<byte> src = data.AsSpan(start + y * pitch, rw * 2);
                            for (int x = 0, p = 0; x < rw; x++, p += 2)
                            {
                                ushort v = (ushort)(src[p] | (src[p + 1] << 8));
                                dstRow[x] = new Rgba32(
                                    (byte)(((v >> 8) & 0xF) * 17),
                                    (byte)(((v >> 4) & 0xF) * 17),
                                    (byte)((v & 0xF) * 17),
                                    (byte)(((v >> 12) & 0xF) * 17)
                                );
                            }
                        }
                    }
                }
            });

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

            int parts = checked(cols * rows);
            int tableBase = 0x2C;
            int dataBase = checked(tableBase + parts * 4);

            int ofs = dataBase;
            int[] blockOfs = new int[parts];
            int[] blockPitch = new int[parts];
            int[] blockW = new int[parts];
            int[] blockH = new int[parts];

            for (int iy = 0, i = 0; iy < rows; iy++)
            for (int ix = 0; ix < cols; ix++, i++)
            {
                int bx = ix * tile, by = iy * tile;
                int w = Math.Min(tile, width - bx), h = Math.Min(tile, height - by);
                if (w <= 0 || h <= 0) { blockOfs[i] = ofs; blockPitch[i] = 0; blockW[i] = 0; blockH[i] = 0; continue; }
                int pitch = bpp == 4 ? checked(w * 4) : checked(((w * 2) + 3) & ~3);
                blockOfs[i] = ofs;
                blockPitch[i] = pitch;
                blockW[i] = w;
                blockH[i] = h;
                ofs = checked(ofs + 0x10 + pitch * h);
            }

            byte[] result = new byte[ofs];
            Buffer.BlockCopy(header, 0, result, 0, 0x2C);

            BeBinary.WriteInt32(result, 0x14, width);
            BeBinary.WriteInt32(result, 0x18, height);
            BeBinary.WriteInt32(result, 0x1C, parts);

            for (int i = 0; i < parts; i++)
                BeBinary.WriteInt32(result, tableBase + i * 4, blockOfs[i]);

            img.ProcessPixelRows(accessor =>
            {
                for (int iy = 0, i = 0; iy < rows; iy++)
                for (int ix = 0; ix < cols; ix++, i++)
                {
                    int w = blockW[i], h = blockH[i];
                    if (w <= 0 || h <= 0) continue;

                    int bx = ix * tile, by = iy * tile;
                    int bo = blockOfs[i];
                    int pitch = blockPitch[i];
                    int start = bo + 0x10;

                    BeBinary.WriteInt32(result, bo + 0x00, bx);
                    BeBinary.WriteInt32(result, bo + 0x04, by);
                    BeBinary.WriteInt32(result, bo + 0x08, w);
                    BeBinary.WriteInt32(result, bo + 0x0C, h);

                    if (bpp == 4)
                    {
                        for (int y = 0; y < h; y++)
                        {
                            ReadOnlySpan<Rgba32> srcRow = accessor.GetRowSpan(by + y).Slice(bx, w);
                            Span<byte> outRow = result.AsSpan(start + y * pitch, w * 4);
                            for (int x = 0, p = 0; x < w; x++, p += 4)
                            {
                                var px = srcRow[x];
                                outRow[p + 0] = px.A;
                                outRow[p + 1] = px.R;
                                outRow[p + 2] = px.G;
                                outRow[p + 3] = px.B;
                            }
                        }
                    }
                    else if (is1555)
                    {
                        for (int y = 0; y < h; y++)
                        {
                            ReadOnlySpan<Rgba32> srcRow = accessor.GetRowSpan(by + y).Slice(bx, w);
                            Span<byte> outRow = result.AsSpan(start + y * pitch, w * 2);
                            for (int x = 0, p = 0; x < w; x++, p += 2)
                            {
                                var px = srcRow[x];
                                ushort v = (ushort)(((px.A > 127 ? 1 : 0) << 15) | ((px.R >> 3) << 10) | ((px.G >> 3) << 5) | (px.B >> 3));
                                outRow[p + 0] = (byte)(v >> 8);
                                outRow[p + 1] = (byte)v;
                            }
                        }
                    }
                    else
                    {
                        for (int y = 0; y < h; y++)
                        {
                            ReadOnlySpan<Rgba32> srcRow = accessor.GetRowSpan(by + y).Slice(bx, w);
                            Span<byte> outRow = result.AsSpan(start + y * pitch, w * 2);
                            for (int x = 0, p = 0; x < w; x++, p += 2)
                            {
                                var px = srcRow[x];
                                ushort v = (ushort)(((px.A >> 4) << 12) | ((px.R >> 4) << 8) | ((px.G >> 4) << 4) | (px.B >> 4));
                                outRow[p + 0] = (byte)(v >> 8);
                                outRow[p + 1] = (byte)v;
                            }
                        }
                    }
                }
            });

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

            int parts = checked(cols * rows);
            int tableBase = baseOffset;
            int dataBase = checked(tableBase + parts * 4);

            int ofs = dataBase;
            int[] blockOfs = new int[parts];
            int[] blockPitch = new int[parts];
            int[] blockW = new int[parts];
            int[] blockH = new int[parts];

            for (int iy = 0, i = 0; iy < rows; iy++)
            for (int ix = 0; ix < cols; ix++, i++)
            {
                int bx = ix * tile, by = iy * tile;
                int w = Math.Min(tile, width - bx), h = Math.Min(tile, height - by);
                if (w <= 0 || h <= 0) { blockOfs[i] = ofs; blockPitch[i] = 0; blockW[i] = 0; blockH[i] = 0; continue; }
                int pitch = Align4(checked(w * bpp));
                blockOfs[i] = ofs;
                blockPitch[i] = pitch;
                blockW[i] = w;
                blockH[i] = h;
                ofs = checked(ofs + 0x10 + pitch * h);
            }

            byte[] result = new byte[ofs];
            Buffer.BlockCopy(header, 0, result, 0, 0x2C);

            BeBinary.WriteInt32(result, 0x14, width, true);
            BeBinary.WriteInt32(result, 0x18, height, true);
            BeBinary.WriteInt32(result, 0x1C, parts, true);

            for (int i = 0; i < parts; i++)
                BeBinary.WriteInt32(result, tableBase + i * 4, blockOfs[i], true);

            img.ProcessPixelRows(accessor =>
            {
                for (int iy = 0, i = 0; iy < rows; iy++)
                for (int ix = 0; ix < cols; ix++, i++)
                {
                    int w = blockW[i], h = blockH[i];
                    if (w <= 0 || h <= 0) continue;

                    int bx = ix * tile, by = iy * tile;
                    int bo = blockOfs[i];
                    int pitch = blockPitch[i];
                    int start = bo + 0x10;

                    BeBinary.WriteInt32(result, bo + 0x00, bx, true);
                    BeBinary.WriteInt32(result, bo + 0x04, by, true);
                    BeBinary.WriteInt32(result, bo + 0x08, w, true);
                    BeBinary.WriteInt32(result, bo + 0x0C, h, true);

                    if (bpp == 4)
                    {
                        for (int y = 0; y < h; y++)
                        {
                            ReadOnlySpan<Rgba32> srcRow = accessor.GetRowSpan(by + y).Slice(bx, w);
                            Span<byte> outRow = result.AsSpan(start + y * pitch, w * 4);
                            for (int x = 0, p = 0; x < w; x++, p += 4)
                            {
                                var px = srcRow[x];
                                outRow[p + 0] = px.B;
                                outRow[p + 1] = px.G;
                                outRow[p + 2] = px.R;
                                outRow[p + 3] = px.A;
                            }
                        }
                    }
                    else if (is1555)
                    {
                        for (int y = 0; y < h; y++)
                        {
                            ReadOnlySpan<Rgba32> srcRow = accessor.GetRowSpan(by + y).Slice(bx, w);
                            Span<byte> outRow = result.AsSpan(start + y * pitch, w * 2);
                            for (int x = 0, p = 0; x < w; x++, p += 2)
                            {
                                var px = srcRow[x];
                                ushort v = (ushort)(((px.A > 127 ? 1 : 0) << 15) | ((px.R >> 3) << 10) | ((px.G >> 3) << 5) | (px.B >> 3));
                                outRow[p + 0] = (byte)v;
                                outRow[p + 1] = (byte)(v >> 8);
                            }
                        }
                    }
                    else
                    {
                        for (int y = 0; y < h; y++)
                        {
                            ReadOnlySpan<Rgba32> srcRow = accessor.GetRowSpan(by + y).Slice(bx, w);
                            Span<byte> outRow = result.AsSpan(start + y * pitch, w * 2);
                            for (int x = 0, p = 0; x < w; x++, p += 2)
                            {
                                var px = srcRow[x];
                                ushort v = (ushort)(((px.A >> 4) << 12) | ((px.R >> 4) << 8) | ((px.G >> 4) << 4) | (px.B >> 4));
                                outRow[p + 0] = (byte)v;
                                outRow[p + 1] = (byte)(v >> 8);
                            }
                        }
                    }
                }
            });

            return result;
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