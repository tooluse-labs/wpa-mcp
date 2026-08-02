using System.Collections;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using WpaMcp.Core.Catalog;

namespace WpaMcp.Tests;

public sealed class ActiveReachableCollectionClosureTests
{
    private const string OutputNamespace = "WpaMcp.Output";
    private static readonly Regex SourceCapPattern = new(
        @"\.(?:Take|TakeLast)\s*\(|\bFirstOrDefault\s*\(\s*\)|\bMax[A-Za-z0-9_]*(?:Rows?|Samples?|Items?|Entries|Candidates|CollectionItems)\b|\bExcludedSampleLimit\b",
        RegexOptions.CultureInvariant);

    [Fact]
    public void ActiveReachableCollections_HaveReviewedFailClosedDispositions()
    {
        var catalog = ActiveToolCatalog.LoadAndValidate();
        var registry = LoadRegistry();
        var graph = BuildGraph(catalog);

        Assert.Equal("active-reachable-collection-closure.v1", registry.FormatVersion);
        Assert.Contains("not semantic inference", registry.RegistrySemantics, StringComparison.Ordinal);
        Assert.Equal(catalog.Tools.Count, registry.ExpectedCounts.ActiveToolCount);
        Assert.Equal(graph.ReachableTypes.Count, registry.ExpectedCounts.ReachableTypeCount);
        Assert.Equal(graph.CollectionProperties.Count, registry.ExpectedCounts.CollectionPropertyCount);
        Assert.Equal(graph.Occurrences.Count, registry.ExpectedCounts.CollectionOccurrenceCount);

        var proofDefinitions = registry.ProofDefinitions.ToDictionary(
            item => item.ProofMode,
            StringComparer.Ordinal);
        Assert.Equal(registry.ProofDefinitions.Count, proofDefinitions.Count);
        Assert.All(registry.ProofDefinitions, definition =>
        {
            Assert.False(string.IsNullOrWhiteSpace(definition.Owner));
            Assert.False(string.IsNullOrWhiteSpace(definition.SourceNote));
        });

        var dispositionByProperty = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var disposition in registry.PropertyDispositions)
        {
            Assert.True(
                proofDefinitions.ContainsKey(disposition.ProofMode),
                $"Unknown proof mode '{disposition.ProofMode}'.");
            Assert.NotEqual("manifest_pageable", disposition.ProofMode);
            foreach (var property in disposition.Properties)
            {
                Assert.True(
                    dispositionByProperty.TryAdd(property, disposition.ProofMode),
                    $"Collection property '{property}' has more than one reviewed disposition.");
            }
        }

        AssertSetEqual(
            graph.CollectionProperties,
            dispositionByProperty.Keys,
            "reachable collection properties");

        var classified = graph.Occurrences
            .Select(occurrence =>
            {
                var tool = catalog.Tools.Single(item =>
                    string.Equals(item.ToolName, occurrence.ToolName, StringComparison.Ordinal));
                var proofMode = tool.PageableSections.Contains(
                    occurrence.Path,
                    StringComparer.Ordinal)
                    ? "manifest_pageable"
                    : dispositionByProperty[occurrence.Property];
                Assert.NotEqual("manifest_only", proofMode);
                Assert.True(
                    proofDefinitions.ContainsKey(proofMode),
                    $"Occurrence '{occurrence.Id}' resolves to unknown proof mode '{proofMode}'.");
                return $"{occurrence.Id}|{proofMode}";
            })
            .Order(StringComparer.Ordinal)
            .ToArray();

        var classifiedOccurrenceHash = Sha256(classified);
        Assert.True(
            string.Equals(
                registry.ReviewedOccurrenceSetSha256,
                classifiedOccurrenceHash,
                StringComparison.Ordinal),
            $"Classified collection occurrence hash changed. Reviewed " +
            $"{registry.ReviewedOccurrenceSetSha256}; actual {classifiedOccurrenceHash}. " +
            "Review tool/path/property/proof changes before updating the registry.");

        var manifestedOccurrences = graph.Occurrences
            .Where(occurrence => catalog.Tools.Single(item =>
                    string.Equals(item.ToolName, occurrence.ToolName, StringComparison.Ordinal))
                .PageableSections.Contains(occurrence.Path, StringComparer.Ordinal))
            .Select(occurrence => $"{occurrence.ToolName}|{occurrence.Path}")
            .Order(StringComparer.Ordinal)
            .ToArray();
        var declaredPageableSections = catalog.Tools
            .SelectMany(tool => tool.PageableSections.Select(pointer =>
                $"{tool.ToolName}|{pointer}"))
            .Order(StringComparer.Ordinal)
            .ToArray();
        AssertSetEqual(
            declaredPageableSections,
            manifestedOccurrences,
            "manifest pageable sections backed by reachable collection occurrences");

        var actualProofCounts = classified
            .Select(item => item[(item.LastIndexOf('|') + 1)..])
            .GroupBy(item => item, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal);
        Assert.Equal(
            registry.ProofModeCounts.OrderBy(item => item.Key, StringComparer.Ordinal),
            actualProofCounts.OrderBy(item => item.Key, StringComparer.Ordinal));

        var sourceCapSites = FindSourceCapSites(LocateRepoRoot());
        Assert.False(string.IsNullOrWhiteSpace(registry.SourceCapReview.Owner));
        Assert.False(string.IsNullOrWhiteSpace(registry.SourceCapReview.SourceNote));
        Assert.Equal(registry.SourceCapReview.SiteCount, sourceCapSites.Count);
        var sourceCapHash = Sha256(sourceCapSites);
        Assert.True(
            string.Equals(
                registry.SourceCapReview.SiteSetSha256,
                sourceCapHash,
                StringComparison.Ordinal),
            $"Reviewed source-cap site hash changed. Reviewed " +
            $"{registry.SourceCapReview.SiteSetSha256}; actual {sourceCapHash}. " +
            "Review every added or removed cap site before updating the registry.");
    }

    private static CollectionGraph BuildGraph(ActiveToolCatalog catalog)
    {
        var reachableTypes = new HashSet<string>(StringComparer.Ordinal);
        var collectionProperties = new HashSet<string>(StringComparer.Ordinal);
        var occurrences = new List<CollectionOccurrence>();

        foreach (var tool in catalog.Tools)
        {
            Traverse(
                tool.ToolName,
                tool.OutputDataType,
                path: "",
                ancestors: new HashSet<Type>(),
                reachableTypes,
                collectionProperties,
                occurrences);
        }

        return new CollectionGraph(reachableTypes, collectionProperties, occurrences);
    }

    private static void Traverse(
        string toolName,
        Type type,
        string path,
        IReadOnlySet<Type> ancestors,
        ISet<string> reachableTypes,
        ISet<string> collectionProperties,
        ICollection<CollectionOccurrence> occurrences)
    {
        type = UnwrapNullable(type);
        if (!IsOutputDto(type))
            return;
        if (ancestors.Contains(type))
        {
            throw new InvalidOperationException(
                $"Reachable output DTO cycle requires an explicit traversal policy: {type.FullName}.");
        }

        reachableTypes.Add(type.FullName!);
        var nextAncestors = ancestors.Append(type).ToHashSet();
        foreach (var property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
                     .Where(property => property.GetMethod is not null && !property.GetMethod.IsStatic)
                     .OrderBy(property => property.Name, StringComparer.Ordinal))
        {
            var jsonName = property.GetCustomAttribute<JsonPropertyNameAttribute>()?.Name
                ?? JsonNamingPolicy.CamelCase.ConvertName(property.Name);
            var propertyPath = path + "/" + EscapePointerSegment(jsonName);
            var propertyId = $"{type.FullName}.{property.Name}";
            var isCollection = TryGetCollectionElementTypes(
                property.PropertyType,
                out var elementTypes);

            if (isCollection)
            {
                collectionProperties.Add(propertyId);
                occurrences.Add(new CollectionOccurrence(
                    toolName,
                    propertyPath,
                    propertyId));
            }

            var nestedPath = isCollection ? propertyPath + "/*" : propertyPath;
            var nestedTypes = isCollection
                ? elementTypes.SelectMany(FindOutputDtoTypes)
                : FindOutputDtoTypes(property.PropertyType);
            foreach (var nestedType in nestedTypes.Distinct())
            {
                Traverse(
                    toolName,
                    nestedType,
                    nestedPath,
                    nextAncestors,
                    reachableTypes,
                    collectionProperties,
                    occurrences);
            }
        }
    }

    private static bool TryGetCollectionElementTypes(Type type, out IReadOnlyList<Type> elementTypes)
    {
        type = UnwrapNullable(type);
        if (type == typeof(string))
        {
            elementTypes = [];
            return false;
        }
        if (type.IsArray)
        {
            elementTypes = [type.GetElementType()!];
            return true;
        }
        if (!typeof(IEnumerable).IsAssignableFrom(type))
        {
            elementTypes = [];
            return false;
        }

        var dictionary = type.GetInterfaces()
            .Append(type)
            .FirstOrDefault(candidate => candidate.IsGenericType &&
                (candidate.GetGenericTypeDefinition() == typeof(IDictionary<,>) ||
                 candidate.GetGenericTypeDefinition() == typeof(IReadOnlyDictionary<,>)));
        if (dictionary is not null)
        {
            elementTypes = [dictionary.GetGenericArguments()[1]];
            return true;
        }

        var enumerable = type.GetInterfaces()
            .Append(type)
            .FirstOrDefault(candidate => candidate.IsGenericType &&
                candidate.GetGenericTypeDefinition() == typeof(IEnumerable<>));
        elementTypes = enumerable is null
            ? []
            : [enumerable.GetGenericArguments()[0]];
        return true;
    }

    private static IEnumerable<Type> FindOutputDtoTypes(Type type)
    {
        type = UnwrapNullable(type);
        if (IsOutputDto(type))
            yield return type;
        if (type.IsArray)
        {
            foreach (var nested in FindOutputDtoTypes(type.GetElementType()!))
                yield return nested;
            yield break;
        }
        if (!type.IsGenericType)
            yield break;
        foreach (var argument in type.GetGenericArguments())
        {
            foreach (var nested in FindOutputDtoTypes(argument))
                yield return nested;
        }
    }

    private static bool IsOutputDto(Type type) =>
        string.Equals(type.Namespace, OutputNamespace, StringComparison.Ordinal) &&
        type.IsClass;

    private static Type UnwrapNullable(Type type) => Nullable.GetUnderlyingType(type) ?? type;

    private static string EscapePointerSegment(string value) =>
        value.Replace("~", "~0", StringComparison.Ordinal)
            .Replace("/", "~1", StringComparison.Ordinal);

    private static ClosureRegistry LoadRegistry()
    {
        var path = Path.Combine(
            LocateRepoRoot(),
            "tests",
            "WpaMcp.Tests",
            "ContractBaselines",
            "active-reachable-collection-closure.v1.json");
        Assert.True(File.Exists(path), $"Reviewed collection closure registry is missing: {path}");
        return JsonSerializer.Deserialize<ClosureRegistry>(
                   File.ReadAllText(path),
                   new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
               ?? throw new InvalidOperationException("Collection closure registry deserialized to null.");
    }

    private static void AssertSetEqual(
        IEnumerable<string> expected,
        IEnumerable<string> actual,
        string label)
    {
        var expectedSet = expected.ToHashSet(StringComparer.Ordinal);
        var actualSet = actual.ToHashSet(StringComparer.Ordinal);
        var missing = expectedSet.Except(actualSet, StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .Take(20)
            .ToArray();
        var stale = actualSet.Except(expectedSet, StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .Take(20)
            .ToArray();
        Assert.True(
            missing.Length == 0 && stale.Length == 0 && expectedSet.Count == actualSet.Count,
            $"{label} mismatch. Missing: [{string.Join(", ", missing)}]. " +
            $"Stale/unexpected: [{string.Join(", ", stale)}]. " +
            $"Expected count {expectedSet.Count}, actual count {actualSet.Count}.");
    }

    private static string Sha256(IEnumerable<string> values)
    {
        var canonical = string.Join("\n", values.Order(StringComparer.Ordinal));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)))
            .ToLowerInvariant();
    }

    private static IReadOnlyList<string> FindSourceCapSites(string repoRoot)
    {
        var sourceRoot = Path.Combine(repoRoot, "src", "WpaMcp");
        var occurrenceByToken = new Dictionary<string, int>(StringComparer.Ordinal);
        var sites = new List<string>();
        foreach (var file in Directory.EnumerateFiles(sourceRoot, "*.cs", SearchOption.AllDirectories)
                     .Order(StringComparer.Ordinal))
        {
            var relativePath = Path.GetRelativePath(repoRoot, file).Replace('\\', '/');
            foreach (var line in File.ReadLines(file))
            {
                foreach (Match match in SourceCapPattern.Matches(line))
                {
                    var token = Regex.Replace(match.Value, @"\s+", "");
                    var identity = $"{relativePath}|{token}";
                    var ordinal = occurrenceByToken.GetValueOrDefault(identity) + 1;
                    occurrenceByToken[identity] = ordinal;
                    sites.Add($"{identity}|occurrence={ordinal}");
                }
            }
        }
        return sites.Order(StringComparer.Ordinal).ToArray();
    }

    private static string LocateRepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "WpaMcp.sln")) &&
                File.Exists(Path.Combine(directory.FullName, "eng", "tool-contracts.v2.json")))
                return directory.FullName;
            directory = directory.Parent;
        }
        throw new DirectoryNotFoundException("Could not locate repository root.");
    }

    private sealed record CollectionOccurrence(string ToolName, string Path, string Property)
    {
        public string Id => $"{ToolName}|{Path}|{Property}";
    }

    private sealed record CollectionGraph(
        IReadOnlySet<string> ReachableTypes,
        IReadOnlySet<string> CollectionProperties,
        IReadOnlyList<CollectionOccurrence> Occurrences);

    private sealed class ClosureRegistry
    {
        public string FormatVersion { get; init; } = "";
        public string RegistrySemantics { get; init; } = "";
        public ExpectedCounts ExpectedCounts { get; init; } = new();
        public List<ProofDefinition> ProofDefinitions { get; init; } = [];
        public List<PropertyDisposition> PropertyDispositions { get; init; } = [];
        public Dictionary<string, int> ProofModeCounts { get; init; } = new(StringComparer.Ordinal);
        public string ReviewedOccurrenceSetSha256 { get; init; } = "";
        public SourceCapReview SourceCapReview { get; init; } = new();
    }

    private sealed class ExpectedCounts
    {
        public int ActiveToolCount { get; init; }
        public int ReachableTypeCount { get; init; }
        public int CollectionPropertyCount { get; init; }
        public int CollectionOccurrenceCount { get; init; }
    }

    private sealed class ProofDefinition
    {
        public string ProofMode { get; init; } = "";
        public string Owner { get; init; } = "";
        public string SourceNote { get; init; } = "";
    }

    private sealed class PropertyDisposition
    {
        public string ProofMode { get; init; } = "";
        public List<string> Properties { get; init; } = [];
    }

    private sealed class SourceCapReview
    {
        public string Owner { get; init; } = "";
        public string SourceNote { get; init; } = "";
        public int SiteCount { get; init; }
        public string SiteSetSha256 { get; init; } = "";
    }
}
