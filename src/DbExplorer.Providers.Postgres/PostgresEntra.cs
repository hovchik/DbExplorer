using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using Azure.Core;
using Azure.Identity;
using DbExplorer.Core.Connections;

namespace DbExplorer.Providers.Postgres;

/// <summary>
/// Microsoft Entra ID sign-in for Azure Database for PostgreSQL: the server takes an Entra access token as the
/// password, and the user name is the Entra user, group or app the server has a role for. Tokens are cached per
/// account for the app's session and renewed five minutes before they expire, so a browser sign-in happens once.
/// </summary>
public static class PostgresEntra
{
    /// <summary>The resource Azure Database for PostgreSQL (and MySQL) tokens are issued for.</summary>
    public const string Scope = "https://ossrdbms-aad.database.windows.net/.default";

    /// <summary>Replaces the Microsoft sign-in in tests: profile → token.</summary>
    public static Func<ConnectionProfile, CancellationToken, ValueTask<AccessToken>>? TokenSourceOverride { get; set; }

    private static readonly TimeSpan RenewBefore = TimeSpan.FromMinutes(5);
    private static readonly ConcurrentDictionary<string, TokenCredential> Credentials = new();
    private static readonly ConcurrentDictionary<string, AccessToken> Tokens = new();
    private static readonly SemaphoreSlim Gate = new(1, 1);

    /// <summary>A valid token for the profile, from the cache or a new sign-in.</summary>
    public static async ValueTask<string> GetTokenAsync(ConnectionProfile p, CancellationToken ct = default)
    {
        var key = CacheKey(p);
        if (Fresh(key) is { } cached) return cached;
        // One sign-in at a time: several connections opening together must not each open a browser window.
        await Gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (Fresh(key) is { } again) return again;
            var token = TokenSourceOverride is { } source
                ? await source(p, ct).ConfigureAwait(false)
                : await Credential(p, key).GetTokenAsync(new TokenRequestContext([Scope], tenantId: Tenant(p)), ct).ConfigureAwait(false);
            Tokens[key] = token;
            return token.Token;
        }
        catch (Exception ex) when (ex is AuthenticationFailedException or CredentialUnavailableException)
        {
            throw new EntraSignInException(Explain(p, ex), ex);
        }
        finally { Gate.Release(); }
    }

    /// <summary>Blocking form for code that builds a connection string; never called on the UI thread.</summary>
    public static string GetToken(ConnectionProfile p) =>
        Task.Run(async () => await GetTokenAsync(p).ConfigureAwait(false)).GetAwaiter().GetResult();

    /// <summary>
    /// The role to sign in as: the Entra user (user@contoso.com), group or app name the server has a role for.
    /// Asked for up front, so opening a pool never has to wait for a sign-in to learn it.
    /// </summary>
    public static string UserFor(ConnectionProfile p) =>
        string.IsNullOrWhiteSpace(p.UserName)
            ? throw new EntraSignInException("Enter the user: the Entra user (name@company.com), group or app name the PostgreSQL server has a role for.")
            : p.UserName.Trim();

    /// <summary>Forgets cached tokens (sign out, or after the server refused one).</summary>
    public static void Forget(ConnectionProfile p) => Tokens.TryRemove(CacheKey(p), out _);

    private static string? Fresh(string key) =>
        Tokens.TryGetValue(key, out var t) && t.ExpiresOn - DateTimeOffset.UtcNow > RenewBefore ? t.Token : null;

    private static string? Tenant(ConnectionProfile p) => string.IsNullOrWhiteSpace(p.EntraTenantId) ? null : p.EntraTenantId.Trim();

    /// <summary>One credential per account and method; the secret is part of the key only as a hash.</summary>
    private static string CacheKey(ConnectionProfile p)
    {
        var secret = p.EntraAuth is EntraAuthMode.Password or EntraAuthMode.ServicePrincipal && !string.IsNullOrEmpty(p.Password)
            ? Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(p.Password!)))[..16]
            : "";
        return $"{p.EntraAuth}|{Tenant(p)}|{p.UserName?.Trim().ToLowerInvariant()}|{p.EntraClientId?.Trim()}|{secret}";
    }

    private static TokenCredential Credential(ConnectionProfile p, string key) => Credentials.GetOrAdd(key, _ =>
    {
        var tenant = Tenant(p);
        var user = string.IsNullOrWhiteSpace(p.UserName) ? null : p.UserName.Trim();
        return p.EntraAuth switch
        {
            EntraAuthMode.Interactive => new InteractiveBrowserCredential(new InteractiveBrowserCredentialOptions
            {
                TenantId = tenant,
                LoginHint = user is not null && user.Contains('@') ? user : null
            }),
#pragma warning disable CS0618 // Microsoft is retiring the password flow; it is offered for accounts that still allow it.
            EntraAuthMode.Password => new UsernamePasswordCredential(
                user ?? throw new EntraSignInException("Enter the Entra user name."), p.Password ?? "",
                tenant ?? "organizations", AzureCliClientId),
#pragma warning restore CS0618
            EntraAuthMode.ServicePrincipal => new ClientSecretCredential(
                tenant ?? throw new EntraSignInException("Enter the directory (tenant) ID: a service principal signs in to one directory."),
                string.IsNullOrWhiteSpace(p.EntraClientId) ? throw new EntraSignInException("Enter the app's client ID.") : p.EntraClientId.Trim(),
                p.Password ?? ""),
            EntraAuthMode.Default => new DefaultAzureCredential(new DefaultAzureCredentialOptions
            {
                TenantId = tenant,
                ExcludeInteractiveBrowserCredential = true
            }),
            _ => throw new EntraSignInException($"PostgreSQL does not support the Entra method {p.EntraAuth}.")
        };
    });

    /// <summary>The public client the Azure CLI signs in with; Azure Database for PostgreSQL accepts its tokens.</summary>
    private const string AzureCliClientId = "04b07795-8ddb-461a-bbee-02f9e1bf7b46";

    private static string Explain(ConnectionProfile p, Exception ex) => p.EntraAuth switch
    {
        EntraAuthMode.Default =>
            "No Microsoft sign-in found on this computer. Run `az login` (Azure CLI), sign in to Visual Studio, or pick browser sign-in. " + ex.Message,
        EntraAuthMode.Interactive when ex.Message.Contains("cancel", StringComparison.OrdinalIgnoreCase) =>
            "The Microsoft sign-in was cancelled.",
        _ => "Microsoft Entra sign-in failed: " + ex.Message
    };
}
