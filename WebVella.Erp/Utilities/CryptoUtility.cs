#region <--- DIRECTIVES --->
using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;

#endregion

namespace WebVella.Erp.Utilities
{
    public class CryptoUtility
    {
        #region <--- Fields --->

        private static string cryptKey;

        // AES-GCM standard sizes (in bytes): a 96-bit nonce and a 128-bit authentication tag.
        // These back the authenticated-encryption API below (User Example 3 / AAP §0.7.3).
        private const int AesGcmNonceSize = 12;
        private const int AesGcmTagSize = 16;

        #endregion

        #region <--- Properties --->

        public static string CryptKey
        {
            get
            {
                if (string.IsNullOrEmpty(cryptKey))
                {
                    if (string.IsNullOrWhiteSpace(ErpSettings.EncryptionKey))
                    {
                        throw new InvalidOperationException("Settings:EncryptionKey is not configured. A symmetric encryption key is required; set it via environment variable, user-secrets, or a secret store.");
                    }

                    cryptKey = ErpSettings.EncryptionKey;
                }
                return cryptKey;
            }
        }

        #endregion

        #region <--- Methods --->

        /// <summary>
        /// 	Encrypts the text using default related key
        /// </summary>
        /// <param name="text"> The text. </param>
        /// <param name="algorithm"> The algorithm. </param>
        /// <returns> </returns>
        public static string EncryptText(string text, SymmetricAlgorithm algorithm)
        {
            return EncryptText(text, CryptKey, algorithm);
        }

        /// <summary>
        /// 	Decrypts the text using machine related key
        /// </summary>
        /// <param name="cypherText"> The cypher text. </param>
        /// <param name="algorithm"> The algorithm. </param>
        /// <returns> </returns>
        public static string DecryptText(string cypherText, SymmetricAlgorithm algorithm)
        {
            return DecryptText(cypherText, CryptKey, algorithm);
        }

        /// <summary>
        /// 	Encrypts the text using default related key
        /// </summary>
        /// <param name="inputData"> The input data. </param>
        /// <param name="algorithm"> The algorithm. </param>
        /// <returns> </returns>
        public static byte[] EncryptData(byte[] inputData, SymmetricAlgorithm algorithm)
        {
            return EncryptData(inputData, CryptKey, algorithm);
        }

        /// <summary>
        /// 	Decrypts the text using machine related key
        /// </summary>
        /// <param name="inputData"> The input data. </param>
        /// <param name="algorithm"> The algorithm. </param>
        /// <returns> </returns>
        public static byte[] DecryptData(byte[] inputData, SymmetricAlgorithm algorithm)
        {
            return DecryptData(inputData, CryptKey, algorithm);
        }

        /// <summary>
        /// 	Encrypts the text.
        /// </summary>
        /// <param name="text"> The text. </param>
        /// <param name="key"> The key. </param>
        /// <param name="algorithm"> The algorithm. </param>
        /// <returns> </returns>
        public static string EncryptText(string text, string key, SymmetricAlgorithm algorithm)
        {
            algorithm.Key = GetValidKey(key, algorithm);
            algorithm.IV = GetValidIV(key, algorithm.IV.Length);

            byte[] buffer = EncryptInternal(text, algorithm);
            return Convert.ToBase64String(buffer);
        }

        /// <summary>
        /// 	Decrypts the text.
        /// </summary>
        /// <param name="cypherText"> The cypher text. </param>
        /// <param name="key"> The key. </param>
        /// <param name="algorithm"> The algorithm. </param>
        /// <returns> </returns>
        public static string DecryptText(string cypherText, string key, SymmetricAlgorithm algorithm)
        {
            algorithm.Key = GetValidKey(key, algorithm);
            algorithm.IV = GetValidIV(key, algorithm.IV.Length);

            byte[] inputBuffer = Convert.FromBase64String(cypherText);
            return DecryptInternal(inputBuffer, algorithm);
        }

        /// <summary>
        /// 	Encrypts the text.
        /// </summary>
        /// <param name="data"> The data. </param>
        /// <param name="key"> The key. </param>
        /// <param name="algorithm"> The algorithm. </param>
        /// <returns> </returns>
        public static byte[] EncryptData(byte[] data, string key, SymmetricAlgorithm algorithm)
        {
            algorithm.Key = GetValidKey(key, algorithm);
            algorithm.IV = GetValidIV(key, algorithm.IV.Length);

            return EncryptDataInternal(data, algorithm);
        }

        /// <summary>
        /// 	Decrypts the text.
        /// </summary>
        /// <param name="data"> The data. </param>
        /// <param name="key"> The key. </param>
        /// <param name="algorithm"> The algorithm. </param>
        /// <returns> </returns>
        public static byte[] DecryptData(byte[] data, string key, SymmetricAlgorithm algorithm)
        {
            algorithm.Key = GetValidKey(key, algorithm);
            algorithm.IV = GetValidIV(key, algorithm.IV.Length);

            return DecryptDataInternal(data, algorithm);
        }

        // ---------------------------------------------------------------------------------------------
        // Authenticated symmetric encryption (AES-256-GCM) — OWASP A02 / User Example 3 / AAP §0.7.3.
        //
        // Unlike the legacy SymmetricAlgorithm-based helpers above (which derive a DETERMINISTIC IV from
        // the key and provide no integrity guarantee), these methods use AES-256 in Galois/Counter Mode:
        // a fresh cryptographically-random nonce is generated for every operation and an authentication
        // tag is produced, so any tampering with the ciphertext is detected on decryption (the AesGcm
        // primitive throws CryptographicException). This is a NEW, additive API — the legacy helpers are
        // retained unchanged for backward compatibility and no existing ciphertext requires migration.
        // ---------------------------------------------------------------------------------------------

        /// <summary>
        /// 	Encrypts UTF-8 text with AES-256-GCM using the configured encryption key.
        /// 	The returned Base64 string packs the random nonce, the authentication tag and the
        /// 	ciphertext as: base64( nonce[12] || tag[16] || ciphertext ).
        /// </summary>
        /// <param name="plainText"> The plaintext to encrypt. </param>
        /// <returns> Base64-encoded nonce + tag + ciphertext. </returns>
        public static string EncryptTextAuthenticated(string plainText)
        {
            if (plainText == null)
                throw new ArgumentNullException(nameof(plainText));

            byte[] cipher = EncryptDataAuthenticated(Encoding.UTF8.GetBytes(plainText));
            return Convert.ToBase64String(cipher);
        }

        /// <summary>
        /// 	Decrypts a Base64 payload produced by <see cref="EncryptTextAuthenticated" /> back to its
        /// 	original UTF-8 text. Throws <see cref="CryptographicException" /> if the data has been
        /// 	tampered with or the key is wrong.
        /// </summary>
        /// <param name="cypherText"> Base64-encoded nonce + tag + ciphertext. </param>
        /// <returns> The decrypted plaintext. </returns>
        public static string DecryptTextAuthenticated(string cypherText)
        {
            if (cypherText == null)
                throw new ArgumentNullException(nameof(cypherText));

            byte[] plain = DecryptDataAuthenticated(Convert.FromBase64String(cypherText));
            return Encoding.UTF8.GetString(plain);
        }

        /// <summary>
        /// 	Encrypts arbitrary data with AES-256-GCM using the configured encryption key. The output
        /// 	packs the random nonce, the authentication tag and the ciphertext as:
        /// 	nonce[12] || tag[16] || ciphertext.
        /// </summary>
        /// <param name="plainData"> The data to encrypt. </param>
        /// <returns> nonce + tag + ciphertext. </returns>
        public static byte[] EncryptDataAuthenticated(byte[] plainData)
        {
            if (plainData == null)
                throw new ArgumentNullException(nameof(plainData));

            byte[] key = DeriveAes256Key(CryptKey);

            byte[] nonce = new byte[AesGcmNonceSize];
            RandomNumberGenerator.Fill(nonce);

            byte[] tag = new byte[AesGcmTagSize];
            byte[] cipherText = new byte[plainData.Length];

            // .NET 8+ requires the authentication tag size to be supplied explicitly to the constructor.
            using (var aesGcm = new AesGcm(key, AesGcmTagSize))
            {
                aesGcm.Encrypt(nonce, plainData, cipherText, tag);
            }

            // Pack nonce || tag || ciphertext so a single opaque token carries everything decryption needs.
            byte[] result = new byte[AesGcmNonceSize + AesGcmTagSize + cipherText.Length];
            Buffer.BlockCopy(nonce, 0, result, 0, AesGcmNonceSize);
            Buffer.BlockCopy(tag, 0, result, AesGcmNonceSize, AesGcmTagSize);
            Buffer.BlockCopy(cipherText, 0, result, AesGcmNonceSize + AesGcmTagSize, cipherText.Length);

            return result;
        }

        /// <summary>
        /// 	Decrypts data produced by <see cref="EncryptDataAuthenticated" />. Throws
        /// 	<see cref="CryptographicException" /> if authentication fails (tampering or wrong key).
        /// </summary>
        /// <param name="cipherData"> nonce + tag + ciphertext. </param>
        /// <returns> The decrypted data. </returns>
        public static byte[] DecryptDataAuthenticated(byte[] cipherData)
        {
            if (cipherData == null)
                throw new ArgumentNullException(nameof(cipherData));
            if (cipherData.Length < AesGcmNonceSize + AesGcmTagSize)
                throw new ArgumentException("Cipher data is too short to contain a nonce and authentication tag.", nameof(cipherData));

            byte[] key = DeriveAes256Key(CryptKey);

            byte[] nonce = new byte[AesGcmNonceSize];
            byte[] tag = new byte[AesGcmTagSize];
            int cipherLength = cipherData.Length - AesGcmNonceSize - AesGcmTagSize;
            byte[] cipherText = new byte[cipherLength];

            Buffer.BlockCopy(cipherData, 0, nonce, 0, AesGcmNonceSize);
            Buffer.BlockCopy(cipherData, AesGcmNonceSize, tag, 0, AesGcmTagSize);
            Buffer.BlockCopy(cipherData, AesGcmNonceSize + AesGcmTagSize, cipherText, 0, cipherLength);

            byte[] plainData = new byte[cipherLength];
            using (var aesGcm = new AesGcm(key, AesGcmTagSize))
            {
                aesGcm.Decrypt(nonce, cipherText, tag, plainData);
            }

            return plainData;
        }

        /// <summary>
        /// 	Computes MD5 hash value for specified input string
        /// </summary>
        /// <param name="inputString"> The input string. </param>
        /// <returns> </returns>
        public static string ComputeMD5Hash(string inputString)
        {
            byte[] bytes = (new UnicodeEncoding()).GetBytes(inputString);
            byte[] hashValue = (MD5.Create()).ComputeHash(bytes);
            return BitConverter.ToString(hashValue);
        }

        /// <summary>
        /// 	Computes the MD5 hash.
        /// </summary>
        /// <param name="inputString"> The input string. </param>
        /// <returns> </returns>
        public static byte[] ComputeMD5HashBytes(string inputString)
        {
            byte[] bytes = (new UnicodeEncoding()).GetBytes(inputString);
            return (MD5.Create()).ComputeHash(bytes);
        }

        /// <summary>
        /// 	Computes the odd M d5 hash.
        /// </summary>
        /// <param name="str"> The STR. </param>
        /// <returns> </returns>
        public static string ComputeOddMD5Hash(string str)
        {
            MD5 md5 = MD5.Create();
            byte[] dataMd5 = md5.ComputeHash(Encoding.Unicode.GetBytes(str));
            StringBuilder sb = new StringBuilder();
            for (int i = 0; i < dataMd5.Length; i++)
                sb.AppendFormat("{0:x2}", dataMd5[i]);
            return sb.ToString();
        }

        /// <summary>
        /// 	Computes the PHP like M d5 hash.
        /// </summary>
        /// <param name="text"> The text. </param>
        /// <returns> </returns>
        public static string ComputePhpLikeMD5Hash(string text)
        {
            byte[] textBytes = Encoding.Default.GetBytes(text);

            var cryptHandler = MD5.Create();
            byte[] hash = cryptHandler.ComputeHash(textBytes);
            string ret = "";
            foreach (byte a in hash)
            {
                if (a < 16)
                    ret += "0" + a.ToString("x");
                else
                    ret += a.ToString("x");
            }
            return ret;
        }

        #endregion

        #region <--- Private Methods --->


        /// <summary>
        /// 	Gets the valid encode key.
        /// </summary>
        /// <param name="key"> The key. </param>
        /// <param name="encodeMethod"> The encode method. </param>
        /// <returns> </returns>
        private static byte[] GetValidKey(string key, SymmetricAlgorithm encodeMethod)
        {
            string result;
            if (encodeMethod.LegalKeySizes.Length > 0)
            {
                int size = encodeMethod.LegalKeySizes[0].MinSize;

                // key sizes are in bits
                while (key.Length * 8 > size &&
                       encodeMethod.LegalKeySizes[0].SkipSize > 0 &&
                       size < encodeMethod.LegalKeySizes[0].MaxSize)
                    size += encodeMethod.LegalKeySizes[0].SkipSize;

                result = key.Length * 8 > size ? key.Substring(0, (size / 8)) : key.PadRight(size / 8, ' ');
            }
            else
                result = key;

            return Encoding.ASCII.GetBytes(result);
        }

        /// <summary>
        /// 	Gets the valid encode IV.
        /// </summary>
        /// <param name="InitVector"> The init vector. </param>
        /// <param name="ValidLength"> Length of the valid. </param>
        /// <returns> </returns>
        private static byte[] GetValidIV(String InitVector, int ValidLength)
        {
            if (InitVector.Length > ValidLength)
                return Encoding.ASCII.GetBytes(InitVector.Substring(0, ValidLength));

            return Encoding.ASCII.GetBytes(InitVector.PadRight(ValidLength, ' '));
        }

        /// <summary>
        /// 	Derives a 256-bit AES key from the configured encryption secret. The secret is expected
        /// 	to be high-entropy key material, so a single SHA-256 pass is used to produce exactly
        /// 	32 bytes regardless of the secret's textual length.
        /// </summary>
        /// <param name="key"> The configured encryption secret. </param>
        /// <returns> A 32-byte (256-bit) key. </returns>
        private static byte[] DeriveAes256Key(string key)
        {
            return SHA256.HashData(Encoding.UTF8.GetBytes(key));
        }

        /// <summary>
        /// 	Encrypts the specified plain text.
        /// </summary>
        /// <param name="text"> The plain text. </param>
        /// <param name="algorithm"> The algorithm. </param>
        /// <returns> </returns>
        private static byte[] EncryptInternal(string text, SymmetricAlgorithm algorithm)
        {
            MemoryStream ms = new MemoryStream();
            CryptoStream encStream = new CryptoStream(ms, algorithm.CreateEncryptor(), CryptoStreamMode.Write);

            StreamWriter sw = new StreamWriter(encStream);
            sw.WriteLine(text);
            sw.Close();
            encStream.Close();

            byte[] buffer = ms.ToArray();
            ms.Close();

            return buffer;
        }

        /// <summary>
        /// 	Decrypts the specified cypher text.
        /// </summary>
        /// <param name="cypherText"> The cypher text. </param>
        /// <param name="algorithm"> The algorithm. </param>
        /// <returns> </returns>
        private static string DecryptInternal(byte[] cypherText, SymmetricAlgorithm algorithm)
        {
            MemoryStream ms = new MemoryStream(cypherText);
            CryptoStream encStream = new CryptoStream(ms, algorithm.CreateDecryptor(), CryptoStreamMode.Read);
            StreamReader sr = new StreamReader(encStream);

            string val = sr.ReadLine();
            sr.Close();
            encStream.Close();
            ms.Close();

            return val;
        }

        /// <summary>
        /// 	Encrypts the specified plain text.
        /// </summary>
        /// <param name="data"> The data. </param>
        /// <param name="algorithm"> The algorithm. </param>
        /// <returns> </returns>
        private static byte[] EncryptDataInternal(byte[] data, SymmetricAlgorithm algorithm)
        {
            MemoryStream ms = new MemoryStream();
            CryptoStream encStream = new CryptoStream(ms, algorithm.CreateEncryptor(), CryptoStreamMode.Write);
            encStream.Write(data, 0, data.Length);
            encStream.Close();

            byte[] buffer = ms.ToArray();

            return buffer;
        }

        /// <summary>
        /// 	Decrypts the specified cypher text.
        /// </summary>
        /// <param name="inputData"> The input data. </param>
        /// <param name="algorithm"> The algorithm. </param>
        /// <returns> </returns>
        private static byte[] DecryptDataInternal(byte[] inputData, SymmetricAlgorithm algorithm)
        {
            MemoryStream ms = new MemoryStream(inputData);
            CryptoStream encStream = new CryptoStream(ms, algorithm.CreateDecryptor(), CryptoStreamMode.Read);
            BinaryReader br = new BinaryReader(encStream);
            List<byte> data = new List<byte>();

            byte[] buffer;
            while ((buffer = br.ReadBytes(2048)).Length > 0)
                data.AddRange(buffer);
            encStream.Close();
            ms.Close();

            return data.ToArray();
        }

        #endregion
    }
}
