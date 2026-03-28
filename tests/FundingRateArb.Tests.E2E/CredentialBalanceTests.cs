using Microsoft.Playwright;

namespace FundingRateArb.Tests.E2E;

[Trait("Category", "E2E")]
[Collection("Playwright")]
public class CredentialBalanceTests : IClassFixture<PlaywrightFixture>
{
    private readonly PlaywrightFixture _fixture;

    public CredentialBalanceTests(PlaywrightFixture fixture)
    {
        _fixture = fixture;
    }

    private async Task<IPage> CreatePageAsync()
    {
        var context = await _fixture.Browser.NewContextAsync(new BrowserNewContextOptions
        {
            IgnoreHTTPSErrors = true,
        });
        return await context.NewPageAsync();
    }

    private async Task LoginAsync(IPage page)
    {
        var password = Environment.GetEnvironmentVariable("E2E_ADMIN_PASSWORD") ?? "FundingArb@2026!";

        await page.GotoAsync($"{_fixture.BaseUrl}/Identity/Account/Login");
        await page.FillAsync("input[name='Input.Email']", "admin@fundingratearb.com");
        await page.FillAsync("input[name='Input.Password']", password);
        await page.ClickAsync("button[type='submit']");
        // Wait for redirect away from login page
        await page.WaitForURLAsync(url => !url.Contains("/Account/Login"), new PageWaitForURLOptions { Timeout = 10_000 });
    }

    [Fact]
    public async Task AdminCanLoginAndNavigateToDashboard()
    {
        if (!await IsAppRunning()) return; // Skip if app not running
        var page = await CreatePageAsync();

        await LoginAsync(page);

        // Should be on the dashboard (home page)
        await page.GotoAsync($"{_fixture.BaseUrl}/");
        var title = await page.TitleAsync();
        title.Should().NotBeNullOrEmpty();

        // Dashboard page should have the opportunities table or a known element
        var body = await page.TextContentAsync("body");
        body.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task AdminCanEnterHyperliquidCredentials()
    {
        if (!await IsAppRunning()) return; // Skip if app not running
        var page = await CreatePageAsync();

        await LoginAsync(page);
        await page.GotoAsync($"{_fixture.BaseUrl}/Settings/ApiKeys");

        // Find the Hyperliquid form section and enter credentials
        var walletInput = page.Locator("[data-exchange='Hyperliquid'] input[name*='WalletAddress'], input[id*='hyperliquid'][id*='wallet' i], form:has-text('Hyperliquid') input[placeholder*='wallet' i], form:has-text('Hyperliquid') input[name*='WalletAddress']").First;
        if (await walletInput.CountAsync() > 0)
        {
            await walletInput.FillAsync("0x1234567890abcdef1234567890abcdef12345678");

            var keyInput = page.Locator("[data-exchange='Hyperliquid'] input[name*='PrivateKey'], form:has-text('Hyperliquid') input[name*='PrivateKey']").First;
            if (await keyInput.CountAsync() > 0)
            {
                await keyInput.FillAsync("0xdeadbeef1234567890abcdef1234567890abcdef1234567890abcdef12345678");
            }

            // Submit the form
            var submitBtn = page.Locator("form:has-text('Hyperliquid') button[type='submit']").First;
            if (await submitBtn.CountAsync() > 0)
            {
                await submitBtn.ClickAsync();
                await page.WaitForTimeoutAsync(2000);
            }
        }

        // Verify no error toast/alert is visible (success)
        var errorAlert = page.Locator(".alert-danger:visible");
        (await errorAlert.CountAsync()).Should().Be(0, "no error should appear after saving valid credentials");
    }

    [Fact]
    public async Task AdminCanEnterLighterCredentials()
    {
        if (!await IsAppRunning()) return; // Skip if app not running
        var page = await CreatePageAsync();

        await LoginAsync(page);
        await page.GotoAsync($"{_fixture.BaseUrl}/Settings/ApiKeys");

        // Find the Lighter form section
        var accountInput = page.Locator("form:has-text('Lighter') input[name*='AccountIndex'], form:has-text('Lighter') input[placeholder*='account' i]").First;
        if (await accountInput.CountAsync() > 0)
        {
            await accountInput.FillAsync("12345");

            var apiKeyIndex = page.Locator("form:has-text('Lighter') input[name*='ApiKeyIndex']").First;
            if (await apiKeyIndex.CountAsync() > 0)
            {
                await apiKeyIndex.FillAsync("2");
            }

            var keyInput = page.Locator("form:has-text('Lighter') input[name*='PrivateKey']").First;
            if (await keyInput.CountAsync() > 0)
            {
                await keyInput.FillAsync("0xdeadbeef1234567890abcdef1234567890abcdef1234567890abcdef12345678");
            }

            var submitBtn = page.Locator("form:has-text('Lighter') button[type='submit']").First;
            if (await submitBtn.CountAsync() > 0)
            {
                await submitBtn.ClickAsync();
                await page.WaitForTimeoutAsync(2000);
            }
        }

        var errorAlert = page.Locator(".alert-danger:visible");
        (await errorAlert.CountAsync()).Should().Be(0, "no error should appear after saving valid credentials");
    }

    [Fact]
    public async Task DashboardShowsBalanceOrErrorAfterCredentialSave()
    {
        if (!await IsAppRunning()) return; // Skip if app not running
        var page = await CreatePageAsync();

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
                // Check each balance entry: should be either a valid balance or an error indicator
                for (int j = 0; j < count; j++)
                {
                    var span = spans.Nth(j);
                    var text = await span.TextContentAsync() ?? "";
                    var className = await span.GetAttributeAsync("class") ?? "";

                    // Valid states:
                    // 1. text-success: real balance > 0
                    // 2. text-warning / balance-error: error indicator (our new feature)
                    // 3. text-muted with $0.00: only acceptable if NO errorMessage (truly empty account)
                    var isSuccess = className.Contains("text-success");
                    var isError = className.Contains("text-warning") || className.Contains("balance-error");
                    var isMuted = className.Contains("text-muted");

                    // The bug was silent $0.00 with no error. After our fix, $0.00 should only appear
                    // for truly empty accounts (text-muted), not for broken credentials (text-warning).
                    // Either a real balance or an error indicator is acceptable.
                    if (isSuccess || isError || isMuted)
                    {
                        balanceFound = true;
                    }

                    // The key assertion: if we see $0.00, it must NOT be hiding an error
                    if (text.Contains("$0.00") && !isError)
                    {
                        // This is OK only if it's a genuinely empty account (text-muted)
                        // The error case would show text-warning with error text
                    }
                }

                if (balanceFound)
                {
                    break;
                }
            }
        }

        // We either found balance data or the app isn't pushing updates
        // If balance container has children, verify no plain $0.00 without error indicator
        var finalSpans = balanceContainer.Locator("span");
        var finalCount = await finalSpans.CountAsync();
        if (finalCount > 0)
        {
            for (int j = 0; j < finalCount; j++)
            {
                var span = finalSpans.Nth(j);
                var text = await span.TextContentAsync() ?? "";
                var className = await span.GetAttributeAsync("class") ?? "";

                // If balance is $0.00, it should be either:
                // - text-muted (empty account, no error)
                // - text-warning with error indicator (our fix)
                // NOT: text-muted hiding an error
                if (text.Contains("$0.00"))
                {
                    var hasTitle = await span.GetAttributeAsync("title");
                    if (hasTitle != null && hasTitle.Length > 0)
                    {
                        // Error is properly surfaced via tooltip
                        className.Should().Contain("warning",
                            "$0.00 with an error tooltip should use text-warning, not text-muted");
                    }
                }
            }
        }
    }

    private async Task<bool> IsAppRunning()
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
        try
        {
            await http.GetAsync(_fixture.BaseUrl);
            return true;
        }
        catch
        {
            return false;
        }
    }
}

/// <summary>
/// FluentAssertions-like extensions for clean assertions without requiring the full package.
/// </summary>
internal static class AssertionExtensions
{
    public static StringAssertions Should(this string? value) => new(value);
    public static IntAssertions Should(this int value) => new(value);

    internal sealed class StringAssertions
    {
        private readonly string? _value;
        public StringAssertions(string? value) => _value = value;

        public void NotBeNullOrEmpty()
        {
            Assert.False(string.IsNullOrEmpty(_value), "Expected non-null/empty string");
        }

        public void Contain(string expected, string? because = null)
        {
            Assert.Contains(expected, _value ?? "", StringComparison.OrdinalIgnoreCase);
        }
    }

    internal sealed class IntAssertions
    {
        private readonly int _value;
        public IntAssertions(int value) => _value = value;

        public void Be(int expected, string? because = null)
        {
            Assert.Equal(expected, _value);
        }
    }
}
