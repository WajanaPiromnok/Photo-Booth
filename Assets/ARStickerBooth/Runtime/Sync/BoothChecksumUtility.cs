using System;
using System.IO;
using System.Security.Cryptography;

namespace PhotoBooth.Booth.Sync
{
    public static class BoothChecksumUtility
    {
        public static string ComputeSha256Tag(string filePath)
        {
            if (string.IsNullOrWhiteSpace(filePath))
            {
                throw new ArgumentException("File path is required.", nameof(filePath));
            }

            using var stream = File.OpenRead(filePath);
            using var sha256 = SHA256.Create();
            var hashBytes = sha256.ComputeHash(stream);
            return "sha256:" + BitConverter.ToString(hashBytes).Replace("-", string.Empty).ToLowerInvariant();
        }
    }
}
