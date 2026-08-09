using CopilotScope.JudgeAgent.Judging;
using Xunit;

namespace CopilotScope.Tests;

public class JudgeResponseParserTests
{
    private const string ValidResponse = """
        {
          "results": [
            {
              "name": "LLM-as-a-Judge (G-Eval)",
              "algorithm": "G-Eval",
              "status": "ok",
              "score": 0.82,
              "metrics": [ { "label": "correctness", "value": "0.9" } ],
              "findings": [ "Turn 2 correctly fixes the null check." ]
            },
            {
              "name": "RAG component metrics (RAGAS)",
              "algorithm": "RAGAS",
              "status": "no-data",
              "score": null,
              "metrics": [],
              "findings": [ "Session has no retrieval context." ]
            }
          ]
        }
        """;

    [Fact]
    public void Parse_ValidJson_ReturnsInsightReports()
    {
        var results = JudgeResponseParser.Parse(ValidResponse);

        Assert.Equal(2, results.Count);
        Assert.Equal("G-Eval", results[0].Algorithm);
        Assert.Equal("ok", results[0].Status);
        Assert.Equal(0.82, results[0].Score);
        Assert.Single(results[0].Metrics);
        Assert.Equal("correctness", results[0].Metrics[0].Label);
        Assert.Equal("RAGAS", results[1].Algorithm);
        Assert.Equal("no-data", results[1].Status);
        Assert.Null(results[1].Score);
    }

    [Fact]
    public void Parse_StripsMarkdownFence()
    {
        var fenced = "```json\n" + ValidResponse + "\n```";

        var results = JudgeResponseParser.Parse(fenced);

        Assert.Equal(2, results.Count);
    }

    [Fact]
    public void Parse_MissingResultsArray_Throws()
    {
        Assert.Throws<InvalidOperationException>(() => JudgeResponseParser.Parse("{\"foo\": \"bar\"}"));
    }

    [Fact]
    public void Parse_InvalidJson_ThrowsWithClearMessage()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => JudgeResponseParser.Parse("not json at all"));
        Assert.Contains("not valid JSON", ex.Message);
    }
}
