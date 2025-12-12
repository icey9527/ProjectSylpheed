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
        public static Image<Rgba32> Decode(byte[] data)
        {
            if (data.Length < 0x2C) throw new InvalidDataException("File too small");

            string magic = Encoding.ASCII.GetString(data, 0, 4);
            int bpp = GetBpp(magic);
            if (bpp == 0) throw new InvalidDataException($"Unknown magic: {magic}");

            int width = BeBinary.ReadInt32(data, 0x14);
            int height = BeBinary.ReadInt32(data, 0x18);
            int parts = BeBinary.ReadInt32(data, 0x1C);

            var img = new Image<Rgba32>(width, height);

            if (parts <= 0)
                DecodeBlock(data, magic, bpp, 0, 0, width, height, 0x2C, img);
            else
            {
                for (int i = 0; i < parts; i++)
                {
                    int blockOfs = BeBinary.ReadInt32(data, 0x2C + i * 4);
                    if (blockOfs < 0 || blockOfs + 0x10 > data.Length) continue;

                    int x = BeBinary.ReadInt32(data, blockOfs);
                    int y = BeBinary.ReadInt32(data, blockOfs + 4);
                    int w = BeBinary.ReadInt32(data, blockOfs + 8);
                    int h = BeBinary.ReadInt32(data, blockOfs + 12);
                    if (w <= 0 || h <= 0) continue;

                    DecodeBlock(data, magic, bpp, x, y, w, h, blockOfs + 0x10, img);
                }
            }

            return img;
        }

        public static byte[] Encode(Image<Rgba32> img, byte[] originalHeader)
        {
            string magic = Encoding.ASCII.GetString(originalHeader, 0, 4);
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
            Array.Copy(originalHeader, 0, result, 0, Math.Min(0x2C, originalHeader.Length));
            BeBinary.WriteInt32(result, 0x14, width);
            BeBinary.WriteInt32(result, 0x18, height);
            BeBinary.WriteInt32(result, 0x1C, blocks.Count);

            for (int i = 0; i < blocks.Count; i++)
                BeBinary.WriteInt32(result, 0x2C + i * 4, blocks[i].Offset);

            foreach (var b in blocks)
            {
                BeBinary.WriteInt32(result, b.Offset, b.X);
                BeBinary.WriteInt32(result, b.Offset + 4, b.Y);
                BeBinary.WriteInt32(result, b.Offset + 8, b.W);
                BeBinary.WriteInt32(result, b.Offset + 12, b.H);
                EncodeBlock(img, result, b.X, b.Y, b.W, b.H, b.Offset + 0x10, b.Pitch, bpp, is1555);
            }

            return result;
        }

        static void DecodeBlock(byte[] data, string magic, int bpp, int dx, int dy, int w, int h, int start, Image<Rgba32> img)
        {
            int pitch = bpp == 4 ? w * 4 : ((w * 2) + 3) & ~3;
            bool is1555 = magic is "1555" or "T1aD";

            for (int y = 0; y < h; y++)
            {
                int sy = dy + y;
                if (sy < 0 || sy >= img.Height) continue;
                int rowBase = start + y * pitch;

                for (int x = 0; x < w; x++)
                {
                    int sx = dx + x;
                    if (sx < 0 || sx >= img.Width) continue;

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

        static void EncodeBlock(Image<Rgba32> img, byte[] dst, int bx, int by, int w, int h,
            int start, int pitch, int bpp, bool is1555)
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

        static int GetBpp(string m) => m switch
        {
            "T8aD" or "T8aB" or "T8aC" or "T32 " => 4,
            "T4aD" or "T4aB" or "T4aC" or "4444" or "T1aD" or "T1aB" or "T1aC" or "1555" => 2,
            _ => 0
        };
    }
}