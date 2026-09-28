namespace DbExplorer.Core.Connections;

/// <summary>What a connection points at; drives the colored banner and extra confirmations.</summary>
public enum ConnectionEnvironment
{
    None,
    Development,
    Test,
    Staging,
    Production
}

public static class ConnectionEnvironmentExtensions
{
    public static string? ShortTag(this ConnectionEnvironment environment) => environment switch
    {
        ConnectionEnvironment.Development => "DEV",
        ConnectionEnvironment.Test => "TEST",
        ConnectionEnvironment.Staging => "STAGING",
        ConnectionEnvironment.Production => "PROD",
        _ => null
    };
}
