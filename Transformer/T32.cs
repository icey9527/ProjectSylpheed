using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace IpfbTool.Core
{
    internal sealed class T32 : ITransformer
    {
        static readonly HashSet<string> SupportedExts = new(StringComparer.OrdinalIgnoreCase)
        {
            ".T32", ".T8aD", ".T8aB", ".T8aC", ".T4aD", ".T4aB", ".T4aC", ".T1aD", ".T1aB", ".T1aC", ".4444", ".1555"
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
            if (srcData.Length < 0x2C)
                throw new InvalidDataException("T32: file too small");

            string m = Encoding.ASCII.GetString(srcData, 0, 4);
            bool le = m is "T32 " or "T8aB" or "T4aB" or "T1aB" or "4444" or "1555";

            uint tag = BeBinary.ReadUInt32(srcData, 0x00, le);
            uint status = BeBinary.ReadUInt32(srcData, 0x04, le);
            uint priority = BeBinary.ReadUInt32(srcData, 0x08, le);
            int posX = BeBinary.ReadInt32(srcData, 0x0C, le);
            int posY = BeBinary.ReadInt32(srcData, 0x10, le);

            uint placement = le ? 0u : BeBinary.ReadUInt32(srcData, 0x20, false);
            int stdW = le ? 640 : BeBinary.ReadInt32(srcData, 0x24, false);
            int stdH = le ? 480 : BeBinary.ReadInt32(srcData, 0x28, false);

            int ofsBase = 0;
            if (le)
            {
                ofsBase = tag is 540160852u or 875836468u or 892679473u ? 32 : 36;
            }

            using var img = T8aImageCodec.Decode(srcData);
            using var ms = new MemoryStream();
            img.SaveAsPng(ms);

            var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["id"] = TexId.ToX8(id),
                ["name"] = logicalName,
                ["type"] = type,
                ["tex"] = "T32",
                ["png"] = pngRel,

                ["tag"] = tag.ToString(),
                ["status"] = status.ToString(),
                ["priority"] = priority.ToString(),
                ["posX"] = posX.ToString(),
                ["posY"] = posY.ToString(),
                ["placement"] = placement.ToString(),
                ["stdW"] = stdW.ToString(),
                ["stdH"] = stdH.ToString(),
            };

            if (le)
            {
                dict["le"] = "1";
                dict["ofsBase"] = ofsBase.ToString();
            }

            manifest.AddT32(dict);

            return ms.ToArray();
        }

        public static byte[] Build(Dictionary<string, string> e, string rootDir)
        {
            string pngPath = Path.Combine(rootDir, TextureUtil.Get(e, "png"));
            using var img = Image.Load<Rgba32>(pngPath);

            uint tag = TextureUtil.GetU32(e, "tag", TextureUtil.FourCC("T8aD"));
            bool le = e.TryGetValue("le", out var lev) && lev == "1";

            byte[] header = new byte[0x2C];

            BeBinary.WriteUInt32(header, 0x00, tag, le);
            BeBinary.WriteUInt32(header, 0x04, TextureUtil.GetU32(e, "status"), le);
            BeBinary.WriteUInt32(header, 0x08, TextureUtil.GetU32(e, "priority"), le);
            BeBinary.WriteInt32(header, 0x0C, TextureUtil.GetI32(e, "posX"), le);
            BeBinary.WriteInt32(header, 0x10, TextureUtil.GetI32(e, "posY"), le);

            BeBinary.WriteInt32(header, 0x14, img.Width, le);
            BeBinary.WriteInt32(header, 0x18, img.Height, le);
            BeBinary.WriteInt32(header, 0x1C, 0, le);

            if (!le)
            {
                BeBinary.WriteUInt32(header, 0x20, TextureUtil.GetU32(e, "placement"), false);
                BeBinary.WriteInt32(header, 0x24, TextureUtil.GetI32(e, "stdW", 1280), false);
                BeBinary.WriteInt32(header, 0x28, TextureUtil.GetI32(e, "stdH", 720), false);
            }
            else
            {
                BeBinary.WriteUInt32(header, 0x20, 0, true);
                BeBinary.WriteInt32(header, 0x24, 640, true);
                BeBinary.WriteInt32(header, 0x28, 480, true);
            }

            return T8aImageCodec.Encode(img, header);
        }
    }
}