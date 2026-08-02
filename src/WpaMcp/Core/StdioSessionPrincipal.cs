using System.Security.Cryptography;

namespace WpaMcp.Core;

/// <summary>
/// One explicit principal for the lifetime of a stateful stdio server process.
/// The registry key is deliberately never exposed through ToString, logging, or wire DTOs.
/// </summary>
internal sealed class StdioSessionPrincipal
{
    private readonly string _registryKey =
        Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16));

    internal string RegistryKey => _registryKey;

    public override string ToString() => "stdio_session_principal:[redacted]";
}
