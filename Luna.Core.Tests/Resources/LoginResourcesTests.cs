using System.Xml.Linq;
using Luna.Core.ViewModels;

namespace Luna.Core.Tests.Resources;

/// <summary>
/// Guards the Vietnamese resource file of Luna.App: every login state and failure has its text, and the file has no
/// duplicate keys, empty values or old brand word. Luna.App cannot be referenced from here, so the file is parsed as XML.
/// </summary>
public class LoginResourcesTests
{
    // Built from pieces so this file never contains the forbidden word literally (same trick as scripts/check-language.ps1).
    private static readonly string Brand = "za" + "lo";

    private static readonly Lazy<List<(string Name, string Value)>> Entries = new(LoadEntries);

    [Fact]
    public void Every_login_state_has_a_text()
    {
        string[] missing = Enum.GetNames<LoginState>()
            .Select(name => "Login_State_" + name)
            .Where(key => Entries.Value.All(e => e.Name != key))
            .ToArray();

        Assert.True(missing.Length == 0, "Missing keys: " + string.Join(", ", missing));
    }

    [Fact]
    public void Every_login_failure_has_a_text()
    {
        string[] missing = Enum.GetNames<LoginFailure>()
            .Select(name => "Login_Failure_" + name)
            .Where(key => Entries.Value.All(e => e.Name != key))
            .ToArray();

        Assert.True(missing.Length == 0, "Missing keys: " + string.Join(", ", missing));
    }

    [Fact]
    public void The_server_failure_text_has_a_slot_for_the_numeric_code()
    {
        var entry = Entries.Value.Single(e => e.Name == "Login_Failure_Server");

        Assert.Contains("{0}", entry.Value);
    }

    [Fact]
    public void No_key_is_duplicated()
    {
        string[] duplicates = Entries.Value
            .GroupBy(e => e.Name, StringComparer.Ordinal)
            .Where(g => g.Count() > 1)
            .Select(g => g.Key)
            .ToArray();

        Assert.True(duplicates.Length == 0, "Duplicate keys: " + string.Join(", ", duplicates));
    }

    [Fact]
    public void No_value_is_empty()
    {
        string[] empty = Entries.Value
            .Where(e => string.IsNullOrWhiteSpace(e.Value))
            .Select(e => e.Name)
            .ToArray();

        Assert.True(empty.Length == 0, "Empty values: " + string.Join(", ", empty));
    }

    [Fact]
    public void No_key_or_value_contains_the_old_brand_word()
    {
        string[] offending = Entries.Value
            .Where(e => e.Name.Contains(Brand, StringComparison.OrdinalIgnoreCase)
                || e.Value.Contains(Brand, StringComparison.OrdinalIgnoreCase))
            .Select(e => e.Name)
            .ToArray();

        Assert.True(offending.Length == 0, "Brand word in: " + string.Join(", ", offending));
    }

    private static List<(string Name, string Value)> LoadEntries()
    {
        string path = Path.Combine(FindRepositoryRoot(), "Luna.App", "Strings", "vi-VN", "Resources.resw");
        Assert.True(File.Exists(path), "Resource file not found: Luna.App/Strings/vi-VN/Resources.resw");

        XDocument doc = XDocument.Load(path);
        return doc.Root!
            .Elements("data")
            .Select(d => (
                Name: (string?)d.Attribute("name") ?? string.Empty,
                Value: (string?)d.Element("value") ?? string.Empty))
            .ToList();
    }

    private static string FindRepositoryRoot()
    {
        DirectoryInfo? dir = new(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Luna.slnx")))
        {
            dir = dir.Parent;
        }

        Assert.True(dir is not null, "Luna.slnx not found above the test output folder");
        return dir!.FullName;
    }
}
