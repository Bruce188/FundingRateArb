using Microsoft.Playwright;

namespace FundingRateArb.Tests.E2E;

/// <summary>
/// Shared fixture that manages a Chromium browser instance for all E2E tests.
/// Headless by default; set HEADED=1 environment variable for visible browser.
/// Browser install runs during initialization; CI pipelines should run
/// "pwsh bin/Debug/net8.0/playwright.ps1 install chromium" as a pre-step for caching.
/// </summary>
public class PlaywrightFixture : IAsyncLifetime
{
    public IPlaywright Playwright { get; private set; } = null!;
    public IBrowser Browser { get; private set; } = null!;
    public HttpClient SharedHttpClient { get; } = new() { Timeout = TimeSpan.FromSeconds(5) };

    public string BaseUrl { get; } = Environment.GetEnvironmentVariable("E2E_BASE_URL") ?? "http://localhost:5273";

    public async Task InitializeAsync()
    {
        // N5: Warn when E2E_BASE_URL is HTTP over non-localhost (credentials transit unencrypted)
        if (BaseUrl.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            && !BaseUrl.Contains("localhost") && !BaseUrl.Contains("127.0.0.1"))
        {
            Console.Error.WriteLine(
                $"WARNING: E2E_BASE_URL ({BaseUrl}) uses HTTP over a non-localhost address. " +
                "Credentials will be transmitted unencrypted. Use HTTPS for non-local targets.");
        }

        // Install Chromium if not already present (idempotent)
        var exitCode = Microsoft.Playwright.Program.Main(["install", "chromium"]);
        if (exitCode != 0)
        {
            throw new InvalidOperationException($"Playwright browser install failed with exit code {exitCode}");
        }

        Playwright = await Microsoft.Playwright.Playwright.CreateAsync();
        var headless = Environment.GetEnvironmentVariable("HEADED") != "1";
        Browser = await Playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions
        {
            Headless = headless,
        });
    }

    public async Task DisposeAsync()
    {
        SharedHttpClient.Dispose();
        await Browser.DisposeAsync();
        Playwright.Dispose();
    }
}

[CollectionDefinition("Playwright")]
public class PlaywrightCollection : ICollectionFixture<PlaywrightFixture>
{
    // This class has no code; it exists solely to apply [CollectionDefinition]
    // and the ICollectionFixture<> interface so xUnit shares PlaywrightFixture
    // across all test classes in the "Playwright" collection.
}
