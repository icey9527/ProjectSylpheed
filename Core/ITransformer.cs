namespace IpfbTool.Core
{
    internal interface ITransformer
    {
        bool CanExtract => true;
        bool CanPack => true;

        bool CanTransformOnExtract(string name);
        (string name, byte[] data) OnExtract(byte[] srcData, string srcName, Manifest manifest);

        // 打包方法也给默认实现
        bool CanTransformOnPack(string name) => false;
        (string name, byte[] data) OnPack(string srcPath, string srcName) => (srcName, null!);
    }
}