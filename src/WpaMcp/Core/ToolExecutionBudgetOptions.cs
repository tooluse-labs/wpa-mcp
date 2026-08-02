namespace WpaMcp.Core;

internal sealed record ToolExecutionBudgetOptions(
    int MaxToolArgumentBytes,
    int MaxStringChars,
    int MaxCollectionItems,
    int MaxJsonRpcRequestBytes,
    int ResponseWarningBytes,
    int MaxJsonRpcResponseBytes)
{
    internal const string MaxToolArgumentBytesVariable = "WPAMCP_MAX_TOOL_ARGUMENT_BYTES";
    internal const string MaxStringCharsVariable = "WPAMCP_MAX_STRING_CHARS";
    internal const string MaxCollectionItemsVariable = "WPAMCP_MAX_COLLECTION_ITEMS";
    internal const string ResponseWarningBytesVariable = "WPAMCP_RESPONSE_WARNING_BYTES";

    internal static ToolExecutionBudgetOptions FromEnvironment(
        Func<string, string?>? getEnvironmentVariable = null)
    {
        getEnvironmentVariable ??= Environment.GetEnvironmentVariable;
        var request = JsonRpcRequestFrameOptions.FromEnvironment(getEnvironmentVariable);
        var response = ToolsListPaginationOptions.FromEnvironment(getEnvironmentVariable);
        return new ToolExecutionBudgetOptions(
            ParseBounded(
                getEnvironmentVariable(MaxToolArgumentBytesVariable),
                MaxToolArgumentBytesVariable,
                defaultValue: Validation.MaxSerializedArgumentsBytes,
                minimum: 1_024,
                maximum: Validation.MaxSerializedArgumentsBytes),
            ParseBounded(
                getEnvironmentVariable(MaxStringCharsVariable),
                MaxStringCharsVariable,
                defaultValue: Validation.MaxStringChars,
                minimum: 64,
                maximum: Validation.MaxStringChars),
            ParseBounded(
                getEnvironmentVariable(MaxCollectionItemsVariable),
                MaxCollectionItemsVariable,
                defaultValue: Validation.MaxCollectionItems,
                minimum: 1,
                maximum: Validation.MaxCollectionItems),
            request.MaxFrameBytes,
            ParseBounded(
                getEnvironmentVariable(ResponseWarningBytesVariable),
                ResponseWarningBytesVariable,
                defaultValue: ToolListPayload.DefaultMaxPayloadBytes,
                minimum: ToolsListPaginationOptions.MinimumConfiguredFrameBytes,
                maximum: 10_000_000),
            response.MaxResponseFrameBytes);
    }

    private static int ParseBounded(
        string? raw,
        string source,
        int defaultValue,
        int minimum,
        int maximum)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return defaultValue;
        if (!int.TryParse(raw, out var value) || value < minimum || value > maximum)
            throw new ToolsListStartupValidationException(
                $"{source} must be an integer from {minimum} through {maximum}.");
        return value;
    }
}
