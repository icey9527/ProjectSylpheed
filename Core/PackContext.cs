using System.IO;

namespace IpfbTool.Core
{
    internal static class PackContext
    {
        public static string RootDir { get; set; } = "";
        
        internal static string CurrentOutputDir { get; set; } = "";
    }
}