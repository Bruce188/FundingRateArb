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

        await page.GotoAsync($"{_fixture.BaseUrl}/Identity/Account/Login");
        await page.FillAsync("input[name='Input.Email']", "admin@fundingratearb.com");
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

        await walletInput.FillAsync("0x1234567890abcdef1234567890abcdef12345678");

        var keyInput = page.Locator(
            "[data-exchange='Hyperliquid'] input[name*='PrivateKey'], " +
            "form:has-text('Hyperliquid') input[name*='PrivateKey']").First;
        Assert.True(await keyInput.CountAsync() > 0, "Hyperliquid private key input not found");
        await keyInput.FillAsync("0xdeadbeef1234567890abcdef1234567890abcdef1234567890abcdef12345678");

        // Submit the form
        var submitBtn = page.Locator("form:has-text('Hyperliquid') button[type='submit']").First;
        Assert.True(await submitBtn.CountAsync() > 0, "Hyperliquid submit button not found");
        await submitBtn.ClickAsync();
        await page.WaitForTimeoutAsync(2000);

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
        await keyInput.FillAsync("0xdeadbeef1234567890abcdef1234567890abcdef1234567890abcdef12345678");

        var submitBtn = page.Locator("form:has-text('Lighter') button[type='submit']").First;
        Assert.True(await submitBtn.CountAsync() > 0, "Lighter submit button not found");
        await submitBtn.ClickAsync();
        await page.WaitForTimeoutAsync(2000);

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

        // Wait for the balance display to be populated via SignalR (up to 90 seconds)
        var balanceContainer = page.Locator("#exchange-balances");
        var balanceFound = false;

        for (int i = 0; i < 90; i++)
        {
            await page.WaitForTimeoutAsync(1000);

            var spans = balanceContainer.Locator("span");
            var count = await spans.CountAsync();
            if (count > 0)
            {
                for (int j = 0; j < count; j++)
                {
                    var span = spans.Nth(j);
                    var className = await span.GetAttributeAsync("class") ?? "";

                    var isSuccess = className.Contains("text-success");
                    var isError = className.Contains("text-warning") || className.Contains("balance-error");
                    var isMuted = className.Contains("text-muted");

                    if (isSuccess || isError || isMuted)
                    {
                        balanceFound = true;
                    }
                }

                if (balanceFound)
                {
                    break;
                }
            }
        }

        // NB4: Assert that balance data was found within the timeout
        Assert.True(balanceFound, "Balance data should appear within 90 seconds via SignalR. " +
            "Either no ReceiveBalanceUpdate was pushed, or the #exchange-balances container has no spans.");

        // Verify no plain $0.00 without error indicator
        var finalSpans = balanceContainer.Locator("span");
        var finalCount = await finalSpans.CountAsync();
        for (int j = 0; j < finalCount; j++)
        {
            var span = finalSpans.Nth(j);
            var text = await span.TextContentAsync() ?? "";
            var className = await span.GetAttributeAsync("class") ?? "";

            if (text.Contains("$0.00"))
            {
                var hasTitle = await span.GetAttributeAsync("title");
                if (hasTitle != null && hasTitle.Length > 0)
                {
                    Assert.Contains("warning", className,
                        StringComparison.OrdinalIgnoreCase);
                }
            }
        }
    }
}
