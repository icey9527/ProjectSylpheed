using System;
using System.Text;

namespace IpfbTool.Core
{
    internal static class TextureHub
    {
        static readonly uint TBM_ = TextureUtil.FourCC("TBM ");
        static readonly uint TBMB = TextureUtil.FourCC("TBMB");
        static readonly uint TBMC = TextureUtil.FourCC("TBMC");
        static readonly uint TBMD = TextureUtil.FourCC("TBMD");

        public static bool TryExtractAndRecord(byte[] payload, uint id, string logicalName, string type, string pngRel,
            Manifest manifest, out string tex, out byte[] png)
        {
            tex = "";
            png = Array.Empty<byte>();
            if (payload == null || payload.Length < 4) return false;

            uint tag = TextureUtil.ReadU32BE(payload, 0);

            if (IsT32(tag))
            {
                tex = "T32";
                png = T32.ExtractAndRecord(payload, id, logicalName, type, pngRel, manifest);
                return true;
            }

            if (tag == TBM_ || tag == TBMB || tag == TBMC || tag == TBMD)
            {
                tex = "TBM";
                png = TBM.ExtractAndRecord(payload, id, logicalName, type, pngRel, manifest);
                return true;
            }

            return false;
        }

        static bool IsT32(uint tag)
        {
            string m = Encoding.ASCII.GetString(new[]
            {
                (byte)(tag >> 24), (byte)(tag >> 16), (byte)(tag >> 8), (byte)tag
            });

            return m is "T8aD" or "T8aB" or "T8aC" or "T32 " or
                     "T4aD" or "T4aB" or "T4aC" or
                     "T1aD" or "T1aB" or "T1aC" or
                     "4444" or "1555";
        }
    }
}