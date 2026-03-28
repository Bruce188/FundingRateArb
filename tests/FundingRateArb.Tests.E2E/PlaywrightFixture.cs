using Microsoft.Playwright;

namespace FundingRateArb.Tests.E2E;

/// <summary>
/// Shared fixture that manages a Chromium browser instance for all E2E tests.
/// Headless by default; set HEADED=1 environment variable for visible browser.
/// </summary>
public class PlaywrightFixture : IAsyncLifetime
{
    public IPlaywright Playwright { get; private set; } = null!;
    public IBrowser Browser { get; private set; } = null!;

    public string BaseUrl { get; } = Environment.GetEnvironmentVariable("E2E_BASE_URL") ?? "http://localhost:5273";

    public async Task InitializeAsync()
    {
        // Install Chromium if not already present
        Microsoft.Playwright.Program.Main(new[] { "install", "chromium" });

        Playwright = await Microsoft.Playwright.Playwright.CreateAsync();
        var headless = Environment.GetEnvironmentVariable("HEADED") != "1";
        Browser = await Playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions
        {
            Headless = headless,
        });
    }

    public async Task DisposeAsync()
    {
        await Browser.DisposeAsync();
        Playwright.Dispose();
    }
}
