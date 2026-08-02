using System.Collections;
using System.Reflection;
using ModelContextProtocol.Server;
using WpaMcp.Output;
using WpaMcp.Tools;

namespace WpaMcp.Tests;

public sealed class CapabilityResourceCollectionClosureTests
{
    private static readonly IReadOnlyDictionary<string, Type> ReviewedResourceRoots =
        new Dictionary<string, Type>(StringComparer.Ordinal)
        {
            [nameof(CapabilityDiscoveryResources.GetRuntimeProfile)] = typeof(RuntimeCompatibilityResourceRecord),
            [nameof(CapabilityDiscoveryResources.GetCapabilityPolicy)] = typeof(CapabilityPolicyResourceIndex),
            [nameof(CapabilityDiscoveryResources.GetCapabilityPolicyPage)] = typeof(CapabilityPolicyResourcePage),
            [nameof(CapabilityDiscoveryResources.GetCapabilityCatalogIndex)] = typeof(CatalogResourceIndexRecord),
            [nameof(CapabilityDiscoveryResources.GetCapabilityDomain)] = typeof(CatalogResourcePageIndexRecord),
            [nameof(CapabilityDiscoveryResources.GetCapabilityDomainPage)] = typeof(ServerCapabilityCatalogShardResource),
            [nameof(CapabilityDiscoveryResources.GetCapabilityDetail)] = typeof(ServerCapabilityRecord),
            [nameof(CapabilityDiscoveryResources.GetToolCatalogIndex)] = typeof(CatalogResourceIndexRecord),
            [nameof(CapabilityDiscoveryResources.GetToolDomain)] = typeof(CatalogResourcePageIndexRecord),
            [nameof(CapabilityDiscoveryResources.GetToolDomainPage)] = typeof(ServerToolResourceShardResource),
            [nameof(CapabilityDiscoveryResources.GetToolDetail)] = typeof(ServerToolResourceRecord),
            [nameof(CapabilityDiscoveryResources.GetToolOutputContract)] = typeof(ToolOutputContractResourceIndex),
            [nameof(CapabilityDiscoveryResources.GetToolOutputContractPage)] = typeof(ToolOutputContractResourcePage),
            [nameof(CapabilityDiscoveryResources.GetToolSectionContracts)] = typeof(CatalogResourcePageIndexRecord),
            [nameof(CapabilityDiscoveryResources.GetToolSectionContractPage)] = typeof(ServerToolSectionContractPageResource),
            [nameof(CapabilityDiscoveryResources.GetWorkflowCatalog)] = typeof(CatalogResourceIndexRecord),
            [nameof(CapabilityDiscoveryResources.GetWorkflow)] = typeof(CapabilityWorkflowCatalogShardResource),
        };

    // Every reachable collection must be deliberately placed in exactly one
    // completeness class. An added collection fails closed until reviewed.
    private static readonly IReadOnlyDictionary<string, string[]> ReviewedCollections =
        new Dictionary<string, string[]>(StringComparer.Ordinal)
        {
            ["complete_index"] =
            [
                "WpaMcp.Output.CapabilityPolicyResourceIndex.Pages",
                "WpaMcp.Output.CatalogResourceIndexRecord.Shards",
                "WpaMcp.Output.CatalogResourcePageIndexRecord.Pages",
            ],
            ["byte_budgeted_page_complete_via_index"] =
            [
                "WpaMcp.Output.CapabilityPolicyResourcePage.DisabledCapabilityIds",
                "WpaMcp.Output.ServerCapabilityCatalogShardResource.Capabilities",
                "WpaMcp.Output.ServerToolResourceShardResource.Tools",
                "WpaMcp.Output.ServerToolSectionContractPageResource.SectionContracts",
            ],
            ["complete_resource"] =
            [
                "WpaMcp.Output.CapabilityGoalRecord.WorkflowIds",
                "WpaMcp.Output.CapabilityWorkflowCatalogShardResource.Goals",
                "WpaMcp.Output.CapabilityWorkflowRecord.CallableToolNames",
                "WpaMcp.Output.CapabilityWorkflowRecord.CapabilityIds",
                "WpaMcp.Output.CapabilityWorkflowRecord.DisabledByPolicyCapabilityIds",
                "WpaMcp.Output.CapabilityWorkflowRecord.DisabledByPolicyToolNames",
                "WpaMcp.Output.CapabilityWorkflowRecord.GoalIds",
                "WpaMcp.Output.CapabilityWorkflowRecord.ToolNames",
                "WpaMcp.Output.ListedCapabilityRecord.CallableToolNames",
                "WpaMcp.Output.ListedCapabilityRecord.ConclusionBoundaryCodes",
                "WpaMcp.Output.ListedCapabilityRecord.DisabledByPolicyToolNames",
                "WpaMcp.Output.ListedCapabilityRecord.GoalIds",
                "WpaMcp.Output.ListedCapabilityRecord.RequiredEventStacks",
                "WpaMcp.Output.ListedCapabilityRecord.RequiredEvents",
                "WpaMcp.Output.ListedCapabilityRecord.SupportedScopes",
                "WpaMcp.Output.ListedCapabilityRecord.ToolNames",
                "WpaMcp.Output.ListedCapabilityRecord.WorkflowIds",
                "WpaMcp.Output.ListedToolResourceRecord.CapabilityIds",
                "WpaMcp.Output.ListedToolResourceRecord.RequiredCapabilities",
                "WpaMcp.Output.ListedToolResourceRecord.SelectableScopes",
                "WpaMcp.Output.PlannerAdmissionRecord.EvidenceReferences",
                "WpaMcp.Output.PlannerAdmissionRecord.MissingEvidence",
                "WpaMcp.Output.RuntimeCompatibilityResourceRecord.ExternalKnownBlockers",
                "WpaMcp.Output.RuntimeCompatibilityResourceRecord.ReleaseBlockers",
                "WpaMcp.Output.RuntimeCompatibilityResourceRecord.RuntimeBlockers",
                "WpaMcp.Output.RuntimeCompatibilityResourceRecord.Warnings",
                "WpaMcp.Output.ServerCapabilityRecord.CallableToolNames",
                "WpaMcp.Output.ServerCapabilityRecord.ConclusionBoundaryCodes",
                "WpaMcp.Output.ServerCapabilityRecord.DisabledByPolicyToolNames",
                "WpaMcp.Output.ServerCapabilityRecord.EvidenceReferences",
                "WpaMcp.Output.ServerCapabilityRecord.GoalIds",
                "WpaMcp.Output.ServerCapabilityRecord.OptionalEvidence",
                "WpaMcp.Output.ServerCapabilityRecord.QuestionsAnswered",
                "WpaMcp.Output.ServerCapabilityRecord.QuestionsNotAnswered",
                "WpaMcp.Output.ServerCapabilityRecord.RequiredEventStacks",
                "WpaMcp.Output.ServerCapabilityRecord.RequiredEvents",
                "WpaMcp.Output.ServerCapabilityRecord.SupportedScopes",
                "WpaMcp.Output.ServerCapabilityRecord.ToolNames",
                "WpaMcp.Output.ServerCapabilityRecord.WorkflowIds",
                "WpaMcp.Output.ServerToolResourceRecord.AllowedMeasurementBases",
                "WpaMcp.Output.ServerToolResourceRecord.CapabilityIds",
                "WpaMcp.Output.ServerToolResourceRecord.DoesNotProve",
                "WpaMcp.Output.ServerToolResourceRecord.PageableSections",
                "WpaMcp.Output.ServerToolResourceRecord.RequiredCapabilities",
                "WpaMcp.Output.ServerToolResourceRecord.SelectableScopes",
                "WpaMcp.Output.ServerToolResourceRecord.SideEffects",
                "WpaMcp.Output.ServerToolResourceRecord.TieBreakers",
                "WpaMcp.Output.ServerToolSectionContractRecord.EvidenceReferenceIds",
                "WpaMcp.Output.ServerToolSectionContractRecord.TieBreakers",
            ],
        };

    [Fact]
    public void EveryPublishedResourceAndReachableCollectionHasReviewedCompleteness()
    {
        var publishedMethods = typeof(CapabilityDiscoveryResources)
            .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Where(method => method.GetCustomAttribute<McpServerResourceAttribute>() is not null)
            .Select(method => method.Name)
            .Order(StringComparer.Ordinal)
            .ToArray();
        Assert.Equal(ReviewedResourceRoots.Keys.Order(StringComparer.Ordinal), publishedMethods);

        var reachableCollections = new HashSet<string>(StringComparer.Ordinal);
        var visited = new HashSet<Type>();
        foreach (var root in ReviewedResourceRoots.Values.Distinct())
            Traverse(root, visited, reachableCollections);

        var reviewed = ReviewedCollections
            .SelectMany(group => group.Value.Select(property => (Property: property, group.Key)))
            .ToArray();
        Assert.Equal(reviewed.Length,
            reviewed.Select(item => item.Property).Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(
            reachableCollections.Order(StringComparer.Ordinal),
            reviewed.Select(item => item.Property).Order(StringComparer.Ordinal));
    }

    private static void Traverse(Type type, ISet<Type> visited, ISet<string> collections)
    {
        type = Nullable.GetUnderlyingType(type) ?? type;
        if (type.Namespace != "WpaMcp.Output" || !visited.Add(type))
            return;
        foreach (var property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
                     .Where(property => property.GetMethod is not null && !property.GetMethod.IsStatic))
        {
            var propertyType = Nullable.GetUnderlyingType(property.PropertyType)
                ?? property.PropertyType;
            if (propertyType != typeof(string) && typeof(IEnumerable).IsAssignableFrom(propertyType))
            {
                collections.Add($"{type.FullName}.{property.Name}");
                var element = CollectionElementType(propertyType);
                if (element is not null)
                    Traverse(element, visited, collections);
            }
            else
            {
                Traverse(propertyType, visited, collections);
            }
        }
    }

    private static Type? CollectionElementType(Type type)
    {
        if (type.IsArray)
            return type.GetElementType();
        return type.GetInterfaces().Append(type)
            .FirstOrDefault(candidate => candidate.IsGenericType &&
                candidate.GetGenericTypeDefinition() == typeof(IEnumerable<>))
            ?.GetGenericArguments()[0];
    }
}
