using Microsoft.Playwright;

namespace FundingRateArb.Tests.E2E;

[Trait("Category", "E2E")]
[Collection("Playwright")]
public class CredentialBalanceTests
{
    private readonly PlaywrightFixture _fixture;

    public CredentialBalanceTests(PlaywrightFixture fixture)
    {
        _fixture = fixture;
    }

    private async Task<(IPage Page, IBrowserContext Context)> CreatePageAsync()
    {
        var isLocalhost = _fixture.BaseUrl.Contains("localhost") || _fixture.BaseUrl.Contains("127.0.0.1");
        var context = await _fixture.Browser.NewContextAsync(new BrowserNewContextOptions
        {
            IgnoreHTTPSErrors = isLocalhost, // N4: Only ignore HTTPS errors for localhost
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
        // Wait for redirect away from login page
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
    public async Task AdminCanLoginAndNavigateToDashboard()
    {
        await SkipIfNotRunning();
        var (page, context) = await CreatePageAsync();
        await using var _ = context; // NB3: ensure context is disposed

        await LoginAsync(page);

        // Should be on the dashboard (home page)
        await page.GotoAsync($"{_fixture.BaseUrl}/");
        var title = await page.TitleAsync();
        Assert.False(string.IsNullOrEmpty(title), "Dashboard page title should not be empty");

        // Dashboard page should have the opportunities table or a known element
        var body = await page.TextContentAsync("body");
        Assert.False(string.IsNullOrEmpty(body), "Dashboard body content should not be empty");
    }

    [SkippableFact]
    public async Task AdminCanEnterHyperliquidCredentials()
    {
        await SkipIfNotRunning();
        var (page, context) = await CreatePageAsync();
        await using var _ = context;

        await LoginAsync(page);
        await page.GotoAsync($"{_fixture.BaseUrl}/Settings/ApiKeys");

        // N6: Fail with descriptive message when selectors don't match
        var walletInput = page.Locator(
            "[data-exchange='Hyperliquid'] input[name*='WalletAddress'], " +
            "form:has-text('Hyperliquid') input[name*='WalletAddress'], " +
            "form:has-text('Hyperliquid') input[placeholder*='wallet' i]").First;

        var walletCount = await walletInput.CountAsync();
        Assert.True(walletCount > 0, "Hyperliquid wallet address input not found on ApiKeys page. " +
            "Verify the page contains a form with exchange name 'Hyperliquid' and a WalletAddress field.");

        await walletInput.FillAsync("0x0000000000000000000000000000000000000001");

        var keyInput = page.Locator(
            "[data-exchange='Hyperliquid'] input[name*='PrivateKey'], " +
            "form:has-text('Hyperliquid') input[name*='PrivateKey']").First;
        Assert.True(await keyInput.CountAsync() > 0, "Hyperliquid private key input not found");
        await keyInput.FillAsync("0x0000000000000000000000000000000000000000000000000000000000000001");

        // Submit the form
        var submitBtn = page.Locator("form:has-text('Hyperliquid') button[type='submit']").First;
        Assert.True(await submitBtn.CountAsync() > 0, "Hyperliquid submit button not found");
        await submitBtn.ClickAsync();
        await page.WaitForLoadStateAsync(LoadState.NetworkIdle);

        // Verify no error toast/alert is visible (success)
        var errorAlert = page.Locator(".alert-danger:visible");
        Assert.Equal(0, await errorAlert.CountAsync());
    }

    [SkippableFact]
    public async Task AdminCanEnterLighterCredentials()
    {
        await SkipIfNotRunning();
        var (page, context) = await CreatePageAsync();
        await using var _ = context;

        await LoginAsync(page);
        await page.GotoAsync($"{_fixture.BaseUrl}/Settings/ApiKeys");

        // N6: Fail with descriptive message when selectors don't match
        var accountInput = page.Locator(
            "form:has-text('Lighter') input[name*='AccountIndex'], " +
            "form:has-text('Lighter') input[placeholder*='account' i]").First;

        var accountCount = await accountInput.CountAsync();
        Assert.True(accountCount > 0, "Lighter account index input not found on ApiKeys page. " +
            "Verify the page contains a form with exchange name 'Lighter' and an AccountIndex field.");

        await accountInput.FillAsync("12345");

        var apiKeyIndex = page.Locator("form:has-text('Lighter') input[name*='ApiKeyIndex']").First;
        if (await apiKeyIndex.CountAsync() > 0)
        {
            await apiKeyIndex.FillAsync("2");
        }

        var keyInput = page.Locator("form:has-text('Lighter') input[name*='PrivateKey']").First;
        Assert.True(await keyInput.CountAsync() > 0, "Lighter private key input not found");
        await keyInput.FillAsync("0x0000000000000000000000000000000000000000000000000000000000000001");

        var submitBtn = page.Locator("form:has-text('Lighter') button[type='submit']").First;
        Assert.True(await submitBtn.CountAsync() > 0, "Lighter submit button not found");
        await submitBtn.ClickAsync();
        await page.WaitForLoadStateAsync(LoadState.NetworkIdle);

        var errorAlert = page.Locator(".alert-danger:visible");
        Assert.Equal(0, await errorAlert.CountAsync());
    }

    [SkippableFact]
    public async Task DashboardShowsBalanceOrErrorAfterCredentialSave()
    {
        await SkipIfNotRunning();
        var (page, context) = await CreatePageAsync();
        await using var _ = context;

        await LoginAsync(page);

        // Navigate to dashboard
        await page.GotoAsync($"{_fixture.BaseUrl}/");

        // N7: Use WaitForSelectorAsync instead of polling loop for balance display
        var balanceContainer = page.Locator("#exchange-balances");
        try
        {
            await balanceContainer.Locator("span.text-success, span.text-warning, span.text-muted")
                .First.WaitForAsync(new LocatorWaitForOptions { Timeout = 90_000 });
        }
        catch (TimeoutException)
        {
            Assert.Fail("Balance data should appear within 90 seconds via SignalR. " +
                "Either no ReceiveBalanceUpdate was pushed, or the #exchange-balances container has no spans.");
        }

        // NB1: Verify balance spans have meaningful state.
        // Dashboard JS renders: text-success for positive balances, text-warning for errors,
        // text-muted for $0.00 (no title attribute). A $0.00 with text-muted is acceptable
        // (means the exchange returned zero). A $0.00 with a title attribute would indicate
        // an error message displayed via tooltip.
        var finalSpans = balanceContainer.Locator("span");
        var finalCount = await finalSpans.CountAsync();
        Assert.True(finalCount > 0, "Expected at least one balance span in #exchange-balances");

        for (int j = 0; j < finalCount; j++)
        {
            var span = finalSpans.Nth(j);
            var text = await span.TextContentAsync() ?? "";
            var className = await span.GetAttributeAsync("class") ?? "";

            if (text.Contains("$0.00"))
            {
                // $0.00 with text-muted (no title) = genuine zero balance — acceptable
                // $0.00 with title attribute = error indicator shown via tooltip
                var title = await span.GetAttributeAsync("title");
                var hasErrorTitle = !string.IsNullOrEmpty(title);
                var isMuted = className.Contains("text-muted");

                Assert.True(isMuted || hasErrorTitle,
                    $"Balance showing $0.00 must be either text-muted (zero balance) or have a title (error tooltip). " +
                    $"Found class=\"{className}\", title=\"{title ?? "(null)"}\"");
            }
        }
    }
}
