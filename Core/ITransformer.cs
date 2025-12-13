namespace IpfbTool.Core
{
    internal interface ITransformer
    {
        bool CanTransformOnExtract(string name);
        (string name, byte[] data) OnExtract(byte[] srcData, string srcName);

        bool CanTransformOnPack(string name);
        (string name, byte[] data) OnPack(string srcPath, string srcName);
    }
}