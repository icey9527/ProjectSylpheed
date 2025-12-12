using System;
using System.IO;

namespace IpfbTool.Core
{
    internal static class FileId
    {
        public static uint FromPath(string path)
        {
            if (string.IsNullOrEmpty(path))
                return 0;

            string fileName = Path.GetFileName(path);

            if (fileName.Length > 0 && fileName[0] == '$')
            {
                string s = fileName.Substring(1);
                int i = 0;
                while (i < s.Length && i < 8 && Uri.IsHexDigit(s[i]))
                    i++;
                if (i == 0)
                    return 0;
                string hex = s.Substring(0, i);
                return Convert.ToUInt32(hex, 16);
            }

            string s2 = path.ToLowerInvariant();
            uint v2 = 0;
            uint v3 = 0;

            foreach (char ch in s2)
            {
                uint v6 = ch;
                v3 = (v3 + v6) & 0xFFFFFFFFu;
                v2 = (v6 + ((v2 << 8) & 0xFFFFFFFFu)) & 0xFFFFFFFFu;
                if ((v2 & 0xFF800000u) != 0)
                    v2 %= 0xFFF9D7u;
            }

            return (v2 | (v3 << 24)) & 0xFFFFFFFFu;
        }
    }
}