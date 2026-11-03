namespace DbExplorer.Core.Connections;

/// <summary>How a connection signs in with Microsoft Entra ID (Azure AD). <see cref="None"/>: not with Entra.</summary>
public enum EntraAuthMode
{
    None,

    /// <summary>A browser window opens for the Microsoft sign-in (MFA, conditional access). The user name is a login hint.</summary>
    Interactive,

    /// <summary>The Windows account of a domain-joined or Entra-joined computer, without a prompt (SQL Server only).</summary>
    Integrated,

    /// <summary>Entra user name and password, no MFA. Microsoft is retiring it; kept for accounts that still allow it.</summary>
    Password,

    /// <summary>Whatever this computer is already signed in with: Azure CLI, Visual Studio, environment variables, managed identity.</summary>
    Default,

    /// <summary>An app registration: client ID as the user, client secret as the password.</summary>
    ServicePrincipal
}

/// <summary>The choices of the connection dialog's "Sign in with" list: one value per row.</summary>
public enum SignInMethod
{
    Password,
    Windows,
    EntraInteractive,
    EntraIntegrated,
    EntraPassword,
    EntraDefault,
    EntraServicePrincipal
}

/// <summary>Maps <see cref="SignInMethod"/> onto a profile's sign-in fields, and says which engines offer what.</summary>
public static class SignInMethods
{
    public const string SqlServerKey = "SqlServer";
    public const string PostgresKey = "Postgres";

    public static SignInMethod Of(ConnectionProfile p) => p.EntraAuth switch
    {
        EntraAuthMode.Interactive => SignInMethod.EntraInteractive,
        EntraAuthMode.Integrated => SignInMethod.EntraIntegrated,
        EntraAuthMode.Password => SignInMethod.EntraPassword,
        EntraAuthMode.Default => SignInMethod.EntraDefault,
        EntraAuthMode.ServicePrincipal => SignInMethod.EntraServicePrincipal,
        _ => p.IntegratedSecurity ? SignInMethod.Windows : SignInMethod.Password
    };

    public static EntraAuthMode EntraModeOf(SignInMethod m) => m switch
    {
        SignInMethod.EntraInteractive => EntraAuthMode.Interactive,
        SignInMethod.EntraIntegrated => EntraAuthMode.Integrated,
        SignInMethod.EntraPassword => EntraAuthMode.Password,
        SignInMethod.EntraDefault => EntraAuthMode.Default,
        SignInMethod.EntraServicePrincipal => EntraAuthMode.ServicePrincipal,
        _ => EntraAuthMode.None
    };

    /// <summary>Sets the profile's sign-in fields for <paramref name="method"/>.</summary>
    public static void Apply(ConnectionProfile p, SignInMethod method)
    {
        p.IntegratedSecurity = method == SignInMethod.Windows;
        p.EntraAuth = EntraModeOf(method);
    }

    /// <summary>
    /// The methods an engine offers, in list order. SQL Server and Azure SQL get every Entra method through the
    /// driver; PostgreSQL (Azure Database for PostgreSQL) gets the ones that work with a token as the password.
    /// </summary>
    public static IReadOnlyList<SignInMethod> For(string? providerKey, bool supportsIntegratedSecurity)
    {
        var list = new List<SignInMethod> { SignInMethod.Password };
        if (supportsIntegratedSecurity) list.Add(SignInMethod.Windows);
        if (string.Equals(providerKey, SqlServerKey, StringComparison.OrdinalIgnoreCase))
            list.AddRange([SignInMethod.EntraInteractive, SignInMethod.EntraIntegrated, SignInMethod.EntraPassword,
                SignInMethod.EntraDefault, SignInMethod.EntraServicePrincipal]);
        else if (string.Equals(providerKey, PostgresKey, StringComparison.OrdinalIgnoreCase))
            list.AddRange([SignInMethod.EntraInteractive, SignInMethod.EntraPassword, SignInMethod.EntraDefault,
                SignInMethod.EntraServicePrincipal]);
        return list;
    }

    public static bool IsEntra(SignInMethod m) => EntraModeOf(m) != EntraAuthMode.None;

    public static string Label(SignInMethod m) => m switch
    {
        SignInMethod.Password => "User name and password",
        SignInMethod.Windows => "Windows account (integrated)",
        SignInMethod.EntraInteractive => "Microsoft Entra ID: browser sign-in (MFA)",
        SignInMethod.EntraIntegrated => "Microsoft Entra ID: this Windows account",
        SignInMethod.EntraPassword => "Microsoft Entra ID: user name and password",
        SignInMethod.EntraDefault => "Microsoft Entra ID: Azure CLI / Visual Studio / managed identity",
        SignInMethod.EntraServicePrincipal => "Microsoft Entra ID: service principal (app)",
        _ => m.ToString()
    };

    /// <summary>One line under the list: what happens on connect.</summary>
    public static string? Hint(SignInMethod m, string? providerKey) => m switch
    {
        SignInMethod.EntraInteractive => string.Equals(providerKey, PostgresKey, StringComparison.OrdinalIgnoreCase)
            ? "A browser window opens on connect. User: the Entra user or group name the server knows (empty: your account)."
            : "A browser window opens on connect, once per session. User (optional): your account, to skip the account picker.",
        SignInMethod.EntraIntegrated => "Signs in as the Windows user of this domain-joined or Entra-joined computer. No prompt.",
        SignInMethod.EntraPassword => "No MFA: works only for accounts that allow it. Prefer browser sign-in.",
        SignInMethod.EntraDefault => string.Equals(providerKey, PostgresKey, StringComparison.OrdinalIgnoreCase)
            ? "Uses `az login`, Visual Studio or a managed identity. User: the Entra user or group name the server knows."
            : "Uses `az login`, Visual Studio, environment variables or a managed identity. No prompt.",
        SignInMethod.EntraServicePrincipal => string.Equals(providerKey, PostgresKey, StringComparison.OrdinalIgnoreCase)
            ? "User: the app's role name on the server. Client ID, secret and directory: from the app registration."
            : "User: the app's client ID. Password: a client secret.",
        _ => null
    };

    /// <summary>Whether the User box means anything for <paramref name="m"/> (and what it is called).</summary>
    public static bool UsesUser(SignInMethod m, string? providerKey) => m switch
    {
        SignInMethod.Windows or SignInMethod.EntraIntegrated => false,
        // SQL Server takes the identity from the token; PostgreSQL needs the role name the token signs in as.
        SignInMethod.EntraDefault => string.Equals(providerKey, PostgresKey, StringComparison.OrdinalIgnoreCase),
        _ => true
    };

    public static bool UsesPassword(SignInMethod m) =>
        m is SignInMethod.Password or SignInMethod.EntraPassword or SignInMethod.EntraServicePrincipal;

    /// <summary>The directory (tenant) box: needed for an app on PostgreSQL, optional for the other Entra methods.</summary>
    public static bool UsesTenant(SignInMethod m) =>
        m is SignInMethod.EntraInteractive or SignInMethod.EntraPassword or SignInMethod.EntraDefault or SignInMethod.EntraServicePrincipal;

    public static string UserLabel(SignInMethod m) => m switch
    {
        SignInMethod.EntraServicePrincipal => "Client ID",
        SignInMethod.EntraInteractive => "User (optional)",
        _ => "User"
    };

    public static string PasswordLabel(SignInMethod m) => m == SignInMethod.EntraServicePrincipal ? "Client secret" : "Password";

    /// <summary>Azure SQL, Azure Synapse, Fabric and Azure Database for PostgreSQL/MySQL host names.</summary>
    public static bool IsAzureHost(string? host)
    {
        var h = host?.Trim().TrimEnd('.').ToLowerInvariant() ?? "";
        var comma = h.IndexOf(',');
        if (comma >= 0) h = h[..comma];
        if (h.StartsWith("tcp:", StringComparison.Ordinal)) h = h[4..];
        return h.EndsWith(".database.windows.net", StringComparison.Ordinal)
               || h.EndsWith(".sql.azuresynapse.net", StringComparison.Ordinal)
               || h.EndsWith(".datawarehouse.fabric.microsoft.com", StringComparison.Ordinal)
               || h.EndsWith(".database.fabric.microsoft.com", StringComparison.Ordinal)
               || h.EndsWith(".postgres.database.azure.com", StringComparison.Ordinal)
               || h.EndsWith(".mysql.database.azure.com", StringComparison.Ordinal)
               || h.EndsWith(".database.usgovcloudapi.net", StringComparison.Ordinal)
               || h.EndsWith(".database.chinacloudapi.cn", StringComparison.Ordinal);
    }
}
