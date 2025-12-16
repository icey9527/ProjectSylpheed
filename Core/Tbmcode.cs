using System;
using System.IO;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace IpfbTool.Core
{
    internal static class TbmImageCodec
    {
        internal readonly struct Header
        {
            public readonly uint Tag, Status, Priority, NumTextures, ColorBits, NumPalettes, Placement;
            public readonly int PosX, PosY, W, H, StdW, StdH;

            public Header(uint tag, uint status, uint priority, int posX, int posY, int w, int h,
                uint numTextures, uint colorBits, uint numPalettes, uint placement, int stdW, int stdH)
            {
                Tag = tag; Status = status; Priority = priority;
                PosX = posX; PosY = posY; W = w; H = h;
                NumTextures = numTextures; ColorBits = colorBits; NumPalettes = numPalettes;
                Placement = placement; StdW = stdW; StdH = stdH;
            }
        }

        public static Header ReadHeader(byte[] d)
        {
            uint tag = TextureUtil.ReadU32BE(d, 0x00);
            uint status = TextureUtil.ReadU32BE(d, 0x04);
            uint priority = TextureUtil.ReadU32BE(d, 0x08);
            int posX = TextureUtil.ReadI32BE(d, 0x0C);
            int posY = TextureUtil.ReadI32BE(d, 0x10);
            int w = TextureUtil.ReadI32BE(d, 0x14);
            int h = TextureUtil.ReadI32BE(d, 0x18);
            uint numTextures = TextureUtil.ReadU32BE(d, 0x1C);
            uint colorBits = TextureUtil.ReadU32BE(d, 0x20);
            uint numPalettes = TextureUtil.ReadU32BE(d, 0x24);
            uint placement = TextureUtil.ReadU32BE(d, 0x28);
            int stdW = TextureUtil.ReadI32BE(d, 0x2C);
            int stdH = TextureUtil.ReadI32BE(d, 0x30);

            return new Header(tag, status, priority, posX, posY, w, h, numTextures, colorBits, numPalettes, placement, stdW, stdH);
        }

        public static Image<Rgba32> Decode(byte[] d, Header h)
        {
            int bpp = h.ColorBits switch
            {
                24 => 3,
                32 => 4,
                _ => throw new InvalidDataException($"TBM: unsupported color_bits {h.ColorBits}")
            };

            int nt = checked((int)h.NumTextures);
            if (d.Length < 0x34 + nt * 4)
                throw new InvalidDataException("TBM: truncated");

            int pos = 0x34;

            int[] offsets = new int[nt];
            for (int i = 0; i < nt; i++)
            {
                offsets[i] = unchecked((int)TextureUtil.ReadU32BE(d, pos));
                pos += 4;
            }

            pos = checked(pos + checked((int)h.NumPalettes) * 4);

            var img = new Image<Rgba32>(h.W, h.H);

            int[] sxArr = new int[nt];
            int[] syArr = new int[nt];
            int[] dxArr = new int[nt];
            int[] dyArr = new int[nt];
            int[] wArr = new int[nt];
            int[] hArr = new int[nt];
            int[] pitchArr = new int[nt];
            int[] startArr = new int[nt];

            int imgW = img.Width, imgH = img.Height;

            for (int i = 0; i < nt; i++)
            {
                int ofs = offsets[i];
                if ((uint)ofs > (uint)(d.Length - 0x10)) { startArr[i] = -1; continue; }

                int ptX = TextureUtil.ReadI32BE(d, ofs + 0x00);
                int ptY = TextureUtil.ReadI32BE(d, ofs + 0x04);
                int w = TextureUtil.ReadI32BE(d, ofs + 0x08);
                int hh = TextureUtil.ReadI32BE(d, ofs + 0x0C);
                if (w <= 0 || hh <= 0) { startArr[i] = -1; continue; }

                int pitch = ((checked(w * bpp)) + 3) & ~3;
                int pixBase = checked(ofs + 0x10);

                long need = (long)pixBase + (long)pitch * hh;
                if ((uint)pixBase > (uint)d.Length || need > d.Length) { startArr[i] = -1; continue; }

                int dx = ptX, dy = ptY;
                int sx = 0, sy = 0;
                int cw = w, ch = hh;

                if (dx < 0) { sx = -dx; cw += dx; dx = 0; }
                if (dy < 0) { sy = -dy; ch += dy; dy = 0; }

                if (dx >= imgW || dy >= imgH) { startArr[i] = -1; continue; }

                if (dx + cw > imgW) cw = imgW - dx;
                if (dy + ch > imgH) ch = imgH - dy;
                if (cw <= 0 || ch <= 0) { startArr[i] = -1; continue; }

                sxArr[i] = sx;
                syArr[i] = sy;
                dxArr[i] = dx;
                dyArr[i] = dy;
                wArr[i] = cw;
                hArr[i] = ch;
                pitchArr[i] = pitch;
                startArr[i] = pixBase;
            }

            img.ProcessPixelRows(accessor =>
            {
                for (int i = 0; i < nt; i++)
                {
                    int start = startArr[i];
                    if (start < 0) continue;

                    int sx = sxArr[i], sy = syArr[i];
                    int dx = dxArr[i], dy = dyArr[i];
                    int cw = wArr[i], ch = hArr[i];
                    int pitch = pitchArr[i];

                    if (bpp == 3)
                    {
                        for (int y = 0; y < ch; y++)
                        {
                            Span<Rgba32> dst = accessor.GetRowSpan(dy + y).Slice(dx, cw);
                            int rowBase = start + (sy + y) * pitch + sx * 3;
                            ReadOnlySpan<byte> src = d.AsSpan(rowBase, cw * 3);

                            for (int x = 0, p = 0; x < cw; x++, p += 3)
                                dst[x] = new Rgba32(src[p + 2], src[p + 1], src[p + 0], 255);
                        }
                    }
                    else
                    {
                        for (int y = 0; y < ch; y++)
                        {
                            Span<Rgba32> dst = accessor.GetRowSpan(dy + y).Slice(dx, cw);
                            int rowBase = start + (sy + y) * pitch + sx * 4;
                            ReadOnlySpan<byte> src = d.AsSpan(rowBase, cw * 4);

                            for (int x = 0, p = 0; x < cw; x++, p += 4)
                                dst[x] = new Rgba32(src[p + 2], src[p + 1], src[p + 0], src[p + 3]);
                        }
                    }
                }
            });

            return img;
        }

        public static byte[] Encode(Image<Rgba32> img, uint tag, uint status, uint priority, int posX, int posY,
            uint placement, int stdW, int stdH, uint colorBits, uint numPalettes)
        {
            int bpp = colorBits switch
            {
                24 => 3,
                32 => 4,
                _ => throw new InvalidDataException($"TBM: unsupported color_bits {colorBits}")
            };

            int width = img.Width, height = img.Height;
            const int tile = 256;
            int cols = (width + tile - 1) / tile, rows = (height + tile - 1) / tile;

            int parts = checked(cols * rows);

            int headerSize = 0x34;
            int offsetTableSize = checked(parts * 4);
            int paletteTableSize = checked(unchecked((int)numPalettes) * 4);
            int dataBase = checked(headerSize + offsetTableSize + paletteTableSize);

            int[] blockOfs = new int[parts];
            int[] blockPitch = new int[parts];
            int[] blockW = new int[parts];
            int[] blockH = new int[parts];

            int ofs = dataBase;

            for (int iy = 0, i = 0; iy < rows; iy++)
            for (int ix = 0; ix < cols; ix++, i++)
            {
                int bx = ix * tile, by = iy * tile;
                int w = Math.Min(tile, width - bx), h = Math.Min(tile, height - by);
                if (w <= 0 || h <= 0) { blockOfs[i] = ofs; blockPitch[i] = 0; blockW[i] = 0; blockH[i] = 0; continue; }

                int pitch = ((checked(w * bpp)) + 3) & ~3;
                blockOfs[i] = ofs;
                blockPitch[i] = pitch;
                blockW[i] = w;
                blockH[i] = h;
                ofs = checked(ofs + 0x10 + pitch * h);
            }

            byte[] result = new byte[ofs];

            TextureUtil.WriteU32BE(result, 0x00, tag);
            TextureUtil.WriteU32BE(result, 0x04, status);
            TextureUtil.WriteU32BE(result, 0x08, priority);
            TextureUtil.WriteI32BE(result, 0x0C, posX);
            TextureUtil.WriteI32BE(result, 0x10, posY);
            TextureUtil.WriteI32BE(result, 0x14, width);
            TextureUtil.WriteI32BE(result, 0x18, height);
            TextureUtil.WriteU32BE(result, 0x1C, unchecked((uint)parts));
            TextureUtil.WriteU32BE(result, 0x20, colorBits);
            TextureUtil.WriteU32BE(result, 0x24, numPalettes);
            TextureUtil.WriteU32BE(result, 0x28, placement);
            TextureUtil.WriteI32BE(result, 0x2C, stdW);
            TextureUtil.WriteI32BE(result, 0x30, stdH);

            int pos = 0x34;
            for (int i = 0; i < parts; i++)
            {
                TextureUtil.WriteU32BE(result, pos, unchecked((uint)blockOfs[i]));
                pos += 4;
            }

            for (int i = 0; i < paletteTableSize; i++)
                result[headerSize + offsetTableSize + i] = 0;

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

                    TextureUtil.WriteI32BE(result, bo + 0x00, bx);
                    TextureUtil.WriteI32BE(result, bo + 0x04, by);
                    TextureUtil.WriteI32BE(result, bo + 0x08, w);
                    TextureUtil.WriteI32BE(result, bo + 0x0C, h);

                    int basePix = bo + 0x10;

                    if (bpp == 3)
                    {
                        for (int y = 0; y < h; y++)
                        {
                            ReadOnlySpan<Rgba32> srcRow = accessor.GetRowSpan(by + y).Slice(bx, w);
                            Span<byte> outRow = result.AsSpan(basePix + y * pitch, w * 3);
                            for (int x = 0, p = 0; x < w; x++, p += 3)
                            {
                                var px = srcRow[x];
                                outRow[p + 0] = px.B;
                                outRow[p + 1] = px.G;
                                outRow[p + 2] = px.R;
                            }
                        }
                    }
                    else
                    {
                        for (int y = 0; y < h; y++)
                        {
                            ReadOnlySpan<Rgba32> srcRow = accessor.GetRowSpan(by + y).Slice(bx, w);
                            Span<byte> outRow = result.AsSpan(basePix + y * pitch, w * 4);
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
                }
            });

            return result;
        }
    }
}