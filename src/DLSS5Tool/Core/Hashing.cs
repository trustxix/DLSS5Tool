using System.Security.Cryptography;

namespace DLSS5Tool.Core;

public static class Hashing
{
    public static string Sha256File(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 16);
        return Convert.ToHexStringLower(SHA256.HashData(fs));
    }

    public static string Sha256(Stream stream)
    {
        return Convert.ToHexStringLower(SHA256.HashData(stream));
    }

    public static string Sha256(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    public static bool Matches(string path, string expectedSha256)
    {
        if (!File.Exists(path)) return false;
        return string.Equals(Sha256File(path), expectedSha256, StringComparison.OrdinalIgnoreCase);
    }
}
