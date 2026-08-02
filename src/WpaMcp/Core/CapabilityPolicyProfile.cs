using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using WpaMcp.Output;

namespace WpaMcp.Core;

/// <summary>
/// Startup-immutable administrative projection over the reviewed capability
/// manifest. The manifest itself remains complete; this profile controls only
/// which mapped tools are callable in the current server process.
/// </summary>
internal sealed class CapabilityPolicyProfile
{
    internal const string DisabledCapabilitiesEnvironmentVariable =
        "WPAMCP_DISABLED_CAPABILITIES";
    internal const string DisableCapabilitiesOption = "--disable-capabilities";

    private static readonly Regex CapabilityIdPattern = new(
        "^[a-z][a-z0-9_]*(\\.[a-z][a-z0-9_]*)+$",
        RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);

    private CapabilityPolicyProfile(
        IReadOnlyList<string> disabledCapabilityIds,
        string source)
    {
        DisabledCapabilityIds = disabledCapabilityIds;
        Source = source;
        ProfileName = disabledCapabilityIds.Count == 0 ? "full" : "restricted";
        var canonical = "capability_policy.v1\n" +
            string.Join('\n', disabledCapabilityIds);
        ProfileHash = Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(canonical)))
            .ToLowerInvariant();
        ProfileIdentity = "cpp_" + ProfileHash[..32];
    }

    internal static CapabilityPolicyProfile Full { get; } =
        new([], "default");

    internal string ProfileName { get; }
    internal string ProfileIdentity { get; }
    internal string ProfileHash { get; }
    internal string Source { get; }
    internal string SelectionScope => "startup_immutable";
    internal IReadOnlyList<string> DisabledCapabilityIds { get; }
    internal bool IsFull => DisabledCapabilityIds.Count == 0;

    internal bool IsDisabled(string capabilityId) =>
        DisabledCapabilityIds.Contains(capabilityId, StringComparer.Ordinal);

    internal static CapabilityPolicyProfile Parse(string? value, string source)
    {
        if (string.IsNullOrWhiteSpace(value))
            return Full;
        if (string.IsNullOrWhiteSpace(source))
            throw new ArgumentException("A capability-policy source is required.", nameof(source));

        var normalized = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var raw in value.Split(',', StringSplitOptions.None))
        {
            var capabilityId = raw.Trim();
            if (capabilityId.Length == 0 || !CapabilityIdPattern.IsMatch(capabilityId))
            {
                throw new ArgumentException(
                    $"{source} must be a comma-separated list of canonical lowercase capability IDs.");
            }
            if (!seen.Add(capabilityId))
            {
                throw new ArgumentException(
                    $"{source} contains duplicate capability ID '{capabilityId}'.");
            }
            normalized.Add(capabilityId);
        }

        normalized.Sort(StringComparer.Ordinal);
        return new CapabilityPolicyProfile(normalized.ToArray(), source);
    }

    internal CapabilityPolicyRecord ToRecord() => new(
        ProfileName,
        ProfileIdentity,
        ProfileHash,
        Source,
        SelectionScope,
        DisabledCapabilityIds);
}
