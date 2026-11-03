namespace DbExplorer.Core.Connections;

/// <summary>The Microsoft Entra ID sign-in failed before the database was reached.</summary>
public sealed class EntraSignInException(string message, Exception? inner = null) : Exception(message, inner);
