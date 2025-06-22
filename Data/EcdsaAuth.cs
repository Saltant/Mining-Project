namespace PoW.Data
{
    using System;
    using System.Security.Cryptography;
    using System.Text;

    public class EcdsaAuth
    {
        public static string GenerateClientId()
        {
            using ECDsa ecdsa = ECDsa.Create();
            var publicKey = ecdsa.ExportSubjectPublicKeyInfo();
            return Convert.ToBase64String(publicKey);
        }

        public static (string PublicKey, string PrivateKey) GenerateKeyPair()
        {
            using ECDsa ecdsa = ECDsa.Create();
            var publicKey = Convert.ToBase64String(ecdsa.ExportSubjectPublicKeyInfo());
            var privateKey = Convert.ToBase64String(ecdsa.ExportPkcs8PrivateKey());
            return (publicKey, privateKey);
        }

        public static bool VerifySignature(string publicKey, string message, string signature)
        {
            try
            {
                using ECDsa ecdsa = ECDsa.Create();
                ecdsa.ImportSubjectPublicKeyInfo(Convert.FromBase64String(publicKey), out _);
                byte[] messageBytes = Encoding.UTF8.GetBytes(message);
                byte[] signatureBytes = Convert.FromBase64String(signature);
                return ecdsa.VerifyData(messageBytes, signatureBytes, HashAlgorithmName.SHA256);
            }
            catch
            {
                return false;
            }
        }

        public static string SignMessage(string privateKey, string message)
        {
            try
            {
                using ECDsa ecdsa = ECDsa.Create();
                ecdsa.ImportPkcs8PrivateKey(Convert.FromBase64String(privateKey), out _);
                byte[] messageBytes = Encoding.UTF8.GetBytes(message);
                byte[] signature = ecdsa.SignData(messageBytes, HashAlgorithmName.SHA256);
                return Convert.ToBase64String(signature);
            }
            catch (Exception ex)
            {
                throw new Exception("Ошибка подписи сообщения.", ex);
            }
        }
    }
}
