using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace IpfbTool.Core
{
    internal static class TextureUtil
    {
        static readonly HashSet<string> TextureExts = new(StringComparer.OrdinalIgnoreCase)
        {
            ".T32",".T8aD",".T8aB",".T8aC",".T4aD",".T4aB",".T4aC",".T1aD",".T1aB",".T1aC",".4444",".1555",
            ".TBM",".TBMD",".TBMC",".TBMB"
        };

        public static string ToPngPath(string srcName)
        {
            string dir = Path.GetDirectoryName(srcName) ?? "";
            string file = Path.GetFileName(srcName);
            string ext = Path.GetExtension(file);

            if (TextureExts.Contains(ext))
                file = Path.GetFileNameWithoutExtension(file);

            return string.IsNullOrEmpty(dir) ? (file + ".png") : Path.Combine(dir, file + ".png");
        }

        public static uint ReadU32BE(byte[] d, int o) => unchecked((uint)BeBinary.ReadInt32(d, o));
        public static int ReadI32BE(byte[] d, int o) => BeBinary.ReadInt32(d, o);
        public static void WriteU32BE(byte[] d, int o, uint v) => BeBinary.WriteInt32(d, o, unchecked((int)v));
        public static void WriteI32BE(byte[] d, int o, int v) => BeBinary.WriteInt32(d, o, v);

        public static uint FourCC(string s)
        {
            s = (s ?? "").PadRight(4);
            var b = Encoding.ASCII.GetBytes(s[..4]);
            return (uint)((b[0] << 24) | (b[1] << 16) | (b[2] << 8) | b[3]);
        }

        public static string Get(Dictionary<string, string> d, string key, string def = "")
            => d != null && d.TryGetValue(key, out var v) && !string.IsNullOrWhiteSpace(v) ? v : def;

        public static uint GetU32(Dictionary<string, string> d, string key, uint def = 0)
        {
            if (d == null || !d.TryGetValue(key, out var s) || string.IsNullOrWhiteSpace(s)) return def;
            s = s.Trim();
            if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase) &&
                uint.TryParse(s[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var hx))
                return hx;
            return uint.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : def;
        }

        public static int GetI32(Dictionary<string, string> d, string key, int def = 0)
        {
            if (d == null || !d.TryGetValue(key, out var s) || string.IsNullOrWhiteSpace(s)) return def;
            s = s.Trim();
            if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase) &&
                int.TryParse(s[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var hx))
                return hx;
            return int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : def;
        }
    }
}