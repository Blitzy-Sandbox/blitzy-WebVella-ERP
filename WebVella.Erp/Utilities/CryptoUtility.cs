#region <--- DIRECTIVES --->
using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;

#endregion

namespace WebVella.Erp.Utilities
{
    /// <summary>
    /// Cryptographic utility class providing encryption, decryption, and hashing functionality.
    /// SECURITY: Updated to address CWE-798 (hard-coded credentials) and CWE-329 (predictable IV).
    /// </summary>
    public class CryptoUtility
    {
        #region <--- Fields --->

        // SECURITY FIX: Removed hard-coded defaultCryptKey constant (CWE-798 mitigation)
        // The encryption key MUST now be configured via ErpSettings.EncryptionKey
        private static string cryptKey;

        #endregion

        #region <--- Properties --->

        /// <summary>
        /// Gets the configured encryption key.
        /// SECURITY: No longer falls back to a default key - configuration is required.
        /// </summary>
        /// <exception cref="InvalidOperationException">
        /// Thrown when encryption key is not configured in ErpSettings.
        /// </exception>
        public static string CryptKey
        {
            get
            {
                if (string.IsNullOrEmpty(cryptKey))
                {
                    // SECURITY FIX: Remove default key fallback - CWE-798 mitigation
                    if (string.IsNullOrWhiteSpace(ErpSettings.EncryptionKey))
                    {
                        throw new InvalidOperationException(
                            "SECURITY ERROR: Encryption key is not configured. " +
                            "Set 'Settings:EncryptionKey' in configuration with a cryptographically random value. " +
                            "Generate a secure key using: openssl rand -base64 32");
                    }
                    cryptKey = ErpSettings.EncryptionKey;
                }
                return cryptKey;
            }
        }

        #endregion

        #region <--- Key Derivation --->

        /// <summary>
        /// Derives a cryptographic key from password using PBKDF2.
        /// SECURITY: Uses 10,000 iterations per NIST SP 800-132 recommendation.
        /// </summary>
        /// <param name="password">The password to derive key from</param>
        /// <param name="salt">Random salt (should be stored with ciphertext)</param>
        /// <param name="keySize">Required key size in bytes</param>
        /// <returns>Derived key bytes</returns>
        private static byte[] DeriveKey(string password, byte[] salt, int keySize)
        {
            // SECURITY: 10,000 iterations minimum per NIST SP 800-132
            using (var pbkdf2 = new Rfc2898DeriveBytes(password, salt, 10000, HashAlgorithmName.SHA256))
            {
                return pbkdf2.GetBytes(keySize);
            }
        }

        /// <summary>
        /// Generates a cryptographically random salt.
        /// </summary>
        /// <param name="size">Salt size in bytes (default 16)</param>
        /// <returns>Random salt bytes</returns>
        private static byte[] GenerateRandomSalt(int size = 16)
        {
            byte[] salt = new byte[size];
            using (var rng = RandomNumberGenerator.Create())
            {
                rng.GetBytes(salt);
            }
            return salt;
        }

        /// <summary>
        /// Generates a cryptographically random IV.
        /// SECURITY: IV must be unique per encryption operation to prevent pattern analysis (CWE-329).
        /// </summary>
        /// <param name="size">IV size in bytes</param>
        /// <returns>Random IV bytes</returns>
        private static byte[] GenerateRandomIV(int size)
        {
            byte[] iv = new byte[size];
            using (var rng = RandomNumberGenerator.Create())
            {
                rng.GetBytes(iv);
            }
            return iv;
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
        /// Encrypts the text using the specified key.
        /// SECURITY: Uses PBKDF2 key derivation with random salt and random IV (CWE-329 mitigation).
        /// Output format: Base64(salt[16] + IV[blockSize] + ciphertext)
        /// </summary>
        /// <param name="text"> The text. </param>
        /// <param name="key"> The key. </param>
        /// <param name="algorithm"> The algorithm. </param>
        /// <returns>Base64 encoded string containing salt, IV, and ciphertext</returns>
        public static string EncryptText(string text, string key, SymmetricAlgorithm algorithm)
        {
            // SECURITY FIX: Generate random salt and IV per operation - CWE-329 mitigation
            byte[] salt = GenerateRandomSalt(16);
            byte[] iv = GenerateRandomIV(algorithm.BlockSize / 8);

            // Derive key using PBKDF2 instead of weak truncation
            algorithm.Key = DeriveKey(key, salt, algorithm.KeySize / 8);
            algorithm.IV = iv;

            byte[] encryptedData = EncryptInternal(text, algorithm);

            // Prepend salt and IV to ciphertext (salt + IV + ciphertext)
            // Salt and IV are not secret and are needed for decryption
            byte[] result = new byte[salt.Length + iv.Length + encryptedData.Length];
            Buffer.BlockCopy(salt, 0, result, 0, salt.Length);
            Buffer.BlockCopy(iv, 0, result, salt.Length, iv.Length);
            Buffer.BlockCopy(encryptedData, 0, result, salt.Length + iv.Length, encryptedData.Length);

            return Convert.ToBase64String(result);
        }

        /// <summary>
        /// Decrypts the text using the specified key.
        /// SECURITY: Extracts salt and IV from ciphertext for PBKDF2 key derivation.
        /// Expected input format: Base64(salt[16] + IV[blockSize] + ciphertext)
        /// </summary>
        /// <param name="cypherText"> The cypher text (Base64 encoded). </param>
        /// <param name="key"> The key. </param>
        /// <param name="algorithm"> The algorithm. </param>
        /// <returns>Decrypted plaintext</returns>
        /// <exception cref="ArgumentException">Thrown when ciphertext format is invalid</exception>
        public static string DecryptText(string cypherText, string key, SymmetricAlgorithm algorithm)
        {
            byte[] inputBuffer = Convert.FromBase64String(cypherText);

            // SECURITY FIX: Extract salt and IV from prepended data
            int saltSize = 16;
            int ivSize = algorithm.BlockSize / 8;

            if (inputBuffer.Length < saltSize + ivSize)
            {
                throw new ArgumentException("Invalid ciphertext format - data too short to contain salt and IV");
            }

            byte[] salt = new byte[saltSize];
            byte[] iv = new byte[ivSize];
            byte[] ciphertext = new byte[inputBuffer.Length - saltSize - ivSize];

            Buffer.BlockCopy(inputBuffer, 0, salt, 0, saltSize);
            Buffer.BlockCopy(inputBuffer, saltSize, iv, 0, ivSize);
            Buffer.BlockCopy(inputBuffer, saltSize + ivSize, ciphertext, 0, ciphertext.Length);

            // Derive key using same PBKDF2 parameters
            algorithm.Key = DeriveKey(key, salt, algorithm.KeySize / 8);
            algorithm.IV = iv;

            return DecryptInternal(ciphertext, algorithm);
        }

        /// <summary>
        /// Encrypts the data using the specified key.
        /// SECURITY: Uses PBKDF2 key derivation with random salt and random IV (CWE-329 mitigation).
        /// Output format: salt[16] + IV[blockSize] + ciphertext
        /// </summary>
        /// <param name="data"> The data. </param>
        /// <param name="key"> The key. </param>
        /// <param name="algorithm"> The algorithm. </param>
        /// <returns>Byte array containing salt, IV, and ciphertext</returns>
        public static byte[] EncryptData(byte[] data, string key, SymmetricAlgorithm algorithm)
        {
            // SECURITY FIX: Generate random salt and IV per operation - CWE-329 mitigation
            byte[] salt = GenerateRandomSalt(16);
            byte[] iv = GenerateRandomIV(algorithm.BlockSize / 8);

            // Derive key using PBKDF2 instead of weak truncation
            algorithm.Key = DeriveKey(key, salt, algorithm.KeySize / 8);
            algorithm.IV = iv;

            byte[] encryptedData = EncryptDataInternal(data, algorithm);

            // Prepend salt and IV to ciphertext (salt + IV + ciphertext)
            // Salt and IV are not secret and are needed for decryption
            byte[] result = new byte[salt.Length + iv.Length + encryptedData.Length];
            Buffer.BlockCopy(salt, 0, result, 0, salt.Length);
            Buffer.BlockCopy(iv, 0, result, salt.Length, iv.Length);
            Buffer.BlockCopy(encryptedData, 0, result, salt.Length + iv.Length, encryptedData.Length);

            return result;
        }

        /// <summary>
        /// Decrypts the data using the specified key.
        /// SECURITY: Extracts salt and IV from ciphertext for PBKDF2 key derivation.
        /// Expected input format: salt[16] + IV[blockSize] + ciphertext
        /// </summary>
        /// <param name="data"> The data. </param>
        /// <param name="key"> The key. </param>
        /// <param name="algorithm"> The algorithm. </param>
        /// <returns>Decrypted data</returns>
        /// <exception cref="ArgumentException">Thrown when data format is invalid</exception>
        public static byte[] DecryptData(byte[] data, string key, SymmetricAlgorithm algorithm)
        {
            // SECURITY FIX: Extract salt and IV from prepended data
            int saltSize = 16;
            int ivSize = algorithm.BlockSize / 8;

            if (data.Length < saltSize + ivSize)
            {
                throw new ArgumentException("Invalid data format - data too short to contain salt and IV");
            }

            byte[] salt = new byte[saltSize];
            byte[] iv = new byte[ivSize];
            byte[] ciphertext = new byte[data.Length - saltSize - ivSize];

            Buffer.BlockCopy(data, 0, salt, 0, saltSize);
            Buffer.BlockCopy(data, saltSize, iv, 0, ivSize);
            Buffer.BlockCopy(data, saltSize + ivSize, ciphertext, 0, ciphertext.Length);

            // Derive key using same PBKDF2 parameters
            algorithm.Key = DeriveKey(key, salt, algorithm.KeySize / 8);
            algorithm.IV = iv;

            return DecryptDataInternal(ciphertext, algorithm);
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
        /// DEPRECATED: Kept for backward compatibility with legacy encrypted data only.
        /// New encryption uses DeriveKey() with PBKDF2 instead.
        /// </summary>
        /// <param name="key"> The key. </param>
        /// <param name="encodeMethod"> The encode method. </param>
        /// <returns> </returns>
        [Obsolete("Use DeriveKey() for new encryption. This method uses weak key truncation/padding.")]
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
        /// Gets the valid encode IV.
        /// DEPRECATED: This method derived IV from key which is insecure (CWE-329).
        /// Kept for backward compatibility with legacy encrypted data only.
        /// New encryption uses GenerateRandomIV() instead.
        /// </summary>
        /// <param name="InitVector"> The init vector. </param>
        /// <param name="ValidLength"> Length of the valid. </param>
        /// <returns> </returns>
        [Obsolete("Use GenerateRandomIV() instead. IV derived from key is insecure (CWE-329).")]
        private static byte[] GetValidIV(String InitVector, int ValidLength)
        {
            // Legacy implementation kept for backward compatibility with existing encrypted data
            if (InitVector.Length > ValidLength)
                return Encoding.ASCII.GetBytes(InitVector.Substring(0, ValidLength));

            return Encoding.ASCII.GetBytes(InitVector.PadRight(ValidLength, ' '));
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
