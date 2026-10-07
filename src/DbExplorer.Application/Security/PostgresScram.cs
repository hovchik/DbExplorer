using System.Security.Cryptography;
using System.Text;

namespace DbExplorer.Application.Security;

/// <summary>
/// Turns a password into the SCRAM-SHA-256 verifier PostgreSQL stores (RFC 5802/7677, the format of pg_authid), so
/// CREATE/ALTER ROLE … PASSWORD sends the verifier and the plain password never reaches the server, its statement log
/// or pg_stat_statements. PostgreSQL 10 and later accept a verifier in place of a password.
/// </summary>
public static class PostgresScram
{
    public const int Iterations = 4096;

    public static string Verifier(string password, byte[]? salt = null, int iterations = Iterations)
    {
        salt ??= RandomNumberGenerator.GetBytes(16);
        // SASLprep is mostly NFKC normalization; ASCII passwords are unchanged by it.
        var bytes = Encoding.UTF8.GetBytes(password.Normalize(NormalizationForm.FormKC));
        var salted = Rfc2898DeriveBytes.Pbkdf2(bytes, salt, iterations, HashAlgorithmName.SHA256, 32);
        var clientKey = HMACSHA256.HashData(salted, "Client Key"u8);
        var storedKey = SHA256.HashData(clientKey);
        var serverKey = HMACSHA256.HashData(salted, "Server Key"u8);
        CryptographicOperations.ZeroMemory(salted);
        CryptographicOperations.ZeroMemory(bytes);
        return $"SCRAM-SHA-256${iterations}:{Convert.ToBase64String(salt)}${Convert.ToBase64String(storedKey)}:{Convert.ToBase64String(serverKey)}";
    }
}
