using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace Signer
{
    internal static class Program
    {
        private static readonly byte[] Magic = Encoding.ASCII.GetBytes("GOSIGN01");

        private static int Main(string[] args)
        {
            try
            {
                if (args.Length == 2 && args[0] == "keygen") return KeyGen(args[1]);
                if (args.Length == 4 && args[0] == "sign") return Sign(args[1], args[2], args[3]);
                Console.Error.WriteLine("usage: keygen <private-key.xml> | sign <dll> <private-key.xml> <version>");
                return 2;
            }
            catch (Exception e)
            {
                Console.Error.WriteLine("signer: " + e.Message);
                return 1;
            }
        }

        private static int KeyGen(string path)
        {
            if (File.Exists(path))
            {
                Console.Error.WriteLine($"{path} already exists; refusing to overwrite a signing key.");
                return 1;
            }
            using var rsa = RSA.Create(2048);
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path)));
            File.WriteAllText(path, rsa.ToXmlString(includePrivateParameters: true));
            Console.WriteLine(rsa.ToXmlString(includePrivateParameters: false));
            return 0;
        }

        private static int Sign(string dllPath, string keyPath, string version)
        {
            var dll = File.ReadAllBytes(dllPath);
            if (EndsWithMagic(dll)) throw new InvalidOperationException(dllPath + " is already signed");
            var versionBytes = Encoding.UTF8.GetBytes(version);
            var signed = new byte[dll.Length + versionBytes.Length];
            Buffer.BlockCopy(dll, 0, signed, 0, dll.Length);
            Buffer.BlockCopy(versionBytes, 0, signed, dll.Length, versionBytes.Length);

            using var rsa = RSA.Create();
            rsa.FromXmlString(File.ReadAllText(keyPath));
            var signature = rsa.SignData(signed, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);

            using var output = new MemoryStream();
            output.Write(signed);
            output.Write(BitConverter.GetBytes(versionBytes.Length));
            output.Write(signature);
            output.Write(BitConverter.GetBytes(signature.Length));
            output.Write(Magic);
            File.WriteAllBytes(dllPath, output.ToArray());
            Console.WriteLine($"signed {Path.GetFileName(dllPath)} as {version}");
            return 0;
        }

        private static bool EndsWithMagic(byte[] data)
        {
            if (data.Length < Magic.Length) return false;
            for (int i = 0; i < Magic.Length; i++)
            {
                if (data[data.Length - Magic.Length + i] != Magic[i]) return false;
            }
            return true;
        }
    }
}
