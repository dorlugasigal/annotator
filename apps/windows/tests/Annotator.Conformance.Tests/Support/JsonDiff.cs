using System.Globalization;
using System.Numerics;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Annotator.Conformance.Tests;

/// <summary>How two numbers in a corpus record must agree.</summary>
internal readonly record struct NumberRule(string Name, Func<double, double, bool> Agree)
{
    /// <summary>The same double, bit for bit (signed zeros differ). Integer literals compare exactly at any size under
    /// this rule, and beyond 2^53 under every rule.</summary>
    public static NumberRule Exact { get; } = new("exact", (a, b) => BitConverter.DoubleToInt64Bits(a) == BitConverter.DoubleToInt64Bits(b));

    public static NumberRule Within(double tolerance) => new($"within {tolerance:R}", (a, b) => a == b || Math.Abs(a - b) <= tolerance);

    /// <summary>At most <paramref name="units"/> units in the last place apart (libm samples).</summary>
    public static NumberRule Ulps(int units) => new($"within {units} ulp", (a, b) =>
    {
        if (a == b || (double.IsNaN(a) && double.IsNaN(b)))
        {
            return true;
        }
        if (Math.Sign(a) != Math.Sign(b) && a != 0 && b != 0)
        {
            return false;
        }
        var distance = Math.Abs(BitConverter.DoubleToInt64Bits(Math.Abs(a)) - BitConverter.DoubleToInt64Bits(Math.Abs(b)));
        return distance <= units;
    });
}

/// <summary>Structural comparison of JSON values, with a rule for numbers; reports every difference with its path.</summary>
internal static class JsonDiff
{
    private static readonly BigInteger SafeInteger = BigInteger.Pow(2, 53);

    public static List<string> Differences(JsonNode? expected, JsonNode? actual, NumberRule rule)
    {
        var differences = new List<string>();
        Compare(expected, actual, rule, "$", differences);
        return differences;
    }

    public static void Equal(JsonNode? expected, JsonNode? actual, NumberRule rule, string? context = null)
    {
        var differences = Differences(expected, actual, rule);
        if (differences.Count != 0)
        {
            Assert.Fail(Describe(differences, context));
        }
    }

    public static string Describe(List<string> differences, string? context) =>
        $"{(context is null ? "" : context + ": ")}{differences.Count} difference(s)\n" + string.Join("\n", differences.Take(25));

    private static void Compare(JsonNode? expected, JsonNode? actual, NumberRule rule, string path, List<string> differences)
    {
        if (expected is null || actual is null)
        {
            if (expected is not null || actual is not null)
            {
                differences.Add($"{path}: expected {Show(expected)}, found {Show(actual)}");
            }
            return;
        }
        var expectedKind = expected.GetValueKind();
        var actualKind = actual.GetValueKind();
        if (expectedKind != actualKind && !(IsBool(expectedKind) && IsBool(actualKind)))
        {
            differences.Add($"{path}: expected {Show(expected)}, found {Show(actual)}");
            return;
        }
        switch (expectedKind)
        {
            case JsonValueKind.Object:
            {
                var left = expected.AsObject();
                var right = actual.AsObject();
                foreach (var (key, value) in left)
                {
                    if (!right.ContainsKey(key))
                    {
                        differences.Add($"{path}.{key}: missing (expected {Show(value)})");
                        continue;
                    }
                    Compare(value, right[key], rule, $"{path}.{key}", differences);
                }
                foreach (var (key, value) in right)
                {
                    if (!left.ContainsKey(key))
                    {
                        differences.Add($"{path}.{key}: unexpected {Show(value)}");
                    }
                }
                break;
            }
            case JsonValueKind.Array:
            {
                var left = expected.AsArray();
                var right = actual.AsArray();
                if (left.Count != right.Count)
                {
                    differences.Add($"{path}: expected {left.Count} items, found {right.Count}");
                }
                for (var index = 0; index < Math.Min(left.Count, right.Count); index++)
                {
                    Compare(left[index], right[index], rule, $"{path}[{index}]", differences);
                }
                break;
            }
            case JsonValueKind.Number:
                if (!NumbersAgree(expected.ToJsonString(), actual.ToJsonString(), rule))
                {
                    differences.Add($"{path}: expected {expected.ToJsonString()}, found {actual.ToJsonString()} ({rule.Name})");
                }
                break;
            case JsonValueKind.String:
                if (!string.Equals(expected.GetValue<string>(), actual.GetValue<string>(), StringComparison.Ordinal))
                {
                    differences.Add($"{path}: expected {Show(expected)}, found {Show(actual)}");
                }
                break;
            case JsonValueKind.True or JsonValueKind.False:
                if (expectedKind != actualKind)
                {
                    differences.Add($"{path}: expected {Show(expected)}, found {Show(actual)}");
                }
                break;
        }
    }

    private static bool IsBool(JsonValueKind kind) => kind is JsonValueKind.True or JsonValueKind.False;

    private static bool NumbersAgree(string expected, string actual, NumberRule rule)
    {
        if (string.Equals(expected, actual, StringComparison.Ordinal))
        {
            return true;
        }
        if (IsInteger(expected) && IsInteger(actual))
        {
            // Seeds and other integers beyond 2^53 would collapse to the same double, so they compare exactly.
            var left = BigInteger.Parse(expected, CultureInfo.InvariantCulture);
            var right = BigInteger.Parse(actual, CultureInfo.InvariantCulture);
            if (left == right || rule.Name == NumberRule.Exact.Name || BigInteger.Abs(left) > SafeInteger || BigInteger.Abs(right) > SafeInteger)
            {
                return left == right;
            }
        }
        return rule.Agree(double.Parse(expected, CultureInfo.InvariantCulture), double.Parse(actual, CultureInfo.InvariantCulture));
    }

    private static bool IsInteger(string text) => text.AsSpan().IndexOfAny(".eE") < 0;

    private static string Show(JsonNode? node)
    {
        var text = node?.ToJsonString() ?? "null";
        return text.Length > 160 ? text[..160] + "…" : text;
    }
}
