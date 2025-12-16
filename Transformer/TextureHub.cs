using System;

namespace IpfbTool.Core
{
    internal static class TextureHub
    {
        static readonly uint TBM_ = TextureUtil.FourCC("TBM ");
        static readonly uint TBMB = TextureUtil.FourCC("TBMB");
        static readonly uint TBMC = TextureUtil.FourCC("TBMC");
        static readonly uint TBMD = TextureUtil.FourCC("TBMD");

        static readonly uint T8aD = TextureUtil.FourCC("T8aD");
        static readonly uint T8aB = TextureUtil.FourCC("T8aB");
        static readonly uint T8aC = TextureUtil.FourCC("T8aC");
        static readonly uint T32_ = TextureUtil.FourCC("T32 ");
        static readonly uint T4aD = TextureUtil.FourCC("T4aD");
        static readonly uint T4aB = TextureUtil.FourCC("T4aB");
        static readonly uint T4aC = TextureUtil.FourCC("T4aC");
        static readonly uint T1aD = TextureUtil.FourCC("T1aD");
        static readonly uint T1aB = TextureUtil.FourCC("T1aB");
        static readonly uint T1aC = TextureUtil.FourCC("T1aC");
        static readonly uint _4444 = TextureUtil.FourCC("4444");
        static readonly uint _1555 = TextureUtil.FourCC("1555");

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

        static bool IsT32(uint tag) => tag == T8aD || tag == T8aB || tag == T8aC || tag == T32_ ||
                                      tag == T4aD || tag == T4aB || tag == T4aC ||
                                      tag == T1aD || tag == T1aB || tag == T1aC ||
                                      tag == _4444 || tag == _1555;
    }
}