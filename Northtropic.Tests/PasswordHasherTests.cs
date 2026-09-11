using Northtropic.Helpers;
using Xunit;

namespace Northtropic.Tests
{
    public class PasswordHasherTests
    {
        [Fact]
        public void HashPassword_ShouldGenerateStandardFormat()
        {
            string raw = "Admin@123456";
            string hash = PasswordHasher.HashPassword(raw);

            Assert.NotNull(hash);
            Assert.StartsWith("PBKDF2:SHA256:100000:", hash);
            Assert.True(PasswordHasher.IsHashed(hash));
        }

        [Fact]
        public void VerifyPassword_WithCorrectPassword_ShouldReturnTrue()
        {
            string raw = "SecretP@ssw0rd!#";
            string hash = PasswordHasher.HashPassword(raw);

            bool verified = PasswordHasher.VerifyPassword(raw, hash, out bool needsRehash);

            Assert.True(verified);
            Assert.False(needsRehash); // Already up-to-date PBKDF2
        }

        [Fact]
        public void VerifyPassword_WithWrongPassword_ShouldReturnFalse()
        {
            string raw = "SecretP@ssw0rd!#";
            string hash = PasswordHasher.HashPassword(raw);

            bool verified = PasswordHasher.VerifyPassword("WrongPassword123", hash, out bool needsRehash);

            Assert.False(verified);
            Assert.False(needsRehash);
        }

        [Fact]
        public void VerifyPassword_WithLegacyPlainText_ShouldVerifyAndFlagRehash()
        {
            string legacyPlain = "123456";

            bool verified = PasswordHasher.VerifyPassword("123456", legacyPlain, out bool needsRehash);

            Assert.True(verified);
            Assert.True(needsRehash); // Needs upgrade to PBKDF2
        }

        [Fact]
        public void VerifyPassword_WithTamperedHash_ShouldReturnFalse()
        {
            string raw = "MySecureKey";
            string hash = PasswordHasher.HashPassword(raw);

            // Tamper the last character
            string tampered = hash.Substring(0, hash.Length - 1) + (hash[^1] == '0' ? '1' : '0');

            bool verified = PasswordHasher.VerifyPassword(raw, tampered, out _);

            Assert.False(verified);
        }
    }
}
