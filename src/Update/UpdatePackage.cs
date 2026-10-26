using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace GlurpOnline.Update
{
    internal sealed class UpdatePackage
    {
        private const string PublicKey =
            "<RSAKeyValue><Modulus>zliDM5lEUi7IxhxXRzAMMMR+YaP1W27Qh5U1XomX9MJ8q0fdrtzjwSMVpu1ekfN/Y/1tqRr5nR4K75Ta1SFaoJrlmrW0EvWjyORIYyxLJ9EgVPI7/M8RI+cRw/0mA1rkkE582QUKJ4kSX72RUoI4DxlSuIfSDbOA0I7AHqcUF8NpmHBHb9dnNyxE4Ynhek5OSQJoiU8O+Ufht5Rcmc3fs+K4cX4ei6i/jhjf5pGuNBreHTDpd0JPlJG5tUKC/e94UO52ReZLvx6SlyLOSgZ8YxRVszB/Tk6lreM+msbeLDR6YqpRRE3s9EvcTKhc/fi0GSbfexWoFlzfTJOvdL+jxQ==</Modulus><Exponent>AQAB</Exponent></RSAKeyValue>";

        private static readonly byte[] Magic = Encoding.ASCII.GetBytes("GOSIGN01");
        public const int MaxSize = 8 * 1024 * 1024;

        public byte[] Bytes { get; private set; }
        public Version Version { get; private set; }
        public string VersionText { get; private set; }

        public static UpdatePackage FromFile(string path)
        {
            try { return Parse(File.ReadAllBytes(path)); }
            catch (Exception e)
            {
                Plugin.Log.LogWarning($"Couldn't read {path}: {e.Message}");
                return null;
            }
        }

        public static UpdatePackage Parse(byte[] data)
        {
            try
            {
                if (data == null || data.Length < 1024 || data.Length > MaxSize) return null;
                int magicStart = data.Length - Magic.Length;
                for (int i = 0; i < Magic.Length; i++) if (data[magicStart + i] != Magic[i]) return null;

                int signatureLength = BitConverter.ToInt32(data, magicStart - 4);
                if (signatureLength <= 0 || signatureLength > 1024) return null;
                int signatureStart = magicStart - 4 - signatureLength;
                int versionLength = BitConverter.ToInt32(data, signatureStart - 4);
                if (versionLength <= 0 || versionLength > 64) return null;
                int signedLength = signatureStart - 4;
                int versionStart = signedLength - versionLength;
                if (versionStart < 512) return null;

                var signature = new byte[signatureLength];
                Buffer.BlockCopy(data, signatureStart, signature, 0, signatureLength);
                byte[] hash;
                using (var sha = SHA256.Create()) hash = sha.ComputeHash(data, 0, signedLength);
                using (var rsa = new RSACryptoServiceProvider())
                {
                    rsa.PersistKeyInCsp = false;
                    rsa.FromXmlString(PublicKey);
                    if (!rsa.VerifyHash(hash, CryptoConfig.MapNameToOID("SHA256"), signature))
                    {
                        Plugin.Log.LogWarning("Rejected an update with an invalid signature");
                        return null;
                    }
                }

                string versionText = Encoding.UTF8.GetString(data, versionStart, versionLength);
                return new UpdatePackage { Bytes = data, VersionText = versionText, Version = new Version(versionText) };
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning("Rejected an unreadable update: " + e.Message);
                return null;
            }
        }
    }
}
