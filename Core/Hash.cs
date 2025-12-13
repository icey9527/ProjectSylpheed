using System.Text;

namespace IpfbTool.Core
{
    internal static class Hash
    {
        static Hash()
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        }

        static readonly Encoding CP932 = Encoding.GetEncoding(932);
        static readonly Encoding UTF16BE = Encoding.BigEndianUnicode;

        public static uint Filehash(string path)
        {
            if (string.IsNullOrEmpty(path)) return 0;

            uint v2 = 0, v3 = 0;
            foreach (var ch in path.ToLowerInvariant())
            {
                uint v6 = ch;
                v3 = unchecked(v3 + v6);
                v2 = unchecked(v6 + (v2 << 8));
                if ((v2 & 0xFF800000u) != 0) v2 %= 0xFFF9D7u;
            }

            return unchecked(v2 | (v3 << 24));
        }

        public static int JIShash(string s)
        {
            if (string.IsNullOrEmpty(s)) return 0;

            var bytes = CP932.GetBytes(s);
            ulong hash = 0;
            int sum = 0;

            foreach (var b in bytes)
            {
                var sb = (sbyte)b;
                hash = (((hash & 0xFFFFFF) << 8) + (uint)(int)sb) % 0xFFFFDF;
                sum += sb;
            }

            return unchecked((int)((((uint)sum & 0xFF) << 24) | (uint)hash));
        }

        public static int UTF16BEhash(string s)
        {
            if (string.IsNullOrEmpty(s)) return 0;

            var bytes = UTF16BE.GetBytes(s);
            ulong hash = 0, sum = 0;

            for (int i = 0; i + 1 < bytes.Length; i += 2)
            {
                ushort wc = (ushort)((bytes[i] << 8) | bytes[i + 1]);
                sum += wc;
                hash = (hash * 0x10000 + wc) % 0xFFFFFF67;
            }

            hash %= 0xFFFFDF;
            return unchecked((int)((((uint)sum & 0xFF) << 24) | (uint)hash));
        }
    }
}