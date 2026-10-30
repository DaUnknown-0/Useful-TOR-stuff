// Password verifier shared by the standalone HostGuard gates. No Unity or account data.
using System;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using Konscious.Security.Cryptography;

namespace HostGuardSecurity;

internal static class HostPasswordHash
{
    internal const int MemoryKiB = 19456, Iterations = 2, Parallelism = 1;
    private sealed class Record
    {
        internal byte[] Salt, Expected;
        internal int Memory, Passes, Lanes;
        internal bool Legacy;
    }

    internal static bool IsSupported(string encoded) => TryRead(encoded, out _);
    internal static string Normalize(string encoded) => encoded?.Length == 64 ? encoded.ToLowerInvariant() : encoded;

    private static bool TryRead(string encoded, out Record record)
    {
        record = null;
        if (string.IsNullOrEmpty(encoded) || encoded.Length > 256) return false;
        if (encoded.Length == 64 && encoded.All(Uri.IsHexDigit))
        {
            record = new Record { Legacy = true, Expected = Convert.FromHexString(encoded) };
            return true;
        }
        var fields = encoded.Split('$');
        if (fields.Length != 6 || fields[0] != "" || fields[1] != "argon2id" || fields[2] != "v=19") return false;
        var parameters = fields[3].Split(',');
        if (parameters.Length != 3 || !ReadParameter(parameters[0], "m=", out int memory)
            || !ReadParameter(parameters[1], "t=", out int passes) || !ReadParameter(parameters[2], "p=", out int lanes)) return false;
        // Remote records cannot request unbounded memory or work on a game client.
        if (memory < MemoryKiB || memory > 65536 || passes < Iterations || passes > 6 || lanes < 1 || lanes > 4) return false;
        try
        {
            byte[] salt = Decode(fields[4]), expected = Decode(fields[5]);
            if (salt.Length < 16 || salt.Length > 64 || expected.Length < 32 || expected.Length > 64) return false;
            record = new Record { Salt = salt, Expected = expected, Memory = memory, Passes = passes, Lanes = lanes };
            return true;
        }
        catch (FormatException) { return false; }
    }

    private static bool ReadParameter(string field, string prefix, out int value)
    {
        value = 0;
        return field.StartsWith(prefix, StringComparison.Ordinal)
            && int.TryParse(field.Substring(prefix.Length), NumberStyles.None, CultureInfo.InvariantCulture, out value);
    }

    private static byte[] Decode(string value)
    {
        if (value.Length > 88 || value.Any(c => !((c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9') || c == '+' || c == '/'))) throw new FormatException();
        return Convert.FromBase64String(value + new string('=', (4 - value.Length % 4) % 4));
    }

    internal static Task<bool> VerifyAsync(string password, string encoded) => Task.Run(() => Verify(password, encoded));

    internal static bool Verify(string password, string encoded)
    {
        if (password == null || Encoding.UTF8.GetByteCount(password) > 1024 || !TryRead(encoded, out var record)) return false;
        byte[] bytes = Encoding.UTF8.GetBytes(password), actual = null;
        try
        {
            if (record.Legacy) actual = SHA256.HashData(bytes);
            else
            {
                using var argon = new Argon2id(bytes) { Salt = record.Salt, MemorySize = record.Memory, Iterations = record.Passes, DegreeOfParallelism = record.Lanes };
                actual = argon.GetBytes(record.Expected.Length);
            }
            return CryptographicOperations.FixedTimeEquals(actual, record.Expected);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
            if (actual != null) CryptographicOperations.ZeroMemory(actual);
        }
    }

    internal static string Create(string password)
    {
        if (string.IsNullOrEmpty(password) || Encoding.UTF8.GetByteCount(password) > 1024) throw new ArgumentException("Password length is outside the supported range.");
        byte[] bytes = Encoding.UTF8.GetBytes(password), salt = RandomNumberGenerator.GetBytes(16), derived = null;
        try
        {
            using var argon = new Argon2id(bytes) { Salt = salt, MemorySize = MemoryKiB, Iterations = Iterations, DegreeOfParallelism = Parallelism };
            derived = argon.GetBytes(32);
            return "$argon2id$v=19$m=" + MemoryKiB + ",t=" + Iterations + ",p=" + Parallelism + "$"
                + Convert.ToBase64String(salt).TrimEnd('=') + "$" + Convert.ToBase64String(derived).TrimEnd('=');
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
            if (derived != null) CryptographicOperations.ZeroMemory(derived);
        }
    }
}
