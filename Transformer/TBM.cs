using System;
using System.Collections.Generic;
using System.IO;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace IpfbTool.Core
{
    internal sealed class TBM : ITransformer
    {
        static readonly HashSet<string> SupportedExts = new(StringComparer.OrdinalIgnoreCase)
        {
            ".TBM", ".TBMD", ".TBMC", ".TBMB"
        };

        public bool CanPack => false;

        public bool CanTransformOnExtract(string name) =>
            SupportedExts.Contains(Path.GetExtension(name));

        public (string name, byte[] data) OnExtract(byte[] srcData, string srcName, Manifest manifest)
        {
            string png = TextureUtil.ToPngPath(srcName);
            uint id = TexId.Standalone(srcName);
            return (png, ExtractAndRecord(srcData, id, srcName, "0", png, manifest));
        }

        internal static byte[] ExtractAndRecord(byte[] srcData, uint id, string logicalName, string type, string pngRel, Manifest manifest)
        {
            if (srcData.Length < 0x34)
                throw new InvalidDataException("TBM: file too small");

            var h = TbmImageCodec.ReadHeader(srcData);

            using var img = TbmImageCodec.Decode(srcData, h);
            using var ms = new MemoryStream();
            img.SaveAsPng(ms);

            manifest.AddT32(new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["id"] = TexId.ToX8(id),
                ["name"] = logicalName,
                ["type"] = type,
                ["tex"] = "TBM",
                ["png"] = pngRel,

                ["tag"] = h.Tag.ToString(),
                ["status"] = h.Status.ToString(),
                ["priority"] = h.Priority.ToString(),
                ["posX"] = h.PosX.ToString(),
                ["posY"] = h.PosY.ToString(),
                ["placement"] = h.Placement.ToString(),
                ["stdW"] = h.StdW.ToString(),
                ["stdH"] = h.StdH.ToString(),
                ["colorBits"] = h.ColorBits.ToString(),
                ["numPalettes"] = h.NumPalettes.ToString(),
            });

            return ms.ToArray();
        }

        public static byte[] Build(Dictionary<string, string> e, string rootDir)
        {
            string pngPath = Path.Combine(rootDir, TextureUtil.Get(e, "png"));
            using var img = Image.Load<Rgba32>(pngPath);

            uint tag = TextureUtil.GetU32(e, "tag", TextureUtil.FourCC("TBMD"));
            uint status = TextureUtil.GetU32(e, "status");
            uint priority = TextureUtil.GetU32(e, "priority");
            int posX = TextureUtil.GetI32(e, "posX");
            int posY = TextureUtil.GetI32(e, "posY");
            uint placement = TextureUtil.GetU32(e, "placement");
            int stdW = TextureUtil.GetI32(e, "stdW", 1280);
            int stdH = TextureUtil.GetI32(e, "stdH", 720);

            uint colorBits = TextureUtil.GetU32(e, "colorBits", 32);
            uint numPalettes = TextureUtil.GetU32(e, "numPalettes", 0);

            return TbmImageCodec.Encode(img, tag, status, priority, posX, posY, placement, stdW, stdH, colorBits, numPalettes);
        }
    }
}