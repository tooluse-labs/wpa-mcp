using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ModelContextProtocol;
using WpaMcp.Core;
using WpaMcp.Core.Catalog;
using WpaMcp.Output;

namespace WpaMcp.Tests;

public sealed class ToolContractDiscoveryTests
{
    [Fact]
    public void ContractLinkMetadata_IsResourceOnlyAndLeavesCatalogResultShapeUnchanged()
    {
        var catalog = ActiveToolCatalog.LoadAndValidate();
        var runtime = new CapabilityDiscoveryRuntime(catalog, new StdioSessionPrincipal());
        var tool = catalog.Tools[0];
        var contract = tool.OutputContract;
        var detail = JsonSerializer.Deserialize<ServerToolResourceRecord>(
            runtime.ToolDetailResource(tool.ToolName).Text,
            McpJsonUtilities.DefaultOptions)!;

        Assert.Equal("linked_content_addressed_resource", detail.FullContractSource);
        Assert.Equal(contract.SchemaUri, detail.OutputContractResourceUri);
        Assert.Equal(contract.Sha256, detail.OutputContractSha256);
        Assert.Equal(contract.Utf8Bytes, detail.OutputContractUtf8Bytes);
        Assert.Equal(contract.MediaType, detail.OutputContractMediaType);

        Assert.Null(typeof(ServerToolCatalogRecord).GetProperty("FullContractSource"));
        Assert.Null(typeof(ServerToolCatalogRecord).GetProperty("OutputContractResourceUri"));
        Assert.Null(typeof(ServerToolCatalogRecord).GetProperty("OutputContractSha256"));
        Assert.Null(typeof(ServerToolCatalogRecord).GetProperty("OutputContractUtf8Bytes"));
        Assert.Null(typeof(ServerToolCatalogRecord).GetProperty("OutputContractMediaType"));
    }

    [Fact]
    public void ContentAddressedResourcePages_ReassembleEveryActiveContract()
    {
        var catalog = ActiveToolCatalog.LoadAndValidate();
        var runtime = new CapabilityDiscoveryRuntime(
            catalog,
            new StdioSessionPrincipal(),
            maxResponseFrameBytes: ToolResponseBudgetOptions.HardMaxResponseFrameBytes);

        foreach (var tool in catalog.Tools)
        {
            var contract = tool.OutputContract;
            var index = JsonSerializer.Deserialize<ToolOutputContractResourceIndex>(
                runtime.ToolOutputContractIndexResource(tool.ToolName, contract.Sha256).Text,
                McpJsonUtilities.DefaultOptions)!;

            Assert.Equal(tool.ToolName, index.ToolName);
            Assert.Equal(contract.ContractVersion, index.ContractVersion);
            Assert.Equal(contract.SchemaUri, index.SchemaUri);
            Assert.Equal(contract.Sha256, index.Sha256);
            Assert.Equal(contract.MediaType, index.MediaType);
            Assert.Equal(contract.Utf8Bytes, index.Utf8Bytes);
            Assert.Equal($"{contract.SchemaUri}/pages/{{page}}", index.PageUriTemplate);
            Assert.Equal("page_asc_start_utf8_byte_asc", index.Ordering);
            Assert.Contains("schemaFragment", index.AssemblyRule, StringComparison.Ordinal);
            Assert.Contains("SHA-256", index.HashRule, StringComparison.Ordinal);

            var assembled = new StringBuilder(contract.CanonicalJson.Length);
            var nextStart = 0;
            for (var pageNumber = 1; pageNumber <= index.PageCount; pageNumber++)
            {
                var page = JsonSerializer.Deserialize<ToolOutputContractResourcePage>(
                    runtime.ToolOutputContractPageResource(
                        tool.ToolName,
                        contract.Sha256,
                        pageNumber).Text,
                    McpJsonUtilities.DefaultOptions)!;

                Assert.Equal(tool.ToolName, page.ToolName);
                Assert.Equal(contract.Sha256, page.Sha256);
                Assert.Equal(pageNumber, page.Page);
                Assert.Equal(index.PageCount, page.PageCount);
                Assert.Equal(nextStart, page.StartUtf8Byte);
                Assert.Equal(
                    Encoding.UTF8.GetByteCount(page.SchemaFragment),
                    page.ReturnedUtf8Bytes);
                Assert.InRange(page.ReturnedUtf8Bytes, 1, contract.Utf8Bytes);

                assembled.Append(page.SchemaFragment);
                nextStart = checked(nextStart + page.ReturnedUtf8Bytes);
            }

            Assert.Equal(contract.Utf8Bytes, nextStart);
            Assert.Equal(contract.CanonicalJson, assembled.ToString());
            Assert.Equal(contract.Sha256, Sha256(assembled.ToString()));
        }
    }

    [Fact]
    public void DiscoveryPreflight_MeasuresEveryFixedPageWithWorstCaseRequestId()
    {
        var catalog = ActiveToolCatalog.LoadAndValidate();
        var preflight = MeasurePreflight(catalog);

        Assert.Equal(8_192, preflight.FixedPageUtf8Bytes);
        Assert.Equal(61, preflight.ToolCount);
        Assert.Equal(318, preflight.PageCount);
        Assert.Equal(35_858, preflight.MaximumToolFrameBytes);
        Assert.Equal("list_processes", preflight.MaximumToolFrameToolName);
        Assert.Equal(2, preflight.MaximumToolFramePage);
        Assert.Equal(15_911, preflight.MaximumResourceFrameBytes);
        Assert.Equal("list_processes", preflight.MaximumResourceFrameToolName);
        Assert.Equal(2, preflight.MaximumResourceFramePage);
        Assert.Equal(
            preflight.MaximumToolFrameBytes,
            preflight.MinimumViableFrameBytes);

        preflight.Validate(preflight.MinimumViableFrameBytes);
        var failure = Assert.Throws<ToolContractDiscoveryStartupValidationException>(() =>
            preflight.Validate(preflight.MinimumViableFrameBytes - 1));
        Assert.Contains(
            preflight.MinimumViableFrameBytes.ToString(),
            failure.Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public void LowBudgetEmbeddedRuntime_DefersContractResourceBudgetFailureUntilRead()
    {
        const int embeddedBudget = 12_000;
        var catalog = ActiveToolCatalog.LoadAndValidate();
        var preflight = MeasurePreflight(catalog);
        var runtime = new CapabilityDiscoveryRuntime(
            catalog,
            new StdioSessionPrincipal(),
            maxResponseFrameBytes: embeddedBudget);
        var contract = catalog.OutputContracts[preflight.MaximumResourceFrameToolName];

        Assert.NotEmpty(runtime.List(domain: null, goal: null, cursor: null).Capabilities);
        var failure = Assert.Throws<CatalogValidationException>(() =>
            runtime.ToolOutputContractPageResource(
                contract.ToolName,
                contract.Sha256,
                preflight.MaximumResourceFramePage));
        Assert.Contains("RESOURCE-WIRE-BUDGET", failure.Message, StringComparison.Ordinal);
        Assert.Contains(embeddedBudget.ToString(), failure.Message, StringComparison.Ordinal);
        Assert.Contains(
            preflight.MaximumResourceFrameBytes.ToString(),
            failure.Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public void ContractResourcePages_AreImmutableAcrossCapsAndMatchToolFallbackBoundaries()
    {
        var catalog = ActiveToolCatalog.LoadAndValidate();
        var preflight = MeasurePreflight(catalog);
        var exact = new CapabilityDiscoveryRuntime(
            catalog,
            new StdioSessionPrincipal(),
            maxResponseFrameBytes: preflight.MinimumViableFrameBytes);
        var maximum = new CapabilityDiscoveryRuntime(
            catalog,
            new StdioSessionPrincipal(),
            maxResponseFrameBytes: ToolResponseBudgetOptions.HardMaxResponseFrameBytes);

        foreach (var contract in catalog.OutputContracts.Values)
        {
            var exactIndexResource = exact.ToolOutputContractIndexResource(
                contract.ToolName,
                contract.Sha256);
            var maximumIndexResource = maximum.ToolOutputContractIndexResource(
                contract.ToolName,
                contract.Sha256);
            Assert.Equal(exactIndexResource.Uri, maximumIndexResource.Uri);
            Assert.Equal(exactIndexResource.Text, maximumIndexResource.Text);
            var index = JsonSerializer.Deserialize<ToolOutputContractResourceIndex>(
                exactIndexResource.Text,
                McpJsonUtilities.DefaultOptions)!;

            for (var pageNumber = 1; pageNumber <= index.PageCount; pageNumber++)
            {
                var exactPageResource = exact.ToolOutputContractPageResource(
                    contract.ToolName,
                    contract.Sha256,
                    pageNumber);
                var maximumPageResource = maximum.ToolOutputContractPageResource(
                    contract.ToolName,
                    contract.Sha256,
                    pageNumber);
                Assert.Equal(exactPageResource.Uri, maximumPageResource.Uri);
                Assert.Equal(exactPageResource.Text, maximumPageResource.Text);

                var resourcePage = JsonSerializer.Deserialize<ToolOutputContractResourcePage>(
                    exactPageResource.Text,
                    McpJsonUtilities.DefaultOptions)!;
                var toolPage = exact.ToolContractPage(contract.ToolName, pageNumber);
                Assert.Equal(toolPage.Page, resourcePage.Page);
                Assert.Equal(toolPage.PageCount, resourcePage.PageCount);
                Assert.Equal(toolPage.StartUtf8Byte, resourcePage.StartUtf8Byte);
                Assert.Equal(toolPage.ReturnedUtf8Bytes, resourcePage.ReturnedUtf8Bytes);
                Assert.Equal(toolPage.SchemaFragment, resourcePage.SchemaFragment);
            }
        }
    }

    [Fact]
    public void ToolFallbackPages_ReassembleLargestContractWithStableRandomAccess()
    {
        var catalog = ActiveToolCatalog.LoadAndValidate();
        var runtime = new CapabilityDiscoveryRuntime(catalog, new StdioSessionPrincipal());
        var contract = catalog.Tools
            .Select(tool => tool.OutputContract)
            .MaxBy(candidate => candidate.Utf8Bytes)!;
        var first = runtime.ToolContractPage(contract.ToolName, page: 1);
        var assembled = new StringBuilder(contract.CanonicalJson.Length);
        var nextStart = 0;

        for (var pageNumber = 1; pageNumber <= first.PageCount; pageNumber++)
        {
            var page = runtime.ToolContractPage(contract.ToolName, pageNumber);
            Assert.Equal(contract.ToolName, page.ToolName);
            Assert.Equal(contract.ContractVersion, page.ContractVersion);
            Assert.Equal(contract.SchemaUri, page.SchemaUri);
            Assert.Equal(contract.Sha256, page.Sha256);
            Assert.Equal(contract.MediaType, page.MediaType);
            Assert.Equal(contract.Utf8Bytes, page.Utf8Bytes);
            Assert.Equal(pageNumber, page.Page);
            Assert.Equal(first.PageCount, page.PageCount);
            Assert.Equal(nextStart, page.StartUtf8Byte);
            Assert.Equal(
                Encoding.UTF8.GetByteCount(page.SchemaFragment),
                page.ReturnedUtf8Bytes);
            Assert.Equal(
                pageNumber < page.PageCount ? pageNumber + 1 : null,
                page.NextPage);

            assembled.Append(page.SchemaFragment);
            nextStart = checked(nextStart + page.ReturnedUtf8Bytes);
        }

        Assert.Equal(contract.Utf8Bytes, nextStart);
        Assert.Equal(contract.CanonicalJson, assembled.ToString());
        Assert.Equal(contract.Sha256, Sha256(assembled.ToString()));

        var repeatedFirst = runtime.ToolContractPage(contract.ToolName, page: 1);
        Assert.Equal(first, repeatedFirst);
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            runtime.ToolContractPage(contract.ToolName, page: 0));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            runtime.ToolContractPage(contract.ToolName, page: first.PageCount + 1));
    }

    [Fact]
    public void ContractResources_RejectUnknownOrMismatchedContentAddresses()
    {
        var catalog = ActiveToolCatalog.LoadAndValidate();
        var runtime = new CapabilityDiscoveryRuntime(catalog, new StdioSessionPrincipal());
        var contract = catalog.Tools[0].OutputContract;

        Assert.Throws<ArgumentException>(() =>
            runtime.ToolOutputContractIndexResource("not_a_tool", contract.Sha256));
        Assert.Throws<ArgumentException>(() =>
            runtime.ToolOutputContractIndexResource(contract.ToolName, new string('0', 64)));
        Assert.Throws<ArgumentException>(() =>
            runtime.ToolOutputContractIndexResource(
                contract.ToolName.ToUpperInvariant(),
                contract.Sha256));
        Assert.Throws<ArgumentException>(() =>
            runtime.ToolOutputContractIndexResource(
                $" {contract.ToolName}",
                contract.Sha256));
        Assert.Throws<ArgumentException>(() =>
            runtime.ToolOutputContractIndexResource(
                contract.ToolName,
                contract.Sha256.ToUpperInvariant()));
        Assert.Throws<ArgumentException>(() =>
            runtime.ToolOutputContractIndexResource(
                contract.ToolName,
                $"{contract.Sha256} "));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            runtime.ToolOutputContractPageResource(
                contract.ToolName,
                contract.Sha256,
                page: 0));
    }

    private static string Sha256(string value) => Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(value)))
        .ToLowerInvariant();

    private static ToolContractDiscoveryPreflightResult MeasurePreflight(
        ActiveToolCatalog catalog) =>
        ToolContractDiscoveryPreflight.Measure(
            catalog,
            catalog.CreateServerTools(new DeferredCatalogServiceProvider()));
}
