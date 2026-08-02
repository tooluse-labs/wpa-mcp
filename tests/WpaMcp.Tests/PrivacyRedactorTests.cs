using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Moq;
using WpaMcp.Core;
using WpaMcp.Core.Catalog;
using WpaMcp.Output;

namespace WpaMcp.Tests;

public sealed class PrivacyRedactorTests
{
    private static readonly ActiveToolCatalog Catalog = ActiveToolCatalog.LoadAndValidate();

    [Theory]
    [InlineData("secret command line")]
    [InlineData("alias_file_path_AAAAAAAAAAAAAAAAAAAAAA")]
    [InlineData("<unmapped:0x0000000000000001>")]
    [InlineData(@"C:\Users\alice\WPR Files\secret.etl")]
    public void StrictMarkerPayload_IsAlwaysRedactedBySemanticPath(string payload)
    {
        using var aliases = new TypedAliasRegistry(Enumerable.Repeat((byte)1, 32).ToArray());
        var redactor = new ToolPrivacyRedactor(
            ToolPrivacyMode.Strict,
            ToolPrivacyTaxonomy.Default,
            aliases);
        var envelope = MarkerEnvelope(payload);

        var redacted = redactor.Redact(envelope, Tool("find_marker"));

        Assert.Equal(
            "[redacted]",
            redacted["data"]!["rows"]![0]!["fields"]![0]!["value"]!.GetValue<string>());
        Assert.DoesNotContain(payload, redacted.ToJsonString(), StringComparison.Ordinal);
    }

    [Fact]
    public void PathsMode_AliasesWholePathsRegistryKeysAndFreeTextWithoutBasenameLeak()
    {
        using var aliases = new TypedAliasRegistry(Enumerable.Repeat((byte)2, 32).ToArray());
        var redactor = new ToolPrivacyRedactor(
            ToolPrivacyMode.Paths,
            ToolPrivacyTaxonomy.Default,
            aliases);
        var envelope = new JsonObject
        {
            ["data"] = new JsonObject
            {
                ["file"] = @"C:\Users\alice\WPR Files\secret_project.docx",
                ["registryKey"] = @"HKCU\Software\Secret Project\Token",
                ["note"] = @"failed to read C:\Users\alice\WPR Files\secret_project.docx after retry",
            },
            ["warnings"] = new JsonArray(
                @"UNC \\server\private share\customer.etl could not be read"),
        };

        var redacted = redactor.Redact(envelope, Tool("file_io_top_files"));
        var json = redacted.ToJsonString();

        Assert.StartsWith("alias_file_path_", redacted["data"]!["file"]!.GetValue<string>(), StringComparison.Ordinal);
        Assert.StartsWith("alias_registry_path_", redacted["data"]!["registryKey"]!.GetValue<string>(), StringComparison.Ordinal);
        Assert.DoesNotContain("secret_project.docx", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("WPR Files", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("customer.etl", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("private share", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void StrictMode_AliasesTypedIdentifiersAndRedactsRegistryValues()
    {
        using var aliases = new TypedAliasRegistry(Enumerable.Repeat((byte)3, 32).ToArray());
        var redactor = new ToolPrivacyRedactor(
            ToolPrivacyMode.Strict,
            ToolPrivacyTaxonomy.Default,
            aliases);
        var envelope = new JsonObject
        {
            ["data"] = new JsonObject
            {
                ["machineName"] = "BUILD-SECRET-01",
                ["localAddress"] = "10.42.1.7",
                ["remoteAddress"] = "2001:db8::1234",
                ["registryValue"] = "customer-api-key",
            },
        };

        var redacted = redactor.Redact(envelope, Tool("net_connections"));

        Assert.StartsWith("alias_machine_name_", redacted["data"]!["machineName"]!.GetValue<string>(), StringComparison.Ordinal);
        Assert.StartsWith("alias_ip_address_", redacted["data"]!["localAddress"]!.GetValue<string>(), StringComparison.Ordinal);
        Assert.StartsWith("alias_ip_address_", redacted["data"]!["remoteAddress"]!.GetValue<string>(), StringComparison.Ordinal);
        Assert.Equal("[redacted]", redacted["data"]!["registryValue"]!.GetValue<string>());
    }

    [Theory]
    [InlineData("2.0")]
    [InlineData("2026.08")]
    public void StrictMode_DoesNotMisclassifyVersionStringsAsIpAddresses(string version)
    {
        using var aliases = new TypedAliasRegistry(Enumerable.Repeat((byte)11, 32).ToArray());
        var redactor = new ToolPrivacyRedactor(
            ToolPrivacyMode.Strict,
            ToolPrivacyTaxonomy.Default,
            aliases);
        var envelope = new JsonObject
        {
            ["contractVersion"] = version,
            ["data"] = new JsonObject { ["schemaVersion"] = version },
        };

        var redacted = redactor.Redact(envelope, Tool("inspect_trace"));

        Assert.Equal(version, redacted["contractVersion"]!.GetValue<string>());
        Assert.Equal(version, redacted["data"]!["schemaVersion"]!.GetValue<string>());
    }

    [Fact]
    public void StrictMode_FreeTextStillAliasesUnambiguousIpv4AndIpv6Literals()
    {
        using var aliases = new TypedAliasRegistry(Enumerable.Repeat((byte)12, 32).ToArray());
        var redactor = new ToolPrivacyRedactor(
            ToolPrivacyMode.Strict,
            ToolPrivacyTaxonomy.Default,
            aliases);
        var envelope = new JsonObject
        {
            ["data"] = new JsonObject
            {
                ["note"] = "contract 2.0 contacted 10.42.1.7 and [2001:db8::1234]",
            },
        };

        var redacted = redactor.Redact(envelope, Tool("inspect_trace"));
        var note = redacted["data"]!["note"]!.GetValue<string>();

        Assert.Contains("contract 2.0", note, StringComparison.Ordinal);
        Assert.DoesNotContain("10.42.1.7", note, StringComparison.Ordinal);
        Assert.DoesNotContain("2001:db8::1234", note, StringComparison.Ordinal);
        Assert.Equal(2, note.Split("alias_ip_address_", StringSplitOptions.None).Length - 1);
    }

    [Fact]
    public void ApprovedBasenameRule_DoesNotPermitAFullPath()
    {
        using var aliases = new TypedAliasRegistry(Enumerable.Repeat((byte)4, 32).ToArray());
        var redactor = new ToolPrivacyRedactor(
            ToolPrivacyMode.Paths,
            ToolPrivacyTaxonomy.Default,
            aliases);
        var envelope = new JsonObject
        {
            ["data"] = new JsonObject
            {
                ["module"] = "kernel32.dll",
                ["imageName"] = @"C:\private\secret-module.dll",
                ["expectedPdbName"] = @"\\server\symbols\private.pdb",
            },
        };

        var redacted = redactor.Redact(envelope, Tool("file_io_top_files"));

        Assert.Equal("kernel32.dll", redacted["data"]!["module"]!.GetValue<string>());
        Assert.StartsWith("alias_file_path_", redacted["data"]!["imageName"]!.GetValue<string>(), StringComparison.Ordinal);
        Assert.StartsWith("alias_unc_path_", redacted["data"]!["expectedPdbName"]!.GetValue<string>(), StringComparison.Ordinal);
    }

    [Fact]
    public void OpaqueLocators_ArePreservedOnlyInTheirContractFields()
    {
        using var aliases = new TypedAliasRegistry(Enumerable.Repeat((byte)5, 32).ToArray());
        var redactor = new ToolPrivacyRedactor(
            ToolPrivacyMode.Strict,
            ToolPrivacyTaxonomy.Default,
            aliases);
        const string traceId = "trc_0123456789abcdef0123456789abcdef";
        const string cursor = "qrc_0123456789abcdef0123456789abcdef";
        var envelope = MarkerEnvelope(traceId);
        envelope["traceRef"] = new JsonObject { ["traceId"] = traceId };
        envelope["sections"] = new JsonArray(new JsonObject { ["nextCursor"] = cursor });

        var redacted = redactor.Redact(envelope, Tool("find_marker"));

        Assert.Equal(traceId, redacted["traceRef"]!["traceId"]!.GetValue<string>());
        Assert.Equal(cursor, redacted["sections"]![0]!["nextCursor"]!.GetValue<string>());
        Assert.Equal("[redacted]", redacted["data"]!["rows"]![0]!["fields"]![0]!["value"]!.GetValue<string>());
    }

    [Theory]
    [InlineData("paths")]
    [InlineData("strict")]
    public async Task ContractToolWrapper_PrivacyModesPreserveExactPagedMachineContract(
        string profile)
    {
        var mode = ToolPrivacyOptions.Parse(profile, nameof(profile)).Mode;
        using var aliases = new TypedAliasRegistry(Enumerable.Repeat((byte)13, 32).ToArray());
        var redactor = new ToolPrivacyRedactor(mode, ToolPrivacyTaxonomy.Default, aliases);
        var services = new ServiceCollection();
        services.AddSingleton(Catalog);
        services.AddSingleton(new CapabilityDiscoveryRuntime(
            Catalog,
            new StdioSessionPrincipal()));
        using var provider = services.BuildServiceProvider();
        var wrapper = Catalog.CreateServerTools(provider, privacy: redactor).Single(candidate =>
            candidate.ProtocolTool.Name == "get_tool_contract");
        var server = new Mock<McpServer>();
        server.SetupGet(candidate => candidate.Services).Returns(provider);
        var contract = Catalog.OutputContracts.Values.MaxBy(candidate => candidate.Utf8Bytes)!;
        var assembled = new StringBuilder(contract.CanonicalJson.Length);
        var pageNumber = 1;
        int? pageCount = null;
        var nextStart = 0;

        while (true)
        {
            var result = await InvokeContractPage(
                wrapper,
                server.Object,
                contract.ToolName,
                pageNumber,
                mode);
            Assert.False(result.IsError);
            var structured = JsonNode.Parse(result.StructuredContent!.Value.GetRawText())!.AsObject();
            var textBlock = Assert.IsType<TextContentBlock>(Assert.Single(result.Content));
            Assert.True(JsonNode.DeepEquals(structured, JsonNode.Parse(textBlock.Text)));
            var data = Assert.IsType<JsonObject>(structured["data"]);

            Assert.Equal(contract.ToolName, data["toolName"]?.GetValue<string>());
            Assert.Equal(contract.ContractVersion, data["contractVersion"]?.GetValue<string>());
            Assert.Equal(contract.SchemaUri, data["schemaUri"]?.GetValue<string>());
            Assert.Equal(contract.Sha256, data["sha256"]?.GetValue<string>());
            Assert.Equal(contract.MediaType, data["mediaType"]?.GetValue<string>());
            Assert.Equal(contract.Utf8Bytes, data["utf8Bytes"]?.GetValue<int>());
            Assert.Equal(pageNumber, data["page"]?.GetValue<int>());
            var currentPageCount = data["pageCount"]?.GetValue<int>()
                ?? throw new JsonException("get_tool_contract omitted data.pageCount.");
            pageCount ??= currentPageCount;
            Assert.Equal(pageCount.Value, currentPageCount);
            Assert.Equal(nextStart, data["startUtf8Byte"]?.GetValue<int>());

            var fragment = data["schemaFragment"]?.GetValue<string>()
                ?? throw new JsonException("get_tool_contract omitted data.schemaFragment.");
            var returnedBytes = data["returnedUtf8Bytes"]?.GetValue<int>()
                ?? throw new JsonException("get_tool_contract omitted data.returnedUtf8Bytes.");
            Assert.Equal(returnedBytes, Encoding.UTF8.GetByteCount(fragment));
            assembled.Append(fragment);
            nextStart = checked(nextStart + returnedBytes);

            var nextPage = data["nextPage"]?.GetValue<int?>();
            Assert.Equal(pageNumber < currentPageCount ? pageNumber + 1 : null, nextPage);
            if (nextPage is null)
                break;
            pageNumber = nextPage.Value;
        }

        Assert.NotNull(pageCount);
        Assert.InRange(pageCount.Value, 2, int.MaxValue);
        Assert.Equal(pageCount.Value, pageNumber);
        Assert.Equal(contract.Utf8Bytes, nextStart);
        Assert.Equal(contract.CanonicalJson, assembled.ToString());
        Assert.Equal(contract.Sha256, Sha256(assembled.ToString()));
    }

    [Theory]
    [InlineData("paths")]
    [InlineData("strict")]
    public void ContractMachineStringBypass_RequiresExactToolAndJsonPointer(string profile)
    {
        var mode = ToolPrivacyOptions.Parse(profile, nameof(profile)).Mode;
        using var aliases = new TypedAliasRegistry(Enumerable.Repeat((byte)14, 32).ToArray());
        var redactor = new ToolPrivacyRedactor(mode, ToolPrivacyTaxonomy.Default, aliases);
        const string schemaUri = "wpa://contracts/tools/private/0123456789abcdef";
        const string schemaFragment =
            "{\"$schema\":\"https://json-schema.org/draft/2020-12/schema\",\"description\":\"C:\\\\private\\\\secret.txt\"}";
        var exactPointers = new JsonObject
        {
            ["data"] = new JsonObject
            {
                ["schemaUri"] = schemaUri,
                ["schemaFragment"] = schemaFragment,
            },
        };

        var otherTool = redactor.Redact(exactPointers, Tool("inspect_trace"));
        Assert.NotEqual(schemaUri, otherTool["data"]!["schemaUri"]!.GetValue<string>());
        Assert.NotEqual(schemaFragment, otherTool["data"]!["schemaFragment"]!.GetValue<string>());

        var nestedPointers = new JsonObject
        {
            ["data"] = new JsonObject
            {
                ["nested"] = new JsonObject
                {
                    ["schemaUri"] = schemaUri,
                    ["schemaFragment"] = schemaFragment,
                },
            },
        };
        var wrongPath = redactor.Redact(nestedPointers, Tool("get_tool_contract"));
        Assert.NotEqual(schemaUri, wrongPath["data"]!["nested"]!["schemaUri"]!.GetValue<string>());
        Assert.NotEqual(
            schemaFragment,
            wrongPath["data"]!["nested"]!["schemaFragment"]!.GetValue<string>());
    }

    [Fact]
    public void TypedAliases_AreStableBoundedKindCheckedAndOnlyResolvedForEnabledInputs()
    {
        var key = Enumerable.Repeat((byte)6, 32).ToArray();
        using var aliases = new TypedAliasRegistry(key);
        var tracePath = @"C:\traces\secret.etl";
        var alias = aliases.Issue(SensitiveFieldKind.TracePath, tracePath);

        Assert.Equal(alias, aliases.Issue(SensitiveFieldKind.TracePath, tracePath));
        Assert.Matches("^alias_trace_path_[A-Za-z0-9_-]{22}$", alias);
        Assert.True(aliases.TryResolve(SensitiveFieldKind.TracePath, alias, out var resolved));
        Assert.Equal(tracePath, resolved);
        Assert.False(aliases.TryResolve(SensitiveFieldKind.FilePath, alias, out _));
        Assert.False(aliases.TryResolve(SensitiveFieldKind.TracePath, new string('a', 129), out _));

        var rewriter = new ToolArgumentRewriter(ToolPrivacyTaxonomy.Default, aliases);
        var rewritten = rewriter.Rewrite("load_trace", new JsonObject { ["path"] = alias });
        Assert.Equal(tracePath, rewritten.Arguments["path"]!.GetValue<string>());
        Assert.Single(rewritten.ResolvedAliases);

        var ordinaryAliasPrefix = rewriter.Rewrite(
            "find_marker",
            new JsonObject { ["nameSubstring"] = "alias_not_a_contract_alias" });
        Assert.Equal(
            "alias_not_a_contract_alias",
            ordinaryAliasPrefix.Arguments["nameSubstring"]!.GetValue<string>());

        var wrongKind = aliases.Issue(SensitiveFieldKind.SymbolPath, @"C:\symbols");
        Assert.Throws<ArgumentException>(() => rewriter.Rewrite(
            "load_trace",
            new JsonObject { ["path"] = wrongKind }));

        using var restarted = new TypedAliasRegistry(Enumerable.Repeat((byte)7, 32).ToArray());
        Assert.False(restarted.TryResolve(SensitiveFieldKind.TracePath, alias, out _));
    }

    [Fact]
    public void PrivacyLogSink_BuffersFragmentedLinesBeforePathRedaction()
    {
        using var aliases = new TypedAliasRegistry(Enumerable.Repeat((byte)9, 32).ToArray());
        var redactor = new ToolPrivacyRedactor(
            ToolPrivacyMode.Paths,
            ToolPrivacyTaxonomy.Default,
            aliases);
        var destination = new StringWriter();
        using (var sink = new PrivacyLogSink(ToolPrivacyMode.Paths, redactor, destination))
        {
            sink.Writer.Write(@"symbol lookup C:\Users\alice\WPR ");
            sink.Writer.WriteLine(@"Files\customer secret.pdb failed");
            sink.Writer.Flush();
        }

        var log = destination.ToString();
        Assert.Contains("alias_file_path_", log, StringComparison.Ordinal);
        Assert.DoesNotContain("alice", log, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("customer secret.pdb", log, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void StrictPrivacyLogSink_DoesNotTrustUntypedDiagnosticText()
    {
        using var aliases = new TypedAliasRegistry(Enumerable.Repeat((byte)10, 32).ToArray());
        var redactor = new ToolPrivacyRedactor(
            ToolPrivacyMode.Strict,
            ToolPrivacyTaxonomy.Default,
            aliases);
        var destination = new StringWriter();
        using (var sink = new PrivacyLogSink(ToolPrivacyMode.Strict, redactor, destination))
            sink.Writer.WriteLine("private-module.pdb token=customer-secret");

        Assert.Equal("[redacted-diagnostic]" + Environment.NewLine, destination.ToString());
    }

    [Fact]
    public void FinalFitter_RegeneratesTextFromTheSameStrictlyRedactedObject()
    {
        using var aliases = new TypedAliasRegistry(Enumerable.Repeat((byte)8, 32).ToArray());
        var tool = Tool("find_marker");
        var arguments = new Dictionary<string, JsonElement>(StringComparer.Ordinal)
        {
            ["path"] = JsonSerializer.SerializeToElement("trc_0123456789abcdef0123456789abcdef"),
            ["nameSubstring"] = JsonSerializer.SerializeToElement("event"),
            ["mode"] = JsonSerializer.SerializeToElement("rows"),
            ["top"] = JsonSerializer.SerializeToElement(10),
        };
        var response = new MarkerSearchResponse(
            "rows",
            1,
            Counts: null,
            Rows:
            [
                new MarkerRow(1, "provider", "event", "process", 7,
                    new Dictionary<string, string> { ["CommandLine"] = @"C:\secret\customer.txt --token abc" }),
            ],
            CapabilityStatus: "observed",
            MatchedEventCount: 1,
            Warnings: Array.Empty<string>());
        var plan = new ReviewedToolOutcomeAdapterRegistry(Catalog.Tools).Plan(tool, arguments);
        var reviewed = plan.Adapt(JsonSerializer.SerializeToNode(response, McpJsonUtilities.DefaultOptions)!);
        var assessments = tool.Capabilities.Select(capability => Catalog.EvaluatorRegistry.EvaluateTool(
            tool,
            capability,
            reviewed.Domain as JsonObject,
            reviewed.Outcome,
            readyFacts: null,
            failed: false)).ToArray();
        var envelope = ToolEnvelopeProjection.Success(
            tool,
            response,
            reviewed,
            plan.PublicArguments,
            assessments);
        var projected = ToolWireJson.ProjectEnvelope(envelope, tool.OutputDataType);
        var fitter = new ToolResponseFrameFitter(
            ToolResponseBudgetOptions.Default,
            new ToolPrivacyRedactor(ToolPrivacyMode.Strict, ToolPrivacyTaxonomy.Default, aliases));

        var fitted = fitter.Fit(
            new RequestId("privacy-mirror"),
            projected,
            ToolOutputSchemaFactory.CreateEnvelopeSchema<MarkerSearchResponse>(),
            tool,
            arguments);
        var structured = JsonNode.Parse(fitted.Result.StructuredContent!.Value.GetRawText())!;
        var textBlock = Assert.Single(fitted.Result.Content);
        var text = JsonNode.Parse(Assert.IsType<TextContentBlock>(textBlock).Text)!;

        Assert.True(JsonNode.DeepEquals(structured, text));
        Assert.DoesNotContain("customer.txt", structured.ToJsonString(), StringComparison.OrdinalIgnoreCase);
        Assert.Equal(
            "[redacted]",
            structured["data"]!["rows"]![0]!["fields"]![0]!["value"]!.GetValue<string>());
    }

    private static ActiveToolDefinition Tool(string name) =>
        Catalog.Tools.Single(tool => tool.ToolName == name);

    private static async Task<CallToolResult> InvokeContractPage(
        McpServerTool tool,
        McpServer server,
        string toolName,
        int page,
        ToolPrivacyMode mode)
    {
        var parameters = new CallToolRequestParams
        {
            Name = "get_tool_contract",
            Arguments = new Dictionary<string, JsonElement>
            {
                ["toolName"] = JsonSerializer.SerializeToElement(toolName),
                ["page"] = JsonSerializer.SerializeToElement(page),
            },
        };
        var request = new JsonRpcRequest
        {
            Id = new RequestId($"privacy-contract-{mode}-{page}"),
            Method = RequestMethods.ToolsCall,
            Params = JsonSerializer.SerializeToNode(parameters, McpJsonUtilities.DefaultOptions),
        };
        return await tool.InvokeAsync(
            new RequestContext<CallToolRequestParams>(server, request, parameters),
            CancellationToken.None);
    }

    private static string Sha256(string value) => Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(value)))
        .ToLowerInvariant();

    private static JsonObject MarkerEnvelope(string payload) => new()
    {
        ["data"] = new JsonObject
        {
            ["rows"] = new JsonArray(new JsonObject
            {
                ["fields"] = new JsonArray(new JsonObject
                {
                    ["name"] = "Message",
                    ["value"] = payload,
                }),
            }),
        },
    };
}
