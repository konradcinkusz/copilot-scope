using CopilotScope.Collector.Quality;

namespace CopilotScope.Tests;

/// <summary>
/// The workflow-friction analyzer configured the way a test needs it.
///
/// Both switches are off in a shipped deployment, and that is the whole point of the feature's
/// design (#95): the analyzer does not run unless an operator turns it on, and it does not
/// quote prompt text unless they turn that on separately. Tests that exercise the detection
/// logic have to opt into both explicitly, which keeps the defaults honest — nothing can quietly
/// start relying on the analyzer being on.
/// </summary>
internal static class TestFriction
{
    public static WorkflowFrictionAnalyzer Analyzer() =>
        new(new WorkflowFrictionOptions { Enabled = true, IncludeFlaggedMessages = true });
}
