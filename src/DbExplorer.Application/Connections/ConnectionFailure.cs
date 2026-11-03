using System.ComponentModel;
using System.Data.Common;
using System.Net.Sockets;
using DbExplorer.Application.Connections.Ssh;
using DbExplorer.Core.Connections;

namespace DbExplorer.Application.Connections;

/// <summary>Why a connection could not be opened, in the terms a user can act on.</summary>
public enum ConnectionFailureKind
{
    /// <summary>The host name could not be resolved (typo, VPN off, DNS).</summary>
    HostNotFound,

    /// <summary>The machine answered, but nothing accepts connections on that port.</summary>
    Refused,

    /// <summary>No answer in time: firewall, server down, wrong network.</summary>
    Timeout,

    /// <summary>The server rejected the user name or password.</summary>
    Login,

    /// <summary>The server does not let this user in from this machine (pg_hba.conf, MySQL host grants).</summary>
    NotAllowed,

    /// <summary>TLS: certificate not trusted, or the server and the connection disagree on encryption.</summary>
    Tls,

    /// <summary>The database named in the connection does not exist or this user cannot open it.</summary>
    DatabaseMissing,

    /// <summary>The SSH tunnel step failed; its message already says why.</summary>
    Ssh,

    /// <summary>Anything else: the driver's text is all there is.</summary>
    Other
}

/// <summary>
/// A failed connect, sorted into a <see cref="Kind"/> with a short <see cref="Title"/>, one line of
/// <see cref="Advice"/> and the driver's own text in <see cref="Detail"/>.
/// </summary>
public sealed record ConnectionFailure(ConnectionFailureKind Kind, string Title, string Advice, string Detail)
{
    /// <summary>"Server not found. Check …" followed by the driver's text, for the status line and the log.</summary>
    public string Summary => string.IsNullOrEmpty(Advice) ? $"{Title}: {Detail}" : $"{Title}. {Advice} ({Detail})";

    /// <summary>
    /// Sorts <paramref name="error"/> (and its inner exceptions) into a kind. <paramref name="profile"/>, when given,
    /// puts the host, port, user and database into the advice.
    /// </summary>
    public static ConnectionFailure Classify(Exception error, ConnectionProfile? profile = null)
    {
        var detail = DetailOf(error);
        var kind = KindOf(error);
        var host = string.IsNullOrWhiteSpace(profile?.Host) ? "the server" : $"\"{profile!.Host.Trim()}\"";
        var endpoint = profile is null || string.IsNullOrWhiteSpace(profile.Host)
            ? "the server"
            : profile.Port > 0 ? $"{profile.Host.Trim()}:{profile.Port}" : profile.Host.Trim();
        var user = string.IsNullOrWhiteSpace(profile?.UserName) ? "this user" : $"\"{profile!.UserName}\"";
        var database = string.IsNullOrWhiteSpace(profile?.Database) ? "The database" : $"Database \"{profile!.Database}\"";
        var integrated = profile?.IntegratedSecurity == true;
        var entra = profile?.EntraAuth ?? EntraAuthMode.None;

        return kind switch
        {
            ConnectionFailureKind.HostNotFound => new(kind, "Server not found",
                $"No computer called {host} could be found. Check the host name for typos, or connect to the VPN if the server is on a private network.", detail),
            ConnectionFailureKind.Refused => new(kind, "Connection refused",
                $"{endpoint} answered, but nothing is accepting connections on that port. Check the port, and that the database server is running and accepts network connections.", detail),
            ConnectionFailureKind.Timeout => new(kind, "No answer from the server",
                $"{endpoint} did not answer in time. A firewall may be blocking the port, the server may be down, or you may need a VPN or an SSH tunnel.", detail),
            ConnectionFailureKind.Login when entra != EntraAuthMode.None || EntraError(error) is not null => new(kind, "Microsoft sign-in failed",
                EntraError(error)?.Message ?? EntraAdvice(entra, user), detail),
            ConnectionFailureKind.Login => new(kind, "Sign-in failed",
                integrated
                    ? "The server did not accept your Windows account. Check that this login exists on the server, or switch to a user name and password."
                    : $"The server rejected {user}. Check the user name and password.", detail),
            ConnectionFailureKind.NotAllowed => new(kind, "Not allowed from this computer",
                $"The server does not let {user} connect from this machine. An administrator has to allow it (pg_hba.conf on PostgreSQL, the user's host on MySQL), or connect through an SSH tunnel.", detail),
            ConnectionFailureKind.Tls => new(kind, "Encryption problem",
                ConnectionSecurity.ExplainFailure(error) ?? "The encrypted connection could not be set up. Check the Encrypt and Trust certificate settings.", detail),
            ConnectionFailureKind.DatabaseMissing => new(kind, "Database not found",
                $"{database} does not exist on the server, or {user} may not open it. Pick one with the database list in the connection settings.", detail),
            ConnectionFailureKind.Ssh => new(kind, "SSH tunnel failed", "", detail),
            _ => new(kind, "Connection failed", "", detail)
        };
    }

    private static EntraSignInException? EntraError(Exception error)
    {
        for (var e = error; e is not null; e = e.InnerException)
            if (e is EntraSignInException entra) return entra;
        return null;
    }

    private static string EntraAdvice(EntraAuthMode mode, string user) => mode switch
    {
        EntraAuthMode.Interactive => $"The server did not accept the Microsoft account. Check that {user} (or a group it is in) has been added to the database as an Entra user, and that you picked the right account in the browser.",
        EntraAuthMode.Integrated => "The server did not accept this Windows account through Microsoft Entra. The computer must be joined to the directory, and the account added to the database as an Entra user. Browser sign-in works on any computer.",
        EntraAuthMode.Password => $"Microsoft did not accept {user} with this password. Accounts with MFA can't use it: switch to browser sign-in.",
        EntraAuthMode.Default => "No usable Microsoft sign-in was found on this computer, or the server did not accept it. Run `az login`, or pick browser sign-in.",
        EntraAuthMode.ServicePrincipal => "Microsoft did not accept the app. Check the client ID, the client secret (it may have expired) and the directory ID, and that the app was added to the database as a user.",
        _ => "The server did not accept the Microsoft sign-in."
    };

    /// <summary>The most useful message in the chain: the SSH explanation, or the outermost driver message.</summary>
    private static string DetailOf(Exception error)
    {
        for (var e = error; e is not null; e = e.InnerException)
            if (e is SshTunnelException) return e.Message;
        var message = error.Message.Trim();
        // Npgsql wraps socket errors in "Exception while connecting"; the inner message is the one that says something.
        if (error.InnerException is { } inner && (message.Length < 40 || message.StartsWith("Exception while", StringComparison.OrdinalIgnoreCase)))
            return $"{message} {inner.Message}".Trim();
        return message;
    }

    private static ConnectionFailureKind KindOf(Exception error)
    {
        var chain = new List<Exception>();
        for (var e = error; e is not null && chain.Count < 10; e = e.InnerException) chain.Add(e);

        if (chain.Any(e => e is SshTunnelException)) return ConnectionFailureKind.Ssh;
        if (chain.Any(e => e is EntraSignInException)) return ConnectionFailureKind.Login;

        // Codes first: they do not depend on the server's language.
        foreach (var e in chain)
        {
            if (e is DbException db && db.SqlState is { Length: > 0 } state)
            {
                // PostgreSQL (and MySQL, which uses 28000 for "access denied").
                if (state is "28P01") return ConnectionFailureKind.Login;
                if (state is "28000")
                    return Mentions(e.Message, "pg_hba.conf", "no pg_hba", "is not allowed to connect")
                        ? ConnectionFailureKind.NotAllowed : ConnectionFailureKind.Login;
                if (state is "3D000") return ConnectionFailureKind.DatabaseMissing;
            }
            if (SqlServerNumber(e) is { } number)
            {
                if (number is 4060 or 4063 or 4064) return ConnectionFailureKind.DatabaseMissing;
                if (number is 18456 or 18452 or 18470 or 18486 or 18487 or 18488) return ConnectionFailureKind.Login;
                if (number == -2) return ConnectionFailureKind.Timeout;
            }
        }

        // Then the texts, for drivers and servers that give no code.
        var all = string.Join(" ", chain.Select(e => e.Message));
        if (Mentions(all, "Unknown database", "Cannot open database", "does not exist")
            && Mentions(all, "database"))
            return ConnectionFailureKind.DatabaseMissing;
        // TLS before the sign-in texts: .NET's own TLS error reads "Authentication failed, see inner exception".
        if (ConnectionSecurity.ExplainFailure(error) is not null) return ConnectionFailureKind.Tls;
        if (Mentions(all, "is not allowed to connect to this", "no pg_hba.conf entry"))
            return ConnectionFailureKind.NotAllowed;
        // Entra ID: MSAL's AADSTS codes, SqlClient's "… in Active Directory", Azure PostgreSQL's token check.
        if (Mentions(all, "AADSTS", "in Active Directory", "Azure AD", "Microsoft Entra", "access token", "token has expired"))
            return ConnectionFailureKind.Login;
        if (Mentions(all, "password authentication failed", "Login failed for user", "Access denied for user",
                "authentication failed", "No password has been provided", "Invalid password"))
            return ConnectionFailureKind.Login;

        foreach (var e in chain)
        {
            if (e is SocketException s)
            {
                switch (s.SocketErrorCode)
                {
                    case SocketError.HostNotFound or SocketError.NoData or SocketError.TryAgain:
                        return ConnectionFailureKind.HostNotFound;
                    case SocketError.ConnectionRefused:
                        return ConnectionFailureKind.Refused;
                    case SocketError.TimedOut or SocketError.HostUnreachable or SocketError.NetworkUnreachable
                        or SocketError.HostDown or SocketError.NetworkDown:
                        return ConnectionFailureKind.Timeout;
                }
            }
            if (e is TimeoutException) return ConnectionFailureKind.Timeout;
            // SqlClient on Windows reports socket errors through Win32Exception with the WSA code.
            if (e is Win32Exception w)
            {
                if (w.NativeErrorCode is 11001 or 11004 or 11002) return ConnectionFailureKind.HostNotFound;
                if (w.NativeErrorCode == 10061) return ConnectionFailureKind.Refused;
                if (w.NativeErrorCode is 10060 or 10065 or 10051 or 258) return ConnectionFailureKind.Timeout;
            }
        }

        if (Mentions(all, "No such host is known", "Name or service not known", "nodename nor servname",
                "Temporary failure in name resolution", "Could not resolve", "No address associated with hostname"))
            return ConnectionFailureKind.HostNotFound;
        if (Mentions(all, "actively refused", "Connection refused", "error: 40 - Could not open a connection"))
            return ConnectionFailureKind.Refused;
        if (Mentions(all, "timeout expired", "timed out", "The timeout period elapsed", "Connect Timeout",
                "server was not found or was not accessible", "Unable to connect to any of the specified MySQL hosts"))
            return ConnectionFailureKind.Timeout;

        return ConnectionFailureKind.Other;
    }

    private static bool Mentions(string text, params string[] parts) =>
        parts.Any(p => text.Contains(p, StringComparison.OrdinalIgnoreCase));

    /// <summary>SqlException.Number, read without a reference to SqlClient (the Application layer has none).</summary>
    private static int? SqlServerNumber(Exception e) =>
        e.GetType().FullName == "Microsoft.Data.SqlClient.SqlException"
        && e.GetType().GetProperty("Number")?.GetValue(e) is int n ? n : null;
}
