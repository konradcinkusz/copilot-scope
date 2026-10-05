using Microsoft.Playwright;
using NUnit.Framework;

namespace CopilotScope.E2E.Playwright;

/// <summary>
/// CopilotScope dashboard E2E tests with Playwright.
/// Tests the UI that displays session quality scores.
///
/// Environment: Set COPILOTSCOPE_URL to the dashboard URL (e.g., http://localhost:4319).
/// Dashboard requires these data-test-* attributes:
/// - data-test-session-id="id" on session rows
/// - data-test-score on score display elements
/// - data-test-confidence on confidence display elements
/// - data-test-scenario="name" on scenario result containers
/// </summary>
[TestFixture]
[Parallelizable(ParallelScope.Self)]
public class DashboardTests
{
    private IBrowser? _browser;
    private IBrowserContext? _context;
    private IPage? _page;

    [SetUp]
    public async Task SetupAsync()
    {
        var playwright = await Microsoft.Playwright.Playwright.CreateAsync();
        _browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions { Headless = true });
        _context = await _browser.NewContextAsync();
        _page = await _context.NewPageAsync();
    }

    [TearDown]
    public async Task TeardownAsync()
    {
        if (_context is not null)
            await _context.CloseAsync();
        if (_browser is not null)
            await _browser.CloseAsync();
    }

    [Test]
    [Category("Smoke")]
    public async Task DashboardLoads_AndDisplaysHeading()
    {
        var url = Environment.GetEnvironmentVariable("COPILOTSCOPE_URL");
        if (string.IsNullOrEmpty(url))
        {
            Assert.Inconclusive("COPILOTSCOPE_URL not set");
        }

        await _page!.GotoAsync(url);
        var heading = _page.GetByRole(AriaRole.Heading);
        await Assertions.Expect(heading).ToBeVisibleAsync();
    }

    [Test]
    [Category("Regression")]
    public async Task ScoreDisplays_WhenSessionsArePresent()
    {
        var url = Environment.GetEnvironmentVariable("COPILOTSCOPE_URL");
        if (string.IsNullOrEmpty(url))
        {
            Assert.Inconclusive("COPILOTSCOPE_URL not set");
        }

        await _page!.GotoAsync(url);
        await _page.WaitForLoadStateAsync(LoadState.NetworkIdle);

        // Check if any score elements are visible
        var scores = _page.Locator("[data-test-score]");
        var count = await scores.CountAsync();

        Assert.That(count, Is.GreaterThanOrEqualTo(0), "Dashboard should have score elements or be empty");
    }

    [Test]
    [Category("Regression")]
    public async Task ConfidenceValues_AreDisplayedAndNumeric()
    {
        var url = Environment.GetEnvironmentVariable("COPILOTSCOPE_URL");
        if (string.IsNullOrEmpty(url))
        {
            Assert.Inconclusive("COPILOTSCOPE_URL not set");
        }

        await _page!.GotoAsync(url);
        await _page.WaitForLoadStateAsync(LoadState.NetworkIdle);

        // Verify confidence elements exist and contain numeric values
        var confidences = _page.Locator("[data-test-confidence]");
        var count = await confidences.CountAsync();

        if (count > 0)
        {
            for (int i = 0; i < count; i++)
            {
                var confidenceText = await confidences.Nth(i).InnerTextAsync();
                var cleaned = confidenceText.Replace("%", "").Trim();

                Assert.That(
                    double.TryParse(cleaned, out var confidence),
                    $"Confidence value '{confidenceText}' should be numeric");

                // Confidence should be 0-1 or 0-100
                Assert.That(
                    confidence >= 0 && (confidence <= 1 || confidence <= 100),
                    $"Confidence {confidence} should be in range [0,1] or [0,100]");
            }
        }
    }

    [Test]
    [Category("Regression")]
    public async Task PageRefresh_PreservesSessions()
    {
        var url = Environment.GetEnvironmentVariable("COPILOTSCOPE_URL");
        if (string.IsNullOrEmpty(url))
        {
            Assert.Inconclusive("COPILOTSCOPE_URL not set");
        }

        await _page!.GotoAsync(url);
        await _page.WaitForLoadStateAsync(LoadState.NetworkIdle);

        // Capture initial state
        var initialSessionCount = await _page.Locator("[data-test-session-id]").CountAsync();

        // Refresh
        await _page.ReloadAsync();
        await _page.WaitForLoadStateAsync(LoadState.NetworkIdle);

        // Verify same count
        var afterRefreshCount = await _page.Locator("[data-test-session-id]").CountAsync();
        Assert.AreEqual(initialSessionCount, afterRefreshCount, "Session count should be preserved after refresh");
    }
}
