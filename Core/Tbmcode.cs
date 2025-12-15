using System;
using System.Collections.Generic;
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

            if (d.Length < 0x34 + (int)h.NumTextures * 4)
                throw new InvalidDataException("TBM: truncated");

            int pos = 0x34;

            var offsets = new int[h.NumTextures];
            for (int i = 0; i < offsets.Length; i++)
            {
                offsets[i] = unchecked((int)TextureUtil.ReadU32BE(d, pos));
                pos += 4;
            }

            pos += unchecked((int)h.NumPalettes) * 4;

            var img = new Image<Rgba32>(h.W, h.H);

            img.ProcessPixelRows(accessor =>
            {
                for (int i = 0; i < offsets.Length; i++)
                {
                    int ofs = offsets[i];
                    if (ofs < 0 || ofs + 0x10 > d.Length) continue;

                    int ptX = TextureUtil.ReadI32BE(d, ofs + 0x00);
                    int ptY = TextureUtil.ReadI32BE(d, ofs + 0x04);
                    int w = TextureUtil.ReadI32BE(d, ofs + 0x08);
                    int hh = TextureUtil.ReadI32BE(d, ofs + 0x0C);
                    if (w <= 0 || hh <= 0) continue;

                    int pitch = ((w * bpp) + 3) & ~3;
                    int pixBase = ofs + 0x10;

                    for (int y = 0; y < hh; y++)
                    {
                        int dy = ptY + y;
                        if ((uint)dy >= (uint)img.Height) continue;

                        int rowBase = pixBase + y * pitch;
                        if (rowBase < 0 || rowBase >= d.Length) break;

                        var row = accessor.GetRowSpan(dy);
                        int maxX = Math.Min(w, img.Width - ptX);
                        if (maxX <= 0) continue;

                        int x0 = ptX;
                        int base0 = rowBase;

                        if (x0 < 0)
                        {
                            int skip = -x0;
                            if (skip >= w) continue;
                            base0 += skip * bpp;
                            maxX = Math.Min(w - skip, img.Width);
                            x0 = 0;
                        }

                        var dst = row.Slice(x0, maxX);

                        if (bpp == 3)
                        {
                            for (int x = 0; x < maxX; x++)
                            {
                                int p = base0 + x * 3;
                                if (p + 2 >= d.Length) break;
                                byte b = d[p], g = d[p + 1], r = d[p + 2];
                                dst[x] = new Rgba32(r, g, b, 255);
                            }
                        }
                        else
                        {
                            for (int x = 0; x < maxX; x++)
                            {
                                int p = base0 + x * 4;
                                if (p + 3 >= d.Length) break;
                                byte b = d[p], g = d[p + 1], r = d[p + 2], a = d[p + 3];
                                dst[x] = new Rgba32(r, g, b, a);
                            }
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

            var blocks = new List<(int X, int Y, int W, int H, int Pitch, int Offset)>(cols * rows);

            int headerSize = 0x34;
            int offsetTableSize = cols * rows * 4;
            int paletteTableSize = unchecked((int)numPalettes) * 4;

            int ofs = headerSize + offsetTableSize + paletteTableSize;

            for (int iy = 0; iy < rows; iy++)
            for (int ix = 0; ix < cols; ix++)
            {
                int x = ix * tile, y = iy * tile;
                int w = Math.Min(tile, width - x), h = Math.Min(tile, height - y);
                if (w <= 0 || h <= 0) continue;

                int pitch = ((w * bpp) + 3) & ~3;
                blocks.Add((x, y, w, h, pitch, ofs));
                ofs += 0x10 + pitch * h;
            }

            var result = new byte[ofs];

            TextureUtil.WriteU32BE(result, 0x00, tag);
            TextureUtil.WriteU32BE(result, 0x04, status);
            TextureUtil.WriteU32BE(result, 0x08, priority);
            TextureUtil.WriteI32BE(result, 0x0C, posX);
            TextureUtil.WriteI32BE(result, 0x10, posY);
            TextureUtil.WriteI32BE(result, 0x14, width);
            TextureUtil.WriteI32BE(result, 0x18, height);
            TextureUtil.WriteU32BE(result, 0x1C, (uint)blocks.Count);
            TextureUtil.WriteU32BE(result, 0x20, colorBits);
            TextureUtil.WriteU32BE(result, 0x24, numPalettes);
            TextureUtil.WriteU32BE(result, 0x28, placement);
            TextureUtil.WriteI32BE(result, 0x2C, stdW);
            TextureUtil.WriteI32BE(result, 0x30, stdH);

            int pos = 0x34;
            for (int i = 0; i < blocks.Count; i++)
            {
                TextureUtil.WriteU32BE(result, pos, unchecked((uint)blocks[i].Offset));
                pos += 4;
            }

            img.ProcessPixelRows(accessor =>
            {
                foreach (var b in blocks)
                {
                    TextureUtil.WriteI32BE(result, b.Offset + 0x00, b.X);
                    TextureUtil.WriteI32BE(result, b.Offset + 0x04, b.Y);
                    TextureUtil.WriteI32BE(result, b.Offset + 0x08, b.W);
                    TextureUtil.WriteI32BE(result, b.Offset + 0x0C, b.H);

                    int basePix = b.Offset + 0x10;

                    for (int y = 0; y < b.H; y++)
                    {
                        var srcRow = accessor.GetRowSpan(b.Y + y).Slice(b.X, b.W);
                        int rowBase = basePix + y * b.Pitch;

                        if (bpp == 3)
                        {
                            for (int x = 0; x < b.W; x++)
                            {
                                int p = rowBase + x * 3;
                                var px = srcRow[x];
                                result[p + 0] = px.B;
                                result[p + 1] = px.G;
                                result[p + 2] = px.R;
                            }
                        }
                        else
                        {
                            for (int x = 0; x < b.W; x++)
                            {
                                int p = rowBase + x * 4;
                                var px = srcRow[x];
                                result[p + 0] = px.B;
                                result[p + 1] = px.G;
                                result[p + 2] = px.R;
                                result[p + 3] = px.A;
                            }
                        }
                    }
                }
            });

            return result;
        }
    }
}