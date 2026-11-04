using System.Reflection;
using System.Runtime.CompilerServices;
using Luna.Core.Sessions;
using Luna.Core.Throttling;
using Luna.Protocol;
using Xunit.Abstractions;

namespace Luna.Core.Tests.Hygiene;

/// <summary>
/// Guards a whole class of bug: a public record that exposes non-trivial data (strings, JSON, collections, other
/// objects) but keeps the compiler-generated ToString, which prints every property value. Such a record leaks
/// secrets into logs and exception messages the moment someone interpolates it.
/// </summary>
public class RecordToStringTests
{
    private static readonly HashSet<Type> SafeTypes =
    [
        typeof(bool),
        typeof(byte), typeof(sbyte), typeof(short), typeof(ushort),
        typeof(int), typeof(uint), typeof(long), typeof(ulong),
        typeof(float), typeof(double), typeof(decimal),
        typeof(DateTime), typeof(DateTimeOffset), typeof(TimeSpan), typeof(Guid),
    ];

    /// <summary>Type name -> one-line reason why the compiler-generated ToString is acceptable. Keep it empty if possible.</summary>
    private static readonly Dictionary<string, string> AllowList = new();

    private readonly ITestOutputHelper _output;

    public RecordToStringTests(ITestOutputHelper output)
    {
        _output = output;
    }

    /// <summary>A record-like type that has unsafe members and a compiler-generated ToString.</summary>
    private sealed record Violation(string TypeName, IReadOnlyList<string> UnsafeMembers);

    private static bool IsSafe(Type type)
    {
        Type effective = Nullable.GetUnderlyingType(type) ?? type;
        return effective.IsEnum || SafeTypes.Contains(effective);
    }

    private static bool IsRecordLike(Type type)
    {
        if (type.IsClass)
        {
            // Records are classes with a compiler-generated "<Clone>$" method.
            return type.GetMethod("<Clone>$", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance) is not null;
        }

        // Record structs have no Clone method. A struct only ever gets a generated ToString from the "record struct" syntax.
        return type.IsValueType && !type.IsEnum && !type.IsPrimitive;
    }

    /// <summary>Returns the violation for one type, or null when the type is fine (or is not a record).</summary>
    private static Violation? Analyze(Type type)
    {
        if (!IsRecordLike(type))
        {
            return null;
        }

        MethodInfo? toString = type.GetMethod("ToString", BindingFlags.Public | BindingFlags.Instance, binder: null, Type.EmptyTypes, modifiers: null);
        if (toString is null || !toString.IsDefined(typeof(CompilerGeneratedAttribute), inherit: false))
        {
            return null;
        }

        // No DeclaredOnly: properties inherited from a base record are printed too.
        var unsafeMembers = new List<string>();
        foreach (PropertyInfo property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (property.GetIndexParameters().Length == 0 && property.GetMethod is not null && !IsSafe(property.PropertyType))
            {
                unsafeMembers.Add(property.Name);
            }
        }

        foreach (FieldInfo field in type.GetFields(BindingFlags.Public | BindingFlags.Instance))
        {
            if (!IsSafe(field.FieldType))
            {
                unsafeMembers.Add(field.Name);
            }
        }

        return unsafeMembers.Count == 0 ? null : new Violation(type.Name, unsafeMembers);
    }

    private static IEnumerable<Type> ScannedTypes() =>
        new[] { typeof(ClientContext).Assembly, typeof(SessionData).Assembly }
            .SelectMany(assembly => assembly.GetTypes())
            .Where(type => !type.IsDefined(typeof(CompilerGeneratedAttribute), inherit: false)
                && !type.Name.Contains('<', StringComparison.Ordinal));

    // ---- Self-checks: prove the guard detects what it claims -------------------------------------------------

    private sealed record Flagged(string Secret);

    private sealed record Overridden(string Secret)
    {
        public override string ToString() => nameof(Overridden);
    }

    private readonly record struct FlaggedStruct(string Secret);

    private readonly record struct OverriddenStruct(string Secret)
    {
        public override string ToString() => nameof(OverriddenStruct);
    }

    private abstract record BaseWithSecret(string Secret);

    private sealed record DerivedPlain(int Number) : BaseWithSecret("x");

    private sealed record SafeOnly(int Number, bool Flag, DayOfWeek Day, DateTime? When, TimeSpan Span);

    [Fact]
    public void Guard_flags_a_record_with_a_string_and_a_generated_ToString()
    {
        Violation? violation = Analyze(typeof(Flagged));

        Assert.NotNull(violation);
        Assert.Equal(["Secret"], violation.UnsafeMembers);
    }

    [Fact]
    public void Guard_accepts_a_record_that_overrides_ToString()
    {
        Assert.Null(Analyze(typeof(Overridden)));
    }

    [Fact]
    public void Guard_flags_a_record_struct_with_a_string_and_a_generated_ToString()
    {
        Violation? violation = Analyze(typeof(FlaggedStruct));

        Assert.NotNull(violation);
        Assert.Equal(["Secret"], violation.UnsafeMembers);
    }

    [Fact]
    public void Guard_accepts_a_record_struct_that_overrides_ToString()
    {
        Assert.Null(Analyze(typeof(OverriddenStruct)));
    }

    [Fact]
    public void Guard_flags_a_derived_record_that_inherits_an_unsafe_property()
    {
        Violation? violation = Analyze(typeof(DerivedPlain));

        Assert.NotNull(violation);
        Assert.Equal(["Secret"], violation.UnsafeMembers);
    }

    [Fact]
    public void Guard_accepts_a_record_with_only_safe_properties()
    {
        Assert.Null(Analyze(typeof(SafeOnly)));
    }

    [Fact]
    public void Guard_ignores_a_plain_class_and_a_plain_struct()
    {
        Assert.Null(Analyze(typeof(ClientContext)));
        Assert.Null(Analyze(typeof(KeyValuePair<string, string>)));
    }

    // ---- The scan over the shipped assemblies ----------------------------------------------------------------

    /// <summary>
    /// Every public record or record struct that exposes unsafe data must declare its own ToString.
    /// Lists type and property names only, never values.
    /// </summary>
    [Fact]
    public void Public_records_with_unsafe_properties_declare_their_own_ToString()
    {
        List<Type> scanned = ScannedTypes().ToList();
        Assert.Contains(typeof(SessionData), scanned);

        List<string> offenders = scanned
            .Select(Analyze)
            .OfType<Violation>()
            .Where(v => !AllowList.ContainsKey(v.TypeName))
            .OrderBy(v => v.TypeName, StringComparer.Ordinal)
            .Select(v => $"{v.TypeName} ({string.Join(", ", v.UnsafeMembers)})")
            .ToList();

        foreach (string offender in offenders)
        {
            _output.WriteLine($"VIOLATOR {offender}");
        }

        Assert.True(
            offenders.Count == 0,
            "These records keep the compiler-generated ToString; declare an override that hides the data: "
            + string.Join("; ", offenders));
    }

    [Fact]
    public void Private_throttle_State_ToString_hides_endpoint_names_and_times()
    {
        const string fakeEndpoint = "fake-endpoint-marker";
        const string fakeTime = "2099-01-01T00:00:00.0000000Z";
        Type stateType = typeof(ApiThrottleGuard).GetNestedType("State", BindingFlags.NonPublic)!;
        object state = Activator.CreateInstance(
            stateType,
            fakeTime,
            new Dictionary<string, string> { [fakeEndpoint] = fakeTime })!;
        Assert.Contains(fakeEndpoint, ((IReadOnlyDictionary<string, string>)stateType.GetProperty("Calls")!.GetValue(state)!).Keys);

        string text = state.ToString()!;

        Assert.Equal("State", text);
        Assert.DoesNotContain(fakeEndpoint, text, StringComparison.Ordinal);
        Assert.DoesNotContain(fakeTime, text, StringComparison.Ordinal);
    }

    [Fact]
    public void AllowList_has_no_stale_entries()
    {
        HashSet<string> violating = ScannedTypes()
            .Select(Analyze)
            .OfType<Violation>()
            .Select(v => v.TypeName)
            .ToHashSet(StringComparer.Ordinal);

        Assert.All(AllowList.Keys, name => Assert.Contains(name, violating));
    }
}
