using System.IO;

namespace IpfbTool.Core
{
    internal interface ITransformer
    {
        bool CanTransformOnExtract(string name);
        (string name, byte[] data) OnExtract(byte[] srcData, string srcName);

        bool CanTransformOnPack(string name);
        (string name, byte[] data) OnPack(string srcPath, string srcName);
    }

    internal static class Transformers
    {
        static readonly ITransformer[] list = { new TBL(), new ISB(), new T8aD(), new RATC(), new LSTA() };

        public static void PreloadForPack(string rootDir) => T8aD.PreloadHeaderDb(rootDir);

        public static (string name, string path) ProcessExtract(string name, string outPath, byte[] data)
        {
            foreach (var t in list)
            {
                if (!t.CanTransformOnExtract(name)) continue;

                var (newName, outData) = t.OnExtract(data, name);
                string dir = Path.GetDirectoryName(outPath) ?? "";
                string newPath = Path.Combine(dir, Path.GetFileName(newName));

                Directory.CreateDirectory(Path.GetDirectoryName(newPath) ?? ".");
                File.WriteAllBytes(newPath, outData);
                return (newName, newPath);
            }

            Directory.CreateDirectory(Path.GetDirectoryName(outPath) ?? ".");
            File.WriteAllBytes(outPath, data);
            return (name, outPath);
        }

        public static (string name, byte[] data) ProcessPack(string name, string srcPath)
        {
            foreach (var t in list)
            {
                if (!t.CanTransformOnPack(name)) continue;
                return t.OnPack(srcPath, name);
            }
            return (name, File.ReadAllBytes(srcPath));
        }
    }
}