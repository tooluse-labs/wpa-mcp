using System.ComponentModel.DataAnnotations;
using System.Reflection;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using WpaMcp.Core;
using WpaMcp.Core.Catalog;
using WpaMcp.Output;

namespace WpaMcp.Tests;

public sealed class ToolOutputSchemaTests
{
    [Fact]
    public void EnvelopeSchema_RequiresEveryPropertyAndClosesEveryObject()
    {
        var schema = ToolOutputSchemaFactory.CreateEnvelopeSchema<SchemaData>();
        var violations = ToolOutputSchemaLinter.LintSchema(schema);

        Assert.Empty(violations);
        Assert.False(schema["additionalProperties"]!.GetValue<bool>());
        var properties = schema["properties"]!.AsObject();
        var required = schema["required"]!.AsArray().Select(value => value!.GetValue<string>()).ToArray();
        Assert.Equal(properties.Count, required.Length);
        Assert.Equal(properties.Select(property => property.Key).ToHashSet(StringComparer.Ordinal),
            required.ToHashSet(StringComparer.Ordinal));
        AssertNullable(properties["data"]!);
        AssertNullable(properties["error"]!);
        AssertNullable(properties["traceRef"]!);
        AssertNullable(properties["noData"]!);

        var sectionItems = Item(properties["sections"]!);
        Assert.False(sectionItems["additionalProperties"]!.GetValue<bool>());
        var sectionProperties = sectionItems["properties"]!.AsObject();
        var sectionRequired = sectionItems["required"]!.AsArray()
            .Select(value => value!.GetValue<string>()).ToHashSet(StringComparer.Ordinal);
        Assert.Contains("requested", sectionRequired);
        Assert.Contains("totalAvailable", sectionRequired);
        Assert.Contains("nextCursor", sectionRequired);
        Assert.Contains("truncationReason", sectionRequired);
        Assert.Contains("noData", sectionRequired);
        Assert.Contains("measurementBasis", sectionRequired);
        Assert.Contains("relationship", sectionRequired);
        Assert.Contains("conclusionStatus", sectionRequired);
        AssertNullable(sectionProperties["requested"]!);
        AssertNullable(sectionProperties["nextCursor"]!);
        AssertNullable(sectionProperties["noData"]!);
    }

    [Fact]
    public void EnvelopeSchema_UsesClosedStableStringEnums()
    {
        var schema = ToolOutputSchemaFactory.CreateEnvelopeSchema<SchemaData>();
        var properties = schema["properties"]!.AsObject();

        Assert.Equal(
            new[] { "succeeded", "partial", "failed" },
            properties["status"]!["enum"]!.AsArray().Select(value => value!.GetValue<string>()));

        var evidenceItems = Item(Props(NonNull(properties["evidenceBoundary"]!))["items"]!);
        Assert.True(evidenceItems["properties"]!.AsObject().ContainsKey("sections"));
        Assert.False(evidenceItems["properties"]!.AsObject().ContainsKey("section"));
        var relationship = evidenceItems["properties"]!["relationship"]!["enum"]!.AsArray()
            .Select(value => value!.GetValue<string>()).ToArray();
        Assert.Equal(new[] { "descriptive", "temporal", "association", "attribution", "causal" }, relationship);
    }

    [Fact]
    public void SchemaLinter_DetectsRequiredAndAdditionalPropertiesRegressions()
    {
        var schema = ToolOutputSchemaFactory.CreateEnvelopeSchema<SchemaData>();
        schema["required"]!.AsArray().RemoveAt(0);
        schema["additionalProperties"] = true;

        var violations = ToolOutputSchemaLinter.LintSchema(schema);

        Assert.Contains(violations, violation => violation.Code == "require_all_properties" && violation.Path == "$");
        Assert.Contains(violations, violation => violation.Code == "additional_properties" && violation.Path == "$");
    }

    [Fact]
    public void SchemaFactory_RejectsArbitraryObjectAndDictionaryData()
    {
        var objectError = Assert.Throws<InvalidOperationException>(() =>
            ToolOutputSchemaFactory.CreateEnvelopeSchema<ArbitraryObjectData>());
        Assert.Contains("arbitrary_object", objectError.Message, StringComparison.Ordinal);

        var dictionaryError = Assert.Throws<InvalidOperationException>(() =>
            ToolOutputSchemaFactory.CreateEnvelopeSchema<DictionaryData>());
        Assert.Contains("arbitrary_object", dictionaryError.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void SchemaFactory_ProjectsAllInt64ValuesToCanonicalDecimalStrings()
    {
        var unsignedSchema = ToolOutputSchemaFactory.CreateEnvelopeSchema<UnsignedIdData>();
        var unsigned = Props(NonNull(unsignedSchema["properties"]!["data"]!))["connectionId"]!;
        Assert.Equal("string", unsigned["type"]!.GetValue<string>());
        Assert.Equal("^(0|[1-9][0-9]*)$", unsigned["pattern"]!.GetValue<string>());

        var longSchema = ToolOutputSchemaFactory.CreateEnvelopeSchema<LongIdData>();
        var signed = Props(NonNull(longSchema["properties"]!["data"]!))["connectionId"]!;
        Assert.Equal("string", signed["type"]!.GetValue<string>());
        Assert.Equal("^(0|-?[1-9][0-9]*)$", signed["pattern"]!.GetValue<string>());

        var schema = ToolOutputSchemaFactory.CreateEnvelopeSchema<StringIdData>();
        Assert.Empty(ToolOutputSchemaLinter.LintSchema(schema));
        var dataSchema = NonNull(schema["properties"]!["data"]!);
        Assert.Equal("string", Props(dataSchema)["connectionId"]!["type"]!.GetValue<string>());
    }

    [Fact]
    public void ApprovedLegacyConnId_IsTheOnlyBoundedNumericCompatibilityProjection()
    {
        var schema = ToolOutputSchemaFactory.CreateEnvelopeSchema<NetConnectionsResponse>();
        var data = NonNull(schema["properties"]!["data"]!);
        var connections = Props(data)["connections"]!;
        var row = Item(connections);
        var connId = NonNull(Props(row)["connId"]!);

        Assert.Equal("integer", connId["type"]!.GetValue<string>());
        Assert.Equal(0, connId["minimum"]!.GetValue<int>());
        Assert.Equal(
            checked((long)PublicIdentifierFormatter.JavaScriptMaxSafeInteger),
            connId["maximum"]!.GetValue<long>());
        Assert.Equal("string", Props(row)["connIdText"]!["type"]!.GetValue<string>());
        Assert.Equal("string", Props(row)["connIdLegacyStatus"]!["type"]!.GetValue<string>());
    }

    [Fact]
    public void ReviewedDictionaries_AreClosedDeterministicRowsInSchemas()
    {
        var markerSchema = ToolOutputSchemaFactory.CreateEnvelopeSchema<MarkerSearchResponse>();
        var markerData = NonNull(markerSchema["properties"]!["data"]!);
        var markerRows = Item(Props(markerData)["rows"]!);
        var fields = NonNull(Props(markerRows)["fields"]!);
        Assert.Equal("array", fields["type"]!.GetValue<string>());
        var fieldRow = Item(fields);
        Assert.False(fieldRow["additionalProperties"]!.GetValue<bool>());
        Assert.Equal(new[] { "name", "value" }, fieldRow["required"]!.AsArray().Select(node => node!.GetValue<string>()));
    }

    [Fact]
    public void EveryActiveToolReturnType_HasAClosedEnvelopeSchema()
    {
        var catalog = ActiveToolCatalog.LoadAndValidate();

        Assert.All(catalog.Tools, tool =>
        {
            var schema = ToolOutputSchemaFactory.CreateEnvelopeSchema(tool.OutputDataType);
            Assert.Empty(ToolOutputSchemaLinter.LintReviewedNumericClosure(schema));
        });
    }

    [Fact]
    public void EveryActiveOutputContract_UsesReachableSafeLocalDefinitionsAndLeanDiscoveryFitsFrameCap()
    {
        var catalog = ActiveToolCatalog.LoadAndValidate();
        var outputContracts = catalog.OutputContracts;

        Assert.Equal(61, outputContracts.Count);
        Assert.All(catalog.Tools, tool =>
        {
            var schema = outputContracts[tool.ToolName].ParseSchema();
            Assert.Equal(
                "https://json-schema.org/draft/2020-12/schema",
                schema["$schema"]!.GetValue<string>());
            Assert.NotEmpty(schema["properties"]!.AsObject());
            Assert.NotEmpty(schema["$defs"]!.AsObject());
            Assert.All(schema["$defs"]!.AsObject(), definition =>
                Assert.Matches("^[A-Za-z_][A-Za-z0-9_.-]{0,127}$", definition.Key));
            Assert.NotEmpty(CollectReferences(schema));
            Assert.All(CollectReferences(schema), reference =>
                Assert.StartsWith("#/$defs/", reference, StringComparison.Ordinal));
            Assert.Empty(ToolOutputSchemaLinter.LintSchema(schema));
        });

        var protocolTools = catalog.CreateProtocolTools(
            new DeferredCatalogServiceProvider());
        Assert.All(protocolTools, tool => Assert.Null(tool.OutputSchema));
        var preflight = ToolsListPageFitter.Preflight(
            protocolTools,
            ToolsListPaginationOptions.HardMaxResponseFrameBytes);
        Assert.True(
            preflight.LargestSingleToolFrameBytes <
            ToolsListPaginationOptions.HardMaxResponseFrameBytes,
            $"Largest lean discovery frame was {preflight.LargestSingleToolFrameBytes} bytes.");
    }

    [Fact]
    public void SchemaFactory_DeduplicatesReferencesDeterministicallyAndPreservesAnnotations()
    {
        var first = ToolOutputSchemaFactory.CreateEnvelopeSchema<ReferenceDedupData>();
        var second = ToolOutputSchemaFactory.CreateEnvelopeSchema<ReferenceDedupData>();

        Assert.Equal(first.ToJsonString(), second.ToJsonString());
        var dataProperties = Props(NonNull(first["properties"]!["data"]!));
        var primary = dataProperties["primary"]!.AsObject();
        var secondary = dataProperties["secondary"]!.AsObject();
        var rowItems = NonNull(dataProperties["rows"]!)["items"]!.AsObject();
        Assert.Equal(primary["$ref"]!.GetValue<string>(), secondary["$ref"]!.GetValue<string>());
        Assert.Equal(primary["$ref"]!.GetValue<string>(), rowItems["$ref"]!.GetValue<string>());
        Assert.Equal("Primary row.", primary["description"]!.GetValue<string>());
        Assert.True(primary["deprecated"]!.GetValue<bool>());
        Assert.Equal("Use secondary.", primary["x-deprecationMessage"]!.GetValue<string>());

        var reparsed = JsonNode.Parse(first.ToJsonString())!.AsObject();
        var reparsedPrimary = Props(NonNull(reparsed["properties"]!["data"]!))["primary"]!.AsObject();
        Assert.Equal("Primary row.", reparsedPrimary["description"]!.GetValue<string>());
        Assert.True(reparsedPrimary["deprecated"]!.GetValue<bool>());
        Assert.Empty(ToolOutputSchemaLinter.LintSchema(reparsed));
    }

    [Fact]
    public void LocalDefinitionLinter_RejectsDanglingCyclicEscapingAndUnreachableRefs()
    {
        static JsonObject WithProbe(JsonObject schema, JsonObject probe)
        {
            schema["properties"]!.AsObject()["refProbe"] = probe;
            schema["required"]!.AsArray().Add("refProbe");
            return schema;
        }

        var dangling = WithProbe(
            ToolOutputSchemaFactory.CreateEnvelopeSchema<SchemaData>(),
            new JsonObject { ["$ref"] = "#/$defs/missing" });
        Assert.Contains(
            ToolOutputSchemaLinter.LintSchema(dangling),
            violation => violation.Code == "dangling_reference");
        Assert.Contains(
            ToolWireSchemaValidator.Validate(new JsonObject(), dangling),
            failure => failure.Message.Contains("dangling_reference", StringComparison.Ordinal));

        var escaping = WithProbe(
            ToolOutputSchemaFactory.CreateEnvelopeSchema<SchemaData>(),
            new JsonObject { ["$ref"] = "https://example.invalid/schema" });
        Assert.Contains(
            ToolOutputSchemaLinter.LintSchema(escaping),
            violation => violation.Code == "escaping_reference");

        var cyclic = ToolOutputSchemaFactory.CreateEnvelopeSchema<SchemaData>();
        cyclic["$defs"]!.AsObject()["cycle_a"] =
            new JsonObject { ["$ref"] = "#/$defs/cycle_b" };
        cyclic["$defs"]!.AsObject()["cycle_b"] =
            new JsonObject { ["$ref"] = "#/$defs/cycle_a" };
        WithProbe(cyclic, new JsonObject { ["$ref"] = "#/$defs/cycle_a" });
        Assert.Contains(
            ToolOutputSchemaLinter.LintSchema(cyclic),
            violation => violation.Code == "cyclic_reference");

        var unreachable = ToolOutputSchemaFactory.CreateEnvelopeSchema<SchemaData>();
        unreachable["$defs"]!.AsObject()["orphan"] =
            new JsonObject { ["type"] = "string" };
        Assert.Contains(
            ToolOutputSchemaLinter.LintSchema(unreachable),
            violation => violation.Code == "unreachable_definition");

        var unreachableCycle = ToolOutputSchemaFactory.CreateEnvelopeSchema<SchemaData>();
        unreachableCycle["$defs"]!.AsObject()["orphan_a"] =
            new JsonObject { ["$ref"] = "#/$defs/orphan_b" };
        unreachableCycle["$defs"]!.AsObject()["orphan_b"] =
            new JsonObject { ["$ref"] = "#/$defs/orphan_a" };
        var unreachableCycleViolations = ToolOutputSchemaLinter.LintSchema(unreachableCycle);
        Assert.Contains(unreachableCycleViolations, violation => violation.Code == "cyclic_reference");
        Assert.Contains(unreachableCycleViolations, violation => violation.Code == "unreachable_definition");
    }

    [Theory]
    [InlineData("https://example.invalid/schema")]
    [InlineData("#anchor")]
    [InlineData("#/properties/value")]
    [InlineData("#/$defs/value/child")]
    [InlineData("#/$defs/value%2Fchild")]
    [InlineData("#/$defs/value~1child")]
    [InlineData("#/$defs/1value")]
    [InlineData("#/$defs/value~2child")]
    public void LocalDefinitionLinter_RejectsNonPortableOrEscapingReferences(string reference)
    {
        var schema = ToolOutputSchemaFactory.CreateEnvelopeSchema<SchemaData>();
        AddRequiredProbe(schema, new JsonObject { ["$ref"] = reference });

        Assert.Contains(
            ToolOutputSchemaLinter.LintSchema(schema),
            violation => violation.Code == "escaping_reference");
    }

    [Fact]
    public void LocalDefinitionLinter_RejectsUnsafeScopesNamesSiblingsAndDialect()
    {
        var schema = ToolOutputSchemaFactory.CreateEnvelopeSchema<SchemaData>();
        var definitionName = schema["$defs"]!.AsObject().First().Key;
        AddRequiredProbe(schema, new JsonObject
        {
            ["$ref"] = "#/$defs/" + definitionName,
            ["type"] = "object",
            ["description"] = 42,
        });
        schema["$anchor"] = "root";
        schema["$schema"] = "https://json-schema.org/draft/2019-09/schema";
        schema["$defs"]!.AsObject()["unsafe/name"] = new JsonObject { ["type"] = "string" };
        schema["$defs"]![definitionName]!["$defs"] = new JsonObject();

        var violations = ToolOutputSchemaLinter.LintSchema(schema);

        Assert.Contains(violations, violation => violation.Code == "unsupported_reference_sibling");
        Assert.Contains(violations, violation => violation.Code == "invalid_reference_annotation");
        Assert.Contains(violations, violation => violation.Code == "unsupported_reference_scope");
        Assert.Contains(violations, violation => violation.Code == "nested_definitions");
        Assert.Contains(violations, violation => violation.Code == "unsafe_definition_name");
        Assert.Contains(violations, violation => violation.Code == "invalid_schema_dialect");
    }

    [Fact]
    public void ReferencedObjectAndArrayItems_EnforceRequiredAndAdditionalPropertyClosure()
    {
        var rowReference = new JsonObject { ["$ref"] = "#/$defs/row" };
        var schema = new JsonObject
        {
            ["$schema"] = "https://json-schema.org/draft/2020-12/schema",
            ["x-wpa-numeric-semantics"] = new JsonObject(),
            ["type"] = "object",
            ["additionalProperties"] = false,
            ["properties"] = new JsonObject
            {
                ["row"] = rowReference,
                ["rows"] = new JsonObject
                {
                    ["type"] = "array",
                    ["items"] = rowReference.DeepClone(),
                },
            },
            ["required"] = new JsonArray("row", "rows"),
            ["$defs"] = new JsonObject
            {
                ["row"] = new JsonObject
                {
                    ["type"] = "object",
                    ["additionalProperties"] = false,
                    ["properties"] = new JsonObject
                    {
                        ["name"] = new JsonObject { ["type"] = "string" },
                    },
                    ["required"] = new JsonArray("name"),
                },
            },
        };
        var valid = JsonNode.Parse("""{"row":{"name":"a"},"rows":[{"name":"b"}]}""")!;
        Assert.Empty(ToolWireSchemaValidator.Validate(valid, schema));

        var missing = JsonNode.Parse("""{"row":{},"rows":[{"name":"b"}]}""")!;
        Assert.Contains(
            ToolWireSchemaValidator.Validate(missing, schema),
            failure => failure.Path == "$.row.name" &&
                failure.Message.Contains("required", StringComparison.Ordinal));

        var additional = JsonNode.Parse(
            """{"row":{"name":"a"},"rows":[{"name":"b","extra":true}]}""")!;
        Assert.Contains(
            ToolWireSchemaValidator.Validate(additional, schema),
            failure => failure.Path == "$.rows[0].extra" &&
                failure.Message.Contains("additional", StringComparison.Ordinal));
    }

    [Fact]
    public void NumericRegistry_RejectsAnUnreferencedSemanticEntry()
    {
        var schema = ToolOutputSchemaFactory.CreateEnvelopeSchema<SchemaData>();
        var registry = schema["x-wpa-numeric-semantics"]!.AsObject();
        registry["orphan"] = registry.First().Value!.DeepClone();

        Assert.Contains(
            ToolOutputSchemaLinter.LintSchema(schema),
            violation => violation.Code == "unreachable_numeric_semantics" &&
                violation.Path.EndsWith(".orphan", StringComparison.Ordinal));
    }

    [Fact]
    public void SchemaFactory_ContinuesToRejectRecursiveDtoGraphs()
    {
        var error = Assert.Throws<InvalidOperationException>(() =>
            ToolOutputSchemaFactory.CreateEnvelopeSchema<RecursiveData>());

        Assert.Contains("recursive_object", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void LocalDefinitionSchema_ValidatesEnvelopeAfterJsonRoundTrip()
    {
        var catalog = ActiveToolCatalog.LoadAndValidate();
        var tool = Assert.Single(catalog.Tools, candidate =>
            candidate.ToolName == "list_capabilities");
        var assessment = catalog.EvaluatorRegistry.EvaluateTool(
            tool,
            Assert.Single(tool.Capabilities),
            domain: null,
            outcome: null,
            readyFacts: null,
            failed: true);
        var envelope = ToolEnvelopeProjection.Failure(
            tool,
            new ToolError("analysis_failed", "synthetic round-trip", false),
            arguments: null,
            [assessment]);
        var wire = ToolWireJson.ProjectEnvelope(envelope, tool.OutputDataType);
        var schema = ToolOutputSchemaFactory.CreateEnvelopeSchema(tool.OutputDataType);
        var roundTripped = JsonNode.Parse(wire.ToJsonString())!;

        Assert.Empty(ToolWireSchemaValidator.Validate(roundTripped, schema));
    }

    [Fact]
    public void StrictNumericClosure_RejectsAnHonestButUnreviewedField()
    {
        var schema = ToolOutputSchemaFactory.CreateEnvelopeSchema<SchemaData>();

        Assert.Empty(ToolOutputSchemaLinter.LintSchema(schema));
        Assert.Contains(
            ToolOutputSchemaLinter.LintReviewedNumericClosure(schema),
            violation => violation.Code == "unreviewed_numeric_semantics" &&
                violation.Path.EndsWith(".count", StringComparison.Ordinal));
    }

    [Fact]
    public void WireProjection_PreservesInt64BoundariesAcrossJsonRoundTrip()
    {
        var data = new WideIntegerData(
            9_007_199_254_740_991,
            9_007_199_254_740_992,
            long.MaxValue,
            long.MinValue,
            ulong.MaxValue);

        var projected = ToolWireJson.Project(data, typeof(WideIntegerData))!.AsObject();
        var reparsed = JsonNode.Parse(projected.ToJsonString())!.AsObject();

        Assert.Equal("9007199254740991", reparsed["belowBoundary"]!.GetValue<string>());
        Assert.Equal("9007199254740992", reparsed["atBoundary"]!.GetValue<string>());
        Assert.Equal("9223372036854775807", reparsed["signedMaximum"]!.GetValue<string>());
        Assert.Equal("-9223372036854775808", reparsed["signedMinimum"]!.GetValue<string>());
        Assert.Equal("18446744073709551615", reparsed["unsignedMaximum"]!.GetValue<string>());
    }

    [Fact]
    public void WireProjection_EnforcesLegacySafeIntegerSiblingContractAtRuntime()
    {
        var safe = new SafeCompatibilityData(
            "9007199254740991",
            9_007_199_254_740_991,
            "exact_safe_integer_deprecated");
        var unsafeExact = new SafeCompatibilityData(
            "9007199254740992",
            null,
            "null_unsafe_integer_deprecated");

        Assert.Equal(9_007_199_254_740_991UL,
            ToolWireJson.Project(safe, typeof(SafeCompatibilityData))!["connectionId"]!.GetValue<ulong>());
        Assert.Null(ToolWireJson.Project(unsafeExact, typeof(SafeCompatibilityData))!["connectionId"]);

        Assert.Throws<InvalidOperationException>(() => ToolWireJson.Project(
            new SafeCompatibilityData(ulong.MaxValue.ToString(), ulong.MaxValue, "exact_safe_integer_deprecated"),
            typeof(SafeCompatibilityData)));
        Assert.Throws<InvalidOperationException>(() => ToolWireJson.Project(
            new SafeCompatibilityData("1", null, "null_unsafe_integer_deprecated"),
            typeof(SafeCompatibilityData)));
    }

    [Fact]
    public void WireProjection_ConvertsReviewedDictionariesToSortedClosedRows()
    {
        var marker = new MarkerRow(
            1,
            "provider",
            "event",
            "process",
            2,
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["z"] = "last",
                ["a"] = "first",
            });

        var projected = ToolWireJson.Project(marker, typeof(MarkerRow))!.AsObject();
        var rows = projected["fields"]!.AsArray();
        Assert.Equal("a", rows[0]!["name"]!.GetValue<string>());
        Assert.Equal("first", rows[0]!["value"]!.GetValue<string>());
        Assert.Equal("z", rows[1]!["name"]!.GetValue<string>());
    }

    [Fact]
    public void WireProjection_OrdersPerPidDictionaryRowsNumerically()
    {
        var empty = new CpuTopFunctionsResponse(
            Array.Empty<CpuFunctionRow>(),
            new SymbolStats(0, 0, null, Array.Empty<UnresolvedModule>()),
            Array.Empty<string>());
        var batch = new CpuTopFunctionsBatchResponse(
            new Dictionary<int, CpuTopFunctionsResponse>
            {
                [10] = empty,
                [2] = empty,
            },
            Array.Empty<string>());

        var projected = ToolWireJson.Project(batch, typeof(CpuTopFunctionsBatchResponse))!.AsObject();
        var rows = projected["perPid"]!.AsArray();
        Assert.Equal(2, rows[0]!["pid"]!.GetValue<int>());
        Assert.Equal(10, rows[1]!["pid"]!.GetValue<int>());

        var schema = ToolOutputSchemaFactory.CreateEnvelopeSchema<CpuTopFunctionsBatchResponse>();
        var perPid = Props(NonNull(schema["properties"]!["data"]!))["perPid"]!;
        Assert.Equal("pid_ascending_numeric", perPid["x-ordering"]!.GetValue<string>());
    }

    [Fact]
    public void NumericSemantics_UseCompactRootRegistryAndNeverClaimNamingInferenceIsReviewed()
    {
        var schema = ToolOutputSchemaFactory.CreateEnvelopeSchema<SchemaData>();
        var data = NonNull(schema["properties"]!["data"]!);
        var count = Props(Item(Props(data)["rows"]!))["count"]!;
        var semanticId = count["x-numeric"]!.GetValue<string>();
        var semantics = NumericSemantics(schema, count, "x-numeric");

        Assert.StartsWith("sem_", semanticId, StringComparison.Ordinal);
        Assert.Equal("unknown", semantics["role"]!.GetValue<string>());
        Assert.Equal("unknown", semantics["unit"]!.GetValue<string>());
        Assert.Equal("unknown", semantics["precision"]!.GetValue<string>());
        Assert.Equal("unreviewed_unknown", semantics["source"]!.GetValue<string>());
        Assert.Null(semantics["denominator"]);

        var serialized = schema.ToJsonString();
        Assert.DoesNotContain("reviewed_naming_convention", serialized, StringComparison.Ordinal);
        Assert.DoesNotContain("documented_population_total", serialized, StringComparison.Ordinal);
        Assert.Equal(1, CountOccurrences(serialized, $"\"{semanticId}\":{{"));
    }

    [Fact]
    public void RatioAndPercentSemantics_ExposeExactPopulationDenominators()
    {
        var waitSchema = ToolOutputSchemaFactory.CreateEnvelopeSchema<WaitAnalysisResponse>();
        var waitData = NonNull(waitSchema["properties"]!["data"]!);
        var waitRow = Item(Props(waitData)["rows"]!);
        AssertMetric(
            waitSchema,
            Props(waitRow)["waitRatio"]!,
            "ratio",
            "blocked_us_divided_by_cpu_us",
            "cpuUs",
            "rounded_binary64",
            maximum: null);
        AssertMetric(
            waitSchema,
            Props(waitData)["scopedStackCoveragePct"]!,
            "percent",
            "ratio",
            "scopedCSwitches",
            "rounded_binary64",
            maximum: 100);

        var startupSchema = ToolOutputSchemaFactory.CreateEnvelopeSchema<DiagnoseSlowStartupResponse>();
        var startupData = NonNull(startupSchema["properties"]!["data"]!);
        var candidate = Item(Props(startupData)["candidates"]!);
        AssertMetric(
            startupSchema,
            Props(candidate)["startupWaitRatio"]!,
            "ratio",
            "observed_startup_wall_us_divided_by_startup_cpu_us",
            "startupCpuUs",
            "rounded_binary64",
            maximum: null);
        AssertMetric(
            startupSchema,
            Props(candidate)["lifetimeWaitRatio"]!,
            "ratio",
            "lifetime_wall_us_divided_by_lifetime_cpu_us",
            "lifetimeCpuUs",
            "rounded_binary64",
            maximum: null);

        var cpuSchema = ToolOutputSchemaFactory.CreateEnvelopeSchema<CpuPreciseResponse>();
        var cpuData = NonNull(cpuSchema["properties"]!["data"]!);
        var core = Item(Props(Item(Props(cpuData)["rows"]!))["topCores"]!);
        AssertMetric(
            cpuSchema,
            Props(core)["cpuPct"]!,
            "percent",
            "ratio",
            "containing_thread_cpu_us",
            "rounded_binary64",
            maximum: 100);
    }

    [Fact]
    public void ParserPdbAndFrameRates_KeepTheirDistinctDenominators()
    {
        var traceSchema = ToolOutputSchemaFactory.CreateEnvelopeSchema<LoadTraceResponse>();
        var traceData = NonNull(traceSchema["properties"]!["data"]!);
        var trace = NonNull(Props(traceData)["trace"]!);
        Assert.Equal(
            "rawEtwRecordCount",
            NumericSemantics(traceSchema, Props(trace)["parserCoverageRate"]!, "x-metric")
                ["denominator"]!.GetValue<string>());
        var symbols = NonNull(Props(traceData)["symbolStatus"]!);
        Assert.Equal(
            "moduleCount",
            NumericSemantics(traceSchema, Props(symbols)["completePdbIdentityRate"]!, "x-metric")
                ["denominator"]!.GetValue<string>());

        var stackSchema = ToolOutputSchemaFactory.CreateEnvelopeSchema<CpuTopFunctionsResponse>();
        var stackData = NonNull(stackSchema["properties"]!["data"]!);
        var stats = NonNull(Props(stackData)["stats"]!);
        Assert.Equal(
            "uniqueCodeFrameCount",
            NumericSemantics(
                stackSchema,
                Props(stats)["observedUniqueCodeFrameNameResolutionRate"]!,
                "x-metric")["denominator"]!.GetValue<string>());
        Assert.Equal(
            "totalCodeFrameMetric",
            NumericSemantics(
                stackSchema,
                Props(stats)["observedMetricWeightedCodeFrameNameResolutionRate"]!,
                "x-metric")["denominator"]!.GetValue<string>());

        var prepareSchema = ToolOutputSchemaFactory.CreateEnvelopeSchema<PrepareSymbolsResponse>();
        var prepareData = NonNull(prepareSchema["properties"]!["data"]!);
        Assert.Equal(
            "framesAttempted",
            NumericSemantics(
                prepareSchema,
                Props(prepareData)["frameResolutionRate"]!,
                "x-metric")["denominator"]!.GetValue<string>());
    }

    [Fact]
    public void OpaqueLocators_ExposeCanonicalPatternsAcrossEnvelopeAndActiveDtos()
    {
        var loadSchema = ToolOutputSchemaFactory.CreateEnvelopeSchema<LoadTraceResponse>();
        var load = NonNull(loadSchema["properties"]!["data"]!);
        var loadProperties = Props(load);
        AssertLocator(
            Props(NonNull(loadProperties["trace"]!))["path"]!,
            ToolOpaqueLocatorInputOverlay.TraceIdPattern,
            "trace_id");
        AssertLocator(
            loadProperties["traceId"]!,
            ToolOpaqueLocatorInputOverlay.TraceIdPattern,
            "trace_id");
        var traceRef = NonNull(loadSchema["properties"]!["traceRef"]!);
        AssertLocator(Props(traceRef)["traceId"]!,
            ToolOpaqueLocatorInputOverlay.TraceIdPattern, "trace_id");
        AssertLocator(Props(traceRef)["symbolContextId"]!,
            "^sym_[0-9a-f]{32}$", "symbol_context_id");
        AssertLocator(Props(Item(loadSchema["properties"]!["sections"]!))["nextCursor"]!,
            "^(?:qrc|cpc)_[0-9a-f]{32}$", "continuation_cursor");

        var prepare = NonNull(ToolOutputSchemaFactory
            .CreateEnvelopeSchema<PrepareSymbolsResponse>()["properties"]!["data"]!);
        AssertLocator(Props(prepare)["traceId"]!,
            ToolOpaqueLocatorInputOverlay.TraceIdPattern, "trace_id");
        AssertLocator(Props(prepare)["symbolContextId"]!,
            "^sym_[0-9a-f]{32}$", "symbol_context_id");

        var capabilities = NonNull(ToolOutputSchemaFactory
            .CreateEnvelopeSchema<ListCapabilitiesResponse>()["properties"]!["data"]!);
        AssertLocator(Props(capabilities)["nextCursor"]!,
            ToolOpaqueLocatorInputOverlay.CapabilityCursorPattern, "capability_cursor");

        var inspect = NonNull(ToolOutputSchemaFactory
            .CreateEnvelopeSchema<InspectTraceResponse>()["properties"]!["data"]!);
        AssertLocator(Props(inspect)["nextCursor"]!,
            ToolOpaqueLocatorInputOverlay.QueryCursorPattern, "query_result_cursor");
        AssertLocator(Props(NonNull(Props(inspect)["pageContext"]!))["traceGenerationId"]!,
            "^tgen_[0-9a-f]{32}$", "trace_generation_id");

        var catalog = ActiveToolCatalog.LoadAndValidate();
        var visited = new HashSet<Type>();
        var properties = new List<PropertyInfo>();
        foreach (var root in catalog.Tools.Select(tool => tool.OutputDataType))
            CollectOutputProperties(root, visited, properties);
        var locatorCandidates = properties.Where(property =>
            property.Name is "TraceId" or "SymbolContextId" or "TraceGenerationId" or "NextCursor" ||
            (property.DeclaringType == typeof(TraceMeta) && property.Name == nameof(TraceMeta.Path)));
        Assert.All(locatorCandidates, property =>
            Assert.NotNull(property.GetCustomAttribute<ToolOpaqueLocatorAttribute>()));
    }

    [Fact]
    public void PlannerExecutionTelemetry_DeclaresExactPerCallMeasurementScopes()
    {
        var inspectSchema = ToolOutputSchemaFactory.CreateEnvelopeSchema<InspectTraceResponse>();
        var inspectData = NonNull(inspectSchema["properties"]!["data"]!);
        var planner = NonNull(Props(inspectData)["plannerExecution"]!);
        var plannerProperties = Props(planner);

        AssertPlannerMetric(
            inspectSchema,
            plannerProperties["physicalTracePassCount"]!,
            unit: "physical_passes",
            precision: "exact",
            aggregation: "current_call_participating_pass_count",
            minimum: 0);
        AssertPlannerMetric(
            inspectSchema,
            plannerProperties["scannedEventCount"]!,
            unit: "materialized_logical_events",
            precision: "exact",
            aggregation: "generation_snapshot_count",
            minimum: 0);
        AssertPlannerMetric(
            inspectSchema,
            plannerProperties["matchedEventCount"]!,
            unit: "events",
            precision: "exact",
            aggregation: "current_call_scoped_match_count",
            minimum: 0);
        AssertPlannerMetric(
            inspectSchema,
            plannerProperties["physicalPassLimit"]!,
            unit: "physical_passes",
            precision: "exact",
            aggregation: "admission_upper_bound",
            minimum: 1);

        var phase = Item(plannerProperties["phaseDurations"]!);
        var duration = Props(phase)["durationUs"]!;
        AssertPlannerMetric(
            inspectSchema,
            duration,
            unit: "microseconds",
            precision: "rounded_integer",
            aggregation: "current_call_elapsed",
            minimum: null);
        Assert.Contains(
            "Monotonic elapsed time",
            duration["description"]!.GetValue<string>(),
            StringComparison.Ordinal);
        Assert.Contains(
            "rounded to the nearest integer microsecond",
            duration["description"]!.GetValue<string>(),
            StringComparison.Ordinal);
        Assert.Contains(
            "midpoint values away from zero",
            duration["description"]!.GetValue<string>(),
            StringComparison.Ordinal);

        AssertPlannerExecutionIsReachable<DiagnoseHighWaitResponse>();
        AssertPlannerExecutionIsReachable<DiagnoseSlowStartupResponse>();
        AssertPlannerExecutionIsReachable<DiagnoseWindowResponse>();
    }

    [Fact]
    public void NumericCollectionsAndDynamicMetrics_ExposeTheirUnitField()
    {
        var histogramSchema = ToolOutputSchemaFactory.CreateEnvelopeSchema<TimeHistogram>();
        var histogram = NonNull(histogramSchema["properties"]!["data"]!);
        var bucketItem = Item(Props(histogram)["buckets"]!);
        var bucketSemantics = NumericSemantics(histogramSchema, bucketItem, "x-metric");
        Assert.Equal("dynamic", bucketSemantics["unit"]!.GetValue<string>());
        Assert.Equal("unit", bucketSemantics["unitField"]!.GetValue<string>());
        Assert.Equal("bucket_sum", bucketSemantics["aggregation"]!.GetValue<string>());

        var evidenceSchema = ToolOutputSchemaFactory.CreateEnvelopeSchema<CompositeEvidence>();
        var evidence = NonNull(evidenceSchema["properties"]!["data"]!);
        var valueSemantics = NumericSemantics(
            evidenceSchema,
            Props(evidence)["metricValue"]!,
            "x-metric");
        Assert.Equal("dynamic", valueSemantics["unit"]!.GetValue<string>());
        Assert.Equal("unit", valueSemantics["unitField"]!.GetValue<string>());
        Assert.Equal("exact", valueSemantics["precision"]!.GetValue<string>());
    }

    [Fact]
    public void SchemaLinter_RejectsMissingRatioDenominatorAndDanglingSemanticId()
    {
        var missingDenominator = ToolOutputSchemaFactory.CreateEnvelopeSchema<WaitAnalysisResponse>();
        var waitData = NonNull(missingDenominator["properties"]!["data"]!);
        var waitRatio = Props(Item(Props(waitData)["rows"]!))["waitRatio"]!;
        NumericSemantics(missingDenominator, waitRatio, "x-metric")["denominator"] = null;
        Assert.Contains(
            ToolOutputSchemaLinter.LintSchema(missingDenominator),
            violation => violation.Code == "ratio_denominator_required");

        var dangling = ToolOutputSchemaFactory.CreateEnvelopeSchema<WaitAnalysisResponse>();
        var danglingData = NonNull(dangling["properties"]!["data"]!);
        NonNull(Props(Item(Props(danglingData)["rows"]!))["waitRatio"]!)["x-metric"] =
            "sem_missing";
        Assert.Contains(
            ToolOutputSchemaLinter.LintSchema(dangling),
            violation => violation.Code == "dangling_numeric_semantics");
    }

    [Fact]
    public void AmbiguousWaitRatioNames_AreDeprecatedAliasesOfAuthoritativeMetrics()
    {
        var processSchema = ToolOutputSchemaFactory.CreateEnvelopeSchema<ProcessListResponse>();
        var processData = NonNull(processSchema["properties"]!["data"]!);
        var processRow = Item(Props(processData)["rows"]!);
        AssertAlias(
            processSchema,
            processRow,
            "waitRatio",
            "wallToCpuRatio",
            "cpuUs",
            "wall_us_divided_by_cpu_us");

        var waitSchema = ToolOutputSchemaFactory.CreateEnvelopeSchema<WaitAnalysisResponse>();
        var waitData = NonNull(waitSchema["properties"]!["data"]!);
        var waitRow = Item(Props(waitData)["rows"]!);
        AssertAlias(
            waitSchema,
            waitRow,
            "waitRatio",
            "blockedToCpuRatio",
            "cpuUs",
            "blocked_us_divided_by_cpu_us");

        var highWaitSchema = ToolOutputSchemaFactory.CreateEnvelopeSchema<DiagnoseHighWaitResponse>();
        var highWaitData = NonNull(highWaitSchema["properties"]!["data"]!);
        var highWaitRow = Item(Props(highWaitData)["candidates"]!);
        AssertAlias(
            highWaitSchema,
            highWaitRow,
            "waitRatio",
            "blockedToCpuRatio",
            "totalCpuUs",
            "total_blocked_us_divided_by_total_cpu_us");

        var startupSchema = ToolOutputSchemaFactory.CreateEnvelopeSchema<DiagnoseSlowStartupResponse>();
        var startupData = NonNull(startupSchema["properties"]!["data"]!);
        var startupRow = Item(Props(startupData)["candidates"]!);
        AssertAlias(
            startupSchema,
            startupRow,
            "startupWaitRatio",
            "observedStartupWallToCpuRatio",
            "startupCpuUs",
            "observed_startup_wall_us_divided_by_startup_cpu_us");
        AssertAlias(
            startupSchema,
            startupRow,
            "lifetimeWaitRatio",
            "lifetimeWallToCpuRatio",
            "lifetimeCpuUs",
            "lifetime_wall_us_divided_by_lifetime_cpu_us");
    }

    [Fact]
    public void ObsoleteProperties_AreMarkedDeprecatedWithoutLosingNumericReplacementMetadata()
    {
        var startupSchema = ToolOutputSchemaFactory.CreateEnvelopeSchema<DiagnoseSlowStartupResponse>();
        var startup = NonNull(startupSchema["properties"]!["data"]!);
        var summary = Props(startup)["summary"]!;
        Assert.True(summary["deprecated"]!.GetValue<bool>());
        Assert.Contains(
            "Use structured Evidence",
            summary["x-deprecationMessage"]!.GetValue<string>(),
            StringComparison.Ordinal);

        var waitSchema = ToolOutputSchemaFactory.CreateEnvelopeSchema<WaitAnalysisResponse>();
        var wait = NonNull(waitSchema["properties"]!["data"]!);
        var row = Item(Props(wait)["rows"]!);
        var alias = NonNull(Props(row)["waitRatio"]!);
        Assert.True(alias["deprecated"]!.GetValue<bool>());
        Assert.Equal("blockedToCpuRatio", alias["x-replacedBy"]!.GetValue<string>());
        Assert.Empty(ToolOutputSchemaLinter.LintSchema(waitSchema));
    }

    private static void AssertAlias(
        JsonObject root,
        JsonNode containingObject,
        string alias,
        string replacement,
        string denominator,
        string aggregation)
    {
        var properties = Props(containingObject);
        var aliasSemantics = NumericSemantics(root, properties[alias]!, "x-metric");
        var replacementSemantics = NumericSemantics(root, properties[replacement]!, "x-metric");

        Assert.True(aliasSemantics["deprecatedAlias"]!.GetValue<bool>());
        Assert.Equal(replacement, aliasSemantics["replacement"]!.GetValue<string>());
        Assert.False(replacementSemantics["deprecatedAlias"]!.GetValue<bool>());
        Assert.Null(replacementSemantics["replacement"]);
        Assert.Equal(denominator, replacementSemantics["denominator"]!.GetValue<string>());
        Assert.Equal(aggregation, replacementSemantics["aggregation"]!.GetValue<string>());
        Assert.Equal("ratio", replacementSemantics["unit"]!.GetValue<string>());
        Assert.Null(replacementSemantics["maximum"]);
        var aliasSchema = NonNull(properties[alias]!);
        Assert.True(aliasSchema["deprecated"]!.GetValue<bool>());
        Assert.Equal(replacement, aliasSchema["x-replacedBy"]!.GetValue<string>());
    }

    private static void AssertMetric(
        JsonObject root,
        JsonNode property,
        string unit,
        string aggregation,
        string denominator,
        string precision,
        double? maximum)
    {
        var semantics = NumericSemantics(root, property, "x-metric");
        Assert.Equal("metric", semantics["role"]!.GetValue<string>());
        Assert.Equal(unit, semantics["unit"]!.GetValue<string>());
        Assert.Equal(aggregation, semantics["aggregation"]!.GetValue<string>());
        Assert.Equal(denominator, semantics["denominator"]!.GetValue<string>());
        Assert.Equal(precision, semantics["precision"]!.GetValue<string>());
        if (maximum is null)
            Assert.Null(semantics["maximum"]);
        else
            Assert.Equal(maximum.Value, semantics["maximum"]!.GetValue<double>());
    }

    private static void AssertPlannerMetric(
        JsonObject root,
        JsonNode property,
        string unit,
        string precision,
        string aggregation,
        double? minimum)
    {
        var semantics = NumericSemantics(root, property, "x-metric");
        Assert.Equal("metric", semantics["role"]!.GetValue<string>());
        Assert.Equal(unit, semantics["unit"]!.GetValue<string>());
        Assert.Equal(precision, semantics["precision"]!.GetValue<string>());
        Assert.Equal(aggregation, semantics["aggregation"]!.GetValue<string>());
        Assert.Null(semantics["denominator"]);
        Assert.Equal("explicit_attribute", semantics["source"]!.GetValue<string>());
        Assert.Null(semantics["maximum"]);
        if (minimum is null)
            Assert.Null(semantics["minimum"]);
        else
            Assert.Equal(minimum.Value, semantics["minimum"]!.GetValue<double>());
    }

    private static void AssertPlannerExecutionIsReachable<TData>() where TData : class
    {
        var schema = ToolOutputSchemaFactory.CreateEnvelopeSchema<TData>();
        var data = NonNull(schema["properties"]!["data"]!);
        Assert.NotNull(Props(data)["plannerExecution"]);
        Assert.Empty(ToolOutputSchemaLinter.LintReviewedNumericClosure(schema));
    }

    private static JsonObject NumericSemantics(JsonObject root, JsonNode property, string extension)
    {
        var leaf = NonNull(property);
        var semanticId = leaf[extension]!.GetValue<string>();
        return root["x-wpa-numeric-semantics"]![semanticId]!.AsObject();
    }

    private static void AssertLocator(JsonNode propertySchema, string pattern, string kind)
    {
        var nonNull = NonNull(propertySchema);
        Assert.Equal(pattern, nonNull["pattern"]!.GetValue<string>());
        Assert.Equal(kind, nonNull["x-opaqueLocator"]!.GetValue<string>());
    }

    private static void CollectOutputProperties(
        Type type,
        HashSet<Type> visited,
        List<PropertyInfo> properties)
    {
        type = Nullable.GetUnderlyingType(type) ?? type;
        if (type.IsArray)
        {
            CollectOutputProperties(type.GetElementType()!, visited, properties);
            return;
        }
        if (type.IsGenericType)
        {
            foreach (var argument in type.GetGenericArguments())
                CollectOutputProperties(argument, visited, properties);
            return;
        }
        if (type.Namespace != "WpaMcp.Output" || !visited.Add(type))
            return;
        foreach (var property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            properties.Add(property);
            CollectOutputProperties(property.PropertyType, visited, properties);
        }
    }

    private static int CountOccurrences(string value, string pattern)
    {
        var count = 0;
        for (var index = 0; (index = value.IndexOf(pattern, index, StringComparison.Ordinal)) >= 0;
             index += pattern.Length)
        {
            count++;
        }
        return count;
    }

    private static IReadOnlyList<string> CollectReferences(JsonNode node)
    {
        var references = new List<string>();
        Visit(node);
        return references;

        void Visit(JsonNode? candidate)
        {
            if (candidate is JsonObject value)
            {
                if (value["$ref"]?.GetValue<string>() is { } reference)
                    references.Add(reference);
                foreach (var property in value)
                    Visit(property.Value);
            }
            else if (candidate is JsonArray array)
            {
                foreach (var item in array)
                    Visit(item);
            }
        }
    }

    private static void AddRequiredProbe(JsonObject schema, JsonObject probe)
    {
        schema["properties"]!.AsObject()["refProbe"] = probe;
        schema["required"]!.AsArray().Add("refProbe");
    }

    private static void AssertNullable(JsonNode schema)
    {
        var alternatives = schema["anyOf"]!.AsArray();
        Assert.Equal(2, alternatives.Count);
        Assert.Single(alternatives, alternative => alternative!["type"]?.GetValue<string>() == "null");
    }

    private static JsonObject NonNull(JsonNode schema) =>
        OutputSchemaTestResolver.NonNull(schema);

    private static JsonObject Props(JsonNode schema) =>
        OutputSchemaTestResolver.Properties(schema);

    private static JsonObject Item(JsonNode schema) =>
        OutputSchemaTestResolver.Items(schema);

    public sealed record SchemaData(
        [property: JsonPropertyName("rows")] IReadOnlyList<SchemaRow> Rows,
        [property: JsonPropertyName("optionalLabel")] string? OptionalLabel);

    public sealed record SchemaRow([property: JsonPropertyName("count")] long Count);

    public sealed record ReferenceDedupData(
        [property: System.ComponentModel.Description("Primary row.")]
        [property: Obsolete("Use secondary.")]
        ReferenceDedupRow Primary,
        ReferenceDedupRow Secondary,
        IReadOnlyList<ReferenceDedupRow> Rows);

    public sealed record ReferenceDedupRow(string Value);

    public sealed class RecursiveData
    {
        public RecursiveData? Next { get; init; }
    }

    public sealed record ArbitraryObjectData([property: JsonPropertyName("payload")] object Payload);

    public sealed record DictionaryData(
        [property: JsonPropertyName("values")] IReadOnlyDictionary<string, string> Values);

    public sealed record UnsignedIdData([property: JsonPropertyName("connectionId")] ulong ConnectionId);

    public sealed record LongIdData([property: JsonPropertyName("connectionId")] long ConnectionId);

    public sealed record StringIdData([property: JsonPropertyName("connectionId")] string ConnectionId);

    public sealed record WideIntegerData(
        long BelowBoundary,
        long AtBoundary,
        long SignedMaximum,
        long SignedMinimum,
        ulong UnsignedMaximum);

    public sealed record SafeCompatibilityData(
        string ConnectionIdText,
        [property: Range(0d, 9_007_199_254_740_991d)]
        [property: ToolSafeIntegerCompatibility("ConnectionIdText", "ConnectionIdLegacyStatus")]
        ulong? ConnectionId,
        string ConnectionIdLegacyStatus);
}
