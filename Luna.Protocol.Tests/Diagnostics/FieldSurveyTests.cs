using System.Text.Json;
using Xunit;
using Luna.Protocol.Diagnostics;

namespace Luna.Protocol.Tests.Diagnostics;

public sealed class FieldSurveyTests
{
    private static JsonElement Obj(string json) => JsonDocument.Parse(json).RootElement;

    [Fact]
    public void Reports_every_type_seen_for_a_field_with_counts()
    {
        var survey = new FieldSurvey();
        for (int i = 0; i < 3; i++)
        {
            survey.Add(Obj("{\"oaInfo\":null}"));
        }

        survey.Add(Obj("{\"oaInfo\":{}}"));
        survey.Add(Obj("{\"oaInfo\":\"x\"}"));

        Assert.Equal(["oaInfo: null x3, object x1, string x1"], survey.Describe());
    }

    [Fact]
    public void Fields_present_in_only_some_elements_report_absence()
    {
        var survey = new FieldSurvey();
        survey.Add(Obj("{\"a\":1,\"b\":true}"));
        survey.Add(Obj("{\"a\":2}"));
        survey.Add(Obj("{\"a\":3}"));

        Assert.Equal(["a: number x3", "b: boolean x1, absent x2"], survey.Describe());
    }

    [Fact]
    public void Lines_are_sorted_by_name()
    {
        var survey = new FieldSurvey();
        survey.Add(Obj("{\"zeta\":1,\"alpha\":\"s\",\"mid\":[]}"));

        Assert.Equal(["alpha: string x1", "mid: array x1", "zeta: number x1"], survey.Describe());
    }

    [Fact]
    public void Nested_objects_are_described_two_levels_deep_and_no_deeper()
    {
        var survey = new FieldSurvey();
        survey.Add(Obj("{\"bizPkg\":{\"label\":null,\"pkgId\":0,\"deep\":{\"x\":1}}}"));

        Assert.Equal(
            [
                "bizPkg: object x1",
                "bizPkg.deep: object x1",
                "bizPkg.label: null x1",
                "bizPkg.pkgId: number x1",
            ],
            survey.Describe());
    }

    [Fact]
    public void Id_like_keys_are_masked_and_counted_once_per_element()
    {
        var survey = new FieldSurvey();
        survey.Add(Obj("{\"1234567890123\":1,\"9876543210987\":2,\"ok\":3}"));

        string[] lines = [.. survey.Describe()];

        Assert.Equal(["<key>: number x1", "ok: number x1"], lines);
    }

    [Fact]
    public void Values_never_appear_in_output()
    {
        const string Secret = "SECRET-cookie-zpw_sek-9f8e7d6c5b4a";
        var survey = new FieldSurvey();
        survey.Add(Obj($"{{\"phoneNumber\":\"0901234567\",\"displayName\":\"{Secret}\",\"nested\":{{\"token\":\"{Secret}\"}},\"n\":424242}}"));

        string all = string.Join("\n", survey.Describe());

        Assert.DoesNotContain(Secret, all, StringComparison.Ordinal);
        Assert.DoesNotContain("0901234567", all, StringComparison.Ordinal);
        Assert.DoesNotContain("424242", all, StringComparison.Ordinal);
    }

    [Fact]
    public void Non_object_elements_are_ignored()
    {
        var survey = new FieldSurvey();
        survey.Add(Obj("42"));
        survey.Add(Obj("[1]"));
        survey.Add(Obj("null"));

        Assert.Empty(survey.Describe());
    }

    [Fact]
    public void Empty_survey_has_no_lines()
    {
        Assert.Empty(new FieldSurvey().Describe());
    }
}
