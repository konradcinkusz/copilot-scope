using CopilotScope.Dashboard.Services;
using Xunit;

namespace CopilotScope.Tests;

/// <summary>
/// Covers the dashboard's transcript parser — the one piece of real logic in the
/// Dashboard that had no test. It normalizes the several captured-content dialects
/// (content-as-string, content-as-parts, parts/text) into (role, text) pairs and must
/// degrade to a single raw message when the payload isn't JSON.
/// </summary>
public sealed class ChatMessageParserTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void EmptyInput_YieldsNoMessages(string? raw)
    {
        Assert.Empty(ChatMessageParser.Parse(raw, "user"));
    }

    [Fact]
    public void PlainText_BecomesOneFallbackRoleMessage()
    {
        var result = ChatMessageParser.Parse("just some prose, not JSON", "assistant");
        var m = Assert.Single(result);
        Assert.Equal("assistant", m.Role);
        Assert.Equal("just some prose, not JSON", m.Text);
    }

    [Fact]
    public void ContentAsString_KeepsRoleAndText()
    {
        var raw = """[{"role":"USER","content":"hello there"}]""";
        var m = Assert.Single(ChatMessageParser.Parse(raw, "user"));
        Assert.Equal("user", m.Role);              // role is lowercased
        Assert.Equal("hello there", m.Text);
    }

    [Fact]
    public void ContentAsParts_AreFlattened()
    {
        var raw = """
            [{"role":"assistant","content":[{"type":"text","text":"line one"},{"type":"text","text":"line two"}]}]
            """;
        var m = Assert.Single(ChatMessageParser.Parse(raw, "user"));
        Assert.Equal("assistant", m.Role);
        Assert.Contains("line one", m.Text);
        Assert.Contains("line two", m.Text);
    }

    [Fact]
    public void PartsTextDialect_IsSupported()
    {
        var raw = """[{"role":"user","parts":[{"text":"from parts"}]}]""";
        var m = Assert.Single(ChatMessageParser.Parse(raw, "user"));
        Assert.Equal("from parts", m.Text);
    }

    [Fact]
    public void NonTextPart_ShowsACompactMarkerNotRawJson()
    {
        var raw = """[{"role":"assistant","content":[{"type":"tool_use","id":"abc"}]}]""";
        var m = Assert.Single(ChatMessageParser.Parse(raw, "user"));
        Assert.Contains("[tool_use]", m.Text);
        Assert.DoesNotContain("abc", m.Text);
    }

    [Fact]
    public void MultipleMessages_ArePreservedInOrder()
    {
        var raw = """[{"role":"user","content":"q"},{"role":"assistant","content":"a"}]""";
        var result = ChatMessageParser.Parse(raw, "user");
        Assert.Equal(2, result.Count);
        Assert.Equal("user", result[0].Role);
        Assert.Equal("assistant", result[1].Role);
    }

    [Fact]
    public void MissingRole_FallsBackToProvidedRole()
    {
        var raw = """[{"content":"no role here"}]""";
        var m = Assert.Single(ChatMessageParser.Parse(raw, "system"));
        Assert.Equal("system", m.Role);
    }

    [Fact]
    public void MalformedJson_DegradesToRawText()
    {
        var raw = """[{"role":"user","content": broken]""";
        var m = Assert.Single(ChatMessageParser.Parse(raw, "user"));
        Assert.Equal(raw, m.Text); // whole raw payload preserved rather than dropped
    }
}
