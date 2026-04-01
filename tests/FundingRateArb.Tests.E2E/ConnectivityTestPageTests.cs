using Microsoft.Playwright;

namespace FundingRateArb.Tests.E2E;

[Trait("Category", "E2E")]
[Collection("Playwright")]
public class ConnectivityTestPageTests
{
    private const int MaxRetries = 3;
    private const int BadgeTimeoutMs = 120_000;
    private const int ButtonEnableTimeoutMs = 10_000;

    private readonly PlaywrightFixture _fixture;

    public ConnectivityTestPageTests(PlaywrightFixture fixture)
    {
        _fixture = fixture;
    }

    private async Task<(IPage Page, IBrowserContext Context)> CreatePageAsync()
    {
        var isLocalhost = _fixture.BaseUrl.Contains("localhost") || _fixture.BaseUrl.Contains("127.0.0.1");
        var context = await _fixture.Browser.NewContextAsync(new BrowserNewContextOptions
        {
            IgnoreHTTPSErrors = isLocalhost,
        });
        var page = await context.NewPageAsync();
        return (page, context);
    }

    private async Task LoginAsync(IPage page)
    {
        var password = Environment.GetEnvironmentVariable("E2E_ADMIN_PASSWORD");
        Skip.If(string.IsNullOrEmpty(password), "E2E_ADMIN_PASSWORD env var is required for E2E tests");

        var adminEmail = Environment.GetEnvironmentVariable("E2E_ADMIN_EMAIL") ?? "admin@fundingratearb.com";
        await page.GotoAsync($"{_fixture.BaseUrl}/Identity/Account/Login");
        await page.FillAsync("input[name='Input.Email']", adminEmail);
        await page.FillAsync("input[name='Input.Password']", password!);
        await page.ClickAsync("button[type='submit']");
        await page.WaitForURLAsync(url => !url.Contains("/Account/Login"), new PageWaitForURLOptions { Timeout = 10_000 });
    }

    private async Task SkipIfNotRunning()
    {
        try
        {
            await _fixture.SharedHttpClient.GetAsync(_fixture.BaseUrl);
        }
        catch
        {
            Skip.If(true, $"App not running at {_fixture.BaseUrl}");
        }
    }

    [SkippableFact]
    public async Task AllExchangesPassConnectivityTest()
    {
        await SkipIfNotRunning();
        var (page, context) = await CreatePageAsync();
        await using var _ = context;

        await LoginAsync(page);

        // Navigate to connectivity test page
        await page.GotoAsync($"{_fixture.BaseUrl}/Admin/ConnectivityTest");
        Assert.Contains("/Admin/ConnectivityTest", page.Url, StringComparison.OrdinalIgnoreCase);

        // Wait for the user dropdown and select the first user with a non-empty value
        await page.WaitForSelectorAsync("#userSelect", new PageWaitForSelectorOptions { Timeout = 10_000 });
        var firstUserValue = await page.EvalOnSelectorAsync<string?>(
            "#userSelect",
            "select => { const opt = select.querySelector('option[value]:not([value=\"\"])'); return opt ? opt.value : null; }");
        Assert.NotNull(firstUserValue);
        await page.SelectOptionAsync("#userSelect", firstUserValue);

        // Wait for the Test All button to become enabled (AJAX loads user exchanges)
        await page.WaitForFunctionAsync(
            "() => !document.getElementById('btnTestAll').disabled",
            null,
            new PageWaitForFunctionOptions { Timeout = ButtonEnableTimeoutMs });

        // Retry loop
        string? lastFailureMessage = null;
        for (int attempt = 1; attempt <= MaxRetries; attempt++)
        {
            // Click Test All
            await page.ClickAsync("#btnTestAll");

            // Wait for all .exchange-status badges to resolve (no longer "Running...")
            // Uncredentialed exchanges remain "Idle" — that is a valid terminal state
            try
            {
                await page.WaitForFunctionAsync(
                    @"() => {
                        const badges = document.querySelectorAll('.exchange-status');
                        if (badges.length === 0) return false;
                        return Array.from(badges).every(b => {
                            const text = b.textContent.trim();
                            return text !== 'Running...';
                        });
                    }",
                    null,
                    new PageWaitForFunctionOptions { Timeout = BadgeTimeoutMs });
            }
            catch (TimeoutException)
            {
                lastFailureMessage = $"Attempt {attempt}: Timed out waiting for all exchange status badges to resolve within {BadgeTimeoutMs / 1000}s";
                if (attempt < MaxRetries)
                {
                    await ResetForRetry(page, firstUserValue);
                    continue;
                }
                Assert.Fail(lastFailureMessage);
            }

            // Collect badge results
            var badgeResults = await page.EvalOnSelectorAllAsync<string[]>(
                ".exchange-status",
                "badges => badges.map(b => b.textContent.trim())");

            Assert.True(badgeResults.Length > 0, "Expected at least one exchange status badge");

            // Read log panel content
            var logContent = await page.TextContentAsync("#logPanel") ?? "";

            // Check for success — "Idle" badges are uncredentialed exchanges, not failures
            var allPass = badgeResults.All(text => text == "Pass" || text == "Idle");
            var logHasFailure = logContent.Contains("TEST FAILED", StringComparison.OrdinalIgnoreCase);

            if (allPass && !logHasFailure)
            {
                // All exchanges passed
                return;
            }

            // Build failure details — exclude "Idle" (uncredentialed exchanges)
            var failedExchanges = badgeResults
                .Select((text, i) => new { Index = i, Text = text })
                .Where(x => x.Text != "Pass" && x.Text != "Idle")
                .Select(x => $"Badge[{x.Index}]={x.Text}")
                .ToList();

            lastFailureMessage = $"Attempt {attempt}: " +
                (failedExchanges.Count > 0 ? $"Failed exchanges: {string.Join(", ", failedExchanges)}. " : "") +
                (logHasFailure ? "Log panel contains failure indicators. " : "") +
                $"Log excerpt: {Truncate(logContent, 500)}";

            // Best-effort Azure log download
            await TryDownloadAzureLogs(attempt);

            if (attempt < MaxRetries)
            {
                // Reset by re-selecting the user so badges go back to Idle
                await ResetForRetry(page, firstUserValue);
            }
        }

        Assert.Fail($"All {MaxRetries} attempts failed. Last failure: {lastFailureMessage}");
    }

    private async Task ResetForRetry(IPage page, string userValue)
    {
        // Deselect user to reset badges to Idle
        await page.SelectOptionAsync("#userSelect", "");

        // Re-select the user
        await page.SelectOptionAsync("#userSelect", userValue);

        // Wait for button to re-enable
        await page.WaitForFunctionAsync(
            "() => !document.getElementById('btnTestAll').disabled",
            null,
            new PageWaitForFunctionOptions { Timeout = ButtonEnableTimeoutMs });
    }

    private static async Task TryDownloadAzureLogs(int attempt)
    {
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            using var process = new System.Diagnostics.Process();
            process.StartInfo.FileName = "az";
            process.StartInfo.Arguments = $"webapp log download --name fundingratearb --resource-group rg-fundingratearb1 --log-file /tmp/azure-e2e-logs-{attempt}.zip";
            process.StartInfo.RedirectStandardOutput = false;
            process.StartInfo.RedirectStandardError = false;
            process.StartInfo.UseShellExecute = false;
            process.Start();
            await process.WaitForExitAsync(cts.Token);
        }
        catch
        {
            // Log download is best-effort diagnostic, not a test requirement
        }
    }

    private static string Truncate(string value, int maxLength)
    {
        if (string.IsNullOrEmpty(value)) return "(empty)";
        return value.Length <= maxLength ? value : value[..maxLength] + "...";
    }
}
