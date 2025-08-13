using System.Security.Cryptography;
using System.Text;

namespace AzureDiscovery.Infrastructure.Helpers
{
    public class AesEncryptionHelper
    {
        public static string Encrypt(string plainText, string key)
        {
            var aes = Aes.Create();
            aes.Key = Encoding.UTF8.GetBytes(key[..32]);
            aes.GenerateIV();

            var encryptor = aes.CreateEncryptor();
            var input = Encoding.UTF8.GetBytes(plainText);
            var cipher = encryptor.TransformFinalBlock(input, 0, input.Length);

            var result = aes.IV.Concat(cipher).ToArray();
            return Convert.ToBase64String(result);
        }

        public static string Decrypt(string encryptedText, string key)
        {
            var fullCipher = Convert.FromBase64String(encryptedText);
            var iv = fullCipher[..16];
            var cipher = fullCipher[16..];

            var aes = Aes.Create();
            aes.Key = Encoding.UTF8.GetBytes(key[..32]);
            aes.IV = iv;

            var decryptor = aes.CreateDecryptor();
            var result = decryptor.TransformFinalBlock(cipher, 0, cipher.Length);
            return Encoding.UTF8.GetString(result);
        }
    }
}
