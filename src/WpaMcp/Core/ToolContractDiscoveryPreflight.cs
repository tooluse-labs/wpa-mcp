using System.Text.Json;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using WpaMcp.Core.Catalog;
using WpaMcp.Output;

namespace WpaMcp.Core;

internal sealed record ToolContractDiscoveryPreflightResult(
    int FixedPageUtf8Bytes,
    int ToolCount,
    int PageCount,
    int MaximumToolFrameBytes,
    string MaximumToolFrameToolName,
    int MaximumToolFramePage,
    int MaximumResourceFrameBytes,
    string MaximumResourceFrameToolName,
    int MaximumResourceFramePage)
{
    internal int MinimumViableFrameBytes =>
        Math.Max(MaximumToolFrameBytes, MaximumResourceFrameBytes);

    internal void Validate(int maxResponseFrameBytes)
    {
        if (MinimumViableFrameBytes <= maxResponseFrameBytes)
            return;

        throw new ToolContractDiscoveryStartupValidationException(
            "MCP output-contract discovery preflight requires at least " +
            $"{MinimumViableFrameBytes} response bytes for immutable " +
            $"{FixedPageUtf8Bytes}-UTF-8-byte pages, but the configured cap is " +
            $"{maxResponseFrameBytes}. Largest Tool frame=" +
            $"{MaximumToolFrameToolName}/page/{MaximumToolFramePage} " +
            $"({MaximumToolFrameBytes} bytes); largest Resource frame=" +
            $"{MaximumResourceFrameToolName}/page/{MaximumResourceFramePage} " +
            $"({MaximumResourceFrameBytes} bytes).");
    }
}

internal sealed class ToolContractDiscoveryStartupValidationException(string message)
    : InvalidOperationException(message);

internal static class ToolContractDiscoveryPreflight
{
    private static readonly RequestId WorstCaseRequestId = new(new string('r', 126));

    internal static ToolContractDiscoveryPreflightResult Measure(
        ActiveToolCatalog catalog,
        IReadOnlyList<McpServerTool> serverTools)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(serverTools);
        if (ToolRequestIdPolicy.SerializedBytes(WorstCaseRequestId) !=
            ToolRequestIdPolicy.MaxSerializedBytes)
        {
            throw new InvalidOperationException(
                "The contract discovery preflight request id no longer matches ingress policy.");
        }

        var wrapper = serverTools.SingleOrDefault(candidate => string.Equals(
                candidate.ProtocolTool.Name,
                "get_tool_contract",
                StringComparison.Ordinal)) as ContractMcpServerTool
            ?? throw new CatalogValidationException(
                "OUTPUT-CONTRACT-PREFLIGHT: get_tool_contract is not registered through the contract wrapper.");

        var pageCount = 0;
        var maximumToolFrameBytes = 0;
        var maximumToolFrameToolName = string.Empty;
        var maximumToolFramePage = 0;
        var maximumResourceFrameBytes = 0;
        var maximumResourceFrameToolName = string.Empty;
        var maximumResourceFramePage = 0;

        foreach (var contract in catalog.OutputContracts.Values
                     .OrderBy(candidate => candidate.ToolName, StringComparer.Ordinal))
        {
            var pages = CapabilityDiscoveryRuntime.BuildToolOutputContractPages(
                contract,
                CapabilityDiscoveryRuntime.ToolContractPageUtf8Bytes);
            pageCount = checked(pageCount + pages.Count);

            var indexFrameBytes = CapabilityDiscoveryRuntime.MeasureReadResourceFrame(
                CapabilityDiscoveryRuntime.CreateResourceContent(
                contract.SchemaUri,
                new ToolOutputContractResourceIndex(
                    contract.ToolName,
                    contract.ContractVersion,
                    contract.SchemaUri,
                    contract.Sha256,
                    contract.MediaType,
                    contract.Utf8Bytes,
                    pages.Count,
                    $"{contract.SchemaUri}/pages/{{page}}",
                    CapabilityDiscoveryRuntime.ToolContractPageOrdering,
                    CapabilityDiscoveryRuntime.ToolContractAssemblyRule,
                    CapabilityDiscoveryRuntime.ToolContractHashRule)));
            RecordMaximum(
                indexFrameBytes,
                contract.ToolName,
                page: 0,
                ref maximumResourceFrameBytes,
                ref maximumResourceFrameToolName,
                ref maximumResourceFramePage);

            foreach (var page in pages)
            {
                var resourceFrameBytes = CapabilityDiscoveryRuntime.MeasureReadResourceFrame(
                    CapabilityDiscoveryRuntime.CreateResourceContent(
                    page.Uri,
                    new ToolOutputContractResourcePage(
                        contract.ToolName,
                        contract.Sha256,
                        page.Number,
                        pages.Count,
                        page.StartUtf8Byte,
                        page.ReturnedUtf8Bytes,
                        page.SchemaFragment)));
                RecordMaximum(
                    resourceFrameBytes,
                    contract.ToolName,
                    page.Number,
                    ref maximumResourceFrameBytes,
                    ref maximumResourceFrameToolName,
                    ref maximumResourceFramePage);

                var data = new ToolContractPageResponse(
                    contract.ToolName,
                    contract.ContractVersion,
                    contract.SchemaUri,
                    contract.Sha256,
                    contract.MediaType,
                    contract.Utf8Bytes,
                    page.Number,
                    pages.Count,
                    page.StartUtf8Byte,
                    page.ReturnedUtf8Bytes,
                    page.SchemaFragment,
                    page.Number < pages.Count ? page.Number + 1 : null);
                var arguments = new Dictionary<string, JsonElement>(StringComparer.Ordinal)
                {
                    ["toolName"] = JsonSerializer.SerializeToElement(contract.ToolName),
                    ["page"] = JsonSerializer.SerializeToElement(page.Number),
                };
                var fitted = wrapper.MeasureSuccessfulDataForPreflight(
                    WorstCaseRequestId,
                    data,
                    arguments);
                RecordMaximum(
                    fitted.FrameBytes,
                    contract.ToolName,
                    page.Number,
                    ref maximumToolFrameBytes,
                    ref maximumToolFrameToolName,
                    ref maximumToolFramePage);
            }
        }

        if (pageCount == 0 || maximumToolFrameBytes == 0 || maximumResourceFrameBytes == 0)
        {
            throw new CatalogValidationException(
                "OUTPUT-CONTRACT-PREFLIGHT: the active output-contract registry is empty.");
        }

        return new ToolContractDiscoveryPreflightResult(
            CapabilityDiscoveryRuntime.ToolContractPageUtf8Bytes,
            catalog.OutputContracts.Count,
            pageCount,
            maximumToolFrameBytes,
            maximumToolFrameToolName,
            maximumToolFramePage,
            maximumResourceFrameBytes,
            maximumResourceFrameToolName,
            maximumResourceFramePage);
    }

    private static void RecordMaximum(
        int frameBytes,
        string toolName,
        int page,
        ref int maximumFrameBytes,
        ref string maximumToolName,
        ref int maximumPage)
    {
        if (frameBytes <= maximumFrameBytes)
            return;
        maximumFrameBytes = frameBytes;
        maximumToolName = toolName;
        maximumPage = page;
    }
}
