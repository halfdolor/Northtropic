using System;
using System.Security.Cryptography;

namespace Northtropic.Helpers
{
    /// <summary>
    /// 工业级密码哈希与安全核验工具
    /// 采用 PBKDF2-SHA256，内置 128-bit 独立密码学随机盐与 100,000 轮哈希迭代。
    /// 支持常数时间比对（防计时攻击），并提供对历史明文密码的无感识别与平滑升级能力。
    /// </summary>
    public static class PasswordHasher
    {
        private const int SaltSize = 16; // 128 bits
        private const int KeySize = 32;  // 256 bits
        private const int Iterations = 100_000;
        private static readonly HashAlgorithmName HashAlgorithm = HashAlgorithmName.SHA256;
        private const char Delimiter = ':';
        private const string FormatHeader = "PBKDF2";

        /// <summary>
        /// 对原始密码进行加盐哈希计算
        /// 格式：PBKDF2:{Algorithm}:{Iterations}:{SaltHex}:{HashHex}
        /// </summary>
        public static string HashPassword(string password)
        {
            if (password == null) throw new ArgumentNullException(nameof(password));

            byte[] salt = RandomNumberGenerator.GetBytes(SaltSize);
            byte[] hash = Rfc2898DeriveBytes.Pbkdf2(
                password,
                salt,
                Iterations,
                HashAlgorithm,
                KeySize);

            return $"{FormatHeader}{Delimiter}{HashAlgorithm.Name}{Delimiter}{Iterations}{Delimiter}{Convert.ToHexString(salt)}{Delimiter}{Convert.ToHexString(hash)}";
        }

        /// <summary>
        /// 核验输入的密码是否与存储的密码（哈希或历史明文）匹配
        /// </summary>
        public static bool VerifyPassword(string enteredPassword, string storedPassword, out bool needsRehash)
        {
            needsRehash = false;
            if (string.IsNullOrEmpty(enteredPassword) || string.IsNullOrEmpty(storedPassword))
            {
                return false;
            }

            // 1. 判断是否为已哈希的密码格式
            if (storedPassword.StartsWith(FormatHeader + Delimiter))
            {
                string[] parts = storedPassword.Split(Delimiter);
                if (parts.Length == 5)
                {
                    string algoName = parts[1];
                    if (int.TryParse(parts[2], out int iterations) && iterations > 0)
                    {
                        try
                        {
                            byte[] salt = Convert.FromHexString(parts[3]);
                            byte[] storedHash = Convert.FromHexString(parts[4]);

                            var algorithm = new HashAlgorithmName(algoName);
                            byte[] inputHash = Rfc2898DeriveBytes.Pbkdf2(
                                enteredPassword,
                                salt,
                                iterations,
                                algorithm,
                                storedHash.Length);

                            // 使用常数时间安全比对，防止侧信道计时攻击
                            return CryptographicOperations.FixedTimeEquals(inputHash, storedHash);
                        }
                        catch
                        {
                            return false;
                        }
                    }
                }
            }

            // 2. 历史明文兼容分支：如果不是哈希串，直接与旧明文进行安全比对
            if (string.Equals(enteredPassword, storedPassword, StringComparison.Ordinal))
            {
                // 成功验证了旧明文密码，标记需要立即升级为哈希存储！
                needsRehash = true;
                return true;
            }

            return false;
        }

        /// <summary>
        /// 核验输入的密码是否与存储的密码匹配（简化重载）
        /// </summary>
        public static bool VerifyPassword(string enteredPassword, string storedPassword)
        {
            return VerifyPassword(enteredPassword, storedPassword, out _);
        }

        /// <summary>
        /// 检查某密码字符串是否已经是标准哈希格式
        /// </summary>
        public static bool IsHashed(string? password)
        {
            return !string.IsNullOrEmpty(password) && password.StartsWith(FormatHeader + Delimiter);
        }
    }
}
