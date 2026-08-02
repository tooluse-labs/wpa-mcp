namespace WpaMcp.Core;

[AttributeUsage(AttributeTargets.Property, AllowMultiple = false, Inherited = true)]
internal sealed class ToolOpaqueLocatorAttribute(string kind, string pattern) : Attribute
{
    internal string Kind { get; } = string.IsNullOrWhiteSpace(kind)
        ? throw new ArgumentException("Locator kind is required.", nameof(kind))
        : kind;
    internal string Pattern { get; } = string.IsNullOrWhiteSpace(pattern)
        ? throw new ArgumentException("Locator pattern is required.", nameof(pattern))
        : pattern;
}

[AttributeUsage(AttributeTargets.Property, AllowMultiple = false, Inherited = true)]
internal sealed class ToolDictionaryRowsAttribute(string keyPropertyName, string valuePropertyName) : Attribute
{
    internal string KeyPropertyName { get; } = RequireWireName(keyPropertyName, nameof(keyPropertyName));

    internal string ValuePropertyName { get; } = RequireWireName(valuePropertyName, nameof(valuePropertyName));

    private static string RequireWireName(string value, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new ArgumentException("A non-empty wire property name is required.", parameterName);
        return value;
    }
}

[AttributeUsage(AttributeTargets.Property, AllowMultiple = false, Inherited = true)]
internal sealed class ToolSafeIntegerCompatibilityAttribute(
    string authoritativeStringProperty,
    string statusProperty) : Attribute
{
    internal string AuthoritativeStringProperty { get; } = authoritativeStringProperty;

    internal string StatusProperty { get; } = statusProperty;
}

[AttributeUsage(AttributeTargets.Property, AllowMultiple = false, Inherited = true)]
internal sealed class ToolMetricSemanticsAttribute(
    string unit,
    string aggregation,
    string denominator,
    double minimum,
    double maximum) : Attribute
{
    internal string Unit { get; } = unit;
    internal string Aggregation { get; } = aggregation;
    internal string Denominator { get; } = denominator;
    internal double Minimum { get; } = minimum;
    internal double Maximum { get; } = maximum;
}

/// <summary>
/// Declares reviewed wire semantics for a numeric value. This is intentionally
/// property-specific: naming conventions must never manufacture a reviewed unit or
/// denominator. Apply it to a numeric collection property to describe each element.
/// </summary>
[AttributeUsage(AttributeTargets.Property, AllowMultiple = false, Inherited = true)]
internal sealed class ToolNumericSemanticsAttribute(
    string role,
    string unit,
    string precision,
    string aggregation,
    string? denominator = null,
    string? unitProperty = null,
    double minimum = double.NaN,
    double maximum = double.NaN) : Attribute
{
    internal string Role { get; } = Require(role, nameof(role));
    internal string Unit { get; } = Require(unit, nameof(unit));
    internal string Precision { get; } = Require(precision, nameof(precision));
    internal string Aggregation { get; } = Require(aggregation, nameof(aggregation));
    internal string? Denominator { get; } = Optional(denominator, nameof(denominator));
    internal string? UnitProperty { get; } = Optional(unitProperty, nameof(unitProperty));
    internal double Minimum { get; } = minimum;
    internal double Maximum { get; } = maximum;

    private static string Require(string value, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new ArgumentException("A non-empty numeric semantic value is required.", parameterName);
        return value;
    }

    private static string? Optional(string? value, string parameterName)
    {
        if (value is not null && string.IsNullOrWhiteSpace(value))
            throw new ArgumentException("An optional numeric semantic value cannot be empty.", parameterName);
        return value;
    }
}
