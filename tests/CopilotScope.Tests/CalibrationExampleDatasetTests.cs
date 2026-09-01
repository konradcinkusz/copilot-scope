using System.Text.Json;
using CopilotScope.Collector.Calibration;
using CopilotScope.JudgeAgent.Calibration;
using Xunit;

namespace CopilotScope.Tests;

// calibration/labels.example.json is shipped as the format a real dataset has to take, so it
// has to keep parsing into the shape the engine consumes. Without this test a renamed rubric or
// a re-cut band scale would leave a checked-in example that silently no longer works — and the
// first person to notice would be someone trying to calibrate a judge.
public class CalibrationExampleDatasetTests
{
    private static CalibrationDataset LoadExample()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "CopilotScope.sln")))
            directory = directory.Parent;

        Assert.NotNull(directory);
        var path = Path.Combine(directory!.FullName, "calibration", "labels.example.json");
        Assert.True(File.Exists(path), $"Example dataset missing at {path}");

        return JsonSerializer.Deserialize<CalibrationDataset>(
            File.ReadAllText(path), new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
    }

    [Fact]
    public void ExampleDataset_ParsesIntoTheShapeTheEngineConsumes()
    {
        var dataset = LoadExample();

        Assert.NotEmpty(dataset.Labels);
        Assert.NotEmpty(dataset.JudgeScores);
        Assert.All(dataset.Labels, label =>
        {
            Assert.True(RubricScale.IsValidLevel(label.Level), $"level {label.Level} is off the scale");
            Assert.NotNull(RubricScale.Find(label.Algorithm));
            Assert.False(string.IsNullOrWhiteSpace(label.Rater));
        });
    }

    [Fact]
    public void ExampleDataset_RefusesToCertifyAnything()
    {
        // The point of the example is the format, not a result. It is deliberately far too small
        // to license a verdict, and the engine saying so is the example working correctly — if
        // this ever came back "calibrated", the repo would be shipping a fabricated calibration.
        var report = new CalibrationEngine().Evaluate(LoadExample());

        Assert.Equal(CalibrationVerdict.InsufficientData, report.Verdict);
        Assert.All(report.Rubrics, rubric =>
            Assert.Equal(CalibrationVerdict.InsufficientData, rubric.Verdict));
        Assert.Contains("NOT-a-real-calibration", report.DatasetVersion!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ExampleDataset_ShowsBothRubricDirections()
    {
        // It carries a normal rubric and the inverted one, because getting deep-friction's
        // direction wrong is the mistake the format is trying to prevent.
        var rubrics = LoadExample().Labels.Select(l => l.Algorithm).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

        Assert.Contains(rubrics, r => RubricScale.Find(r)!.HigherIsBetter);
        Assert.Contains(rubrics, r => !RubricScale.Find(r)!.HigherIsBetter);
    }
}
