using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;

namespace DbExplorer.Application.Connections;

public interface ISecretProtector
{
    bool IsSupported { get; }
    string Protect(string plainText);
    string? Unprotect(string protectedText);
}

/// <summary>Windows DPAPI: the password can only be decrypted by the same Windows user.</summary>
[SupportedOSPlatform("windows")]
public sealed class DpapiSecretProtector : ISecretProtector
{
    private static readonly byte[] Entropy = "DbExplorer.v1"u8.ToArray();

    public bool IsSupported => true;

    public string Protect(string plainText) =>
        Convert.ToBase64String(ProtectedData.Protect(
            Encoding.UTF8.GetBytes(plainText), Entropy, DataProtectionScope.CurrentUser));

    public string? Unprotect(string protectedText)
    {
        try
        {
            return Encoding.UTF8.GetString(ProtectedData.Unprotect(
                Convert.FromBase64String(protectedText), Entropy, DataProtectionScope.CurrentUser));
        }
        catch (Exception ex) when (ex is CryptographicException or FormatException)
        {
            return null;
        }
    }
}

/// <summary>Used where no OS secret store is wired up: passwords are simply not persisted.</summary>
public sealed class NoSecretProtector : ISecretProtector
{
    public bool IsSupported => false;
    public string Protect(string plainText) => throw new NotSupportedException();
    public string? Unprotect(string protectedText) => null;
}
