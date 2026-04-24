using System.Text.RegularExpressions;
using FluentAssertions;

namespace FundingRateArb.Tests.Unit.Views;

/// <summary>
/// Tests that verify the content of _LoginPartial.cshtml matches the required
/// Identity scaffold structure.  These are static file-content tests — they read
/// the source file directly and assert the required Razor/HTML elements are present.
/// </summary>
public class LoginPartialContentTests
{
    private static readonly string LoginPartialPath = Path.GetFullPath(
        Path.Combine(
            AppContext.BaseDirectory,
            "..", "..", "..", "..", "..",
            "src", "FundingRateArb.Web", "Views", "Shared", "_LoginPartial.cshtml"));

    private readonly string _content;

    public LoginPartialContentTests()
    {
        File.Exists(LoginPartialPath).Should().BeTrue(
            $"_LoginPartial.cshtml must exist at {LoginPartialPath}");
        _content = File.ReadAllText(LoginPartialPath);
    }

    // ── Injections ────────────────────────────────────────────────────────────

    [Fact]
    public void File_InjectsSignInManager_WithApplicationUser()
    {
        _content.Should().MatchRegex(
            @"@inject\s+SignInManager<ApplicationUser>",
            "SignInManager<ApplicationUser> must be injected at the top of the partial");
    }

    [Fact]
    public void File_InjectsUserManager_WithApplicationUser()
    {
        _content.Should().MatchRegex(
            @"@inject\s+UserManager<ApplicationUser>",
            "UserManager<ApplicationUser> must be injected at the top of the partial");
    }

    [Fact]
    public void File_HasUsingDirective_ForApplicationUserNamespace()
    {
        _content.Should().Contain(
            "@using FundingRateArb.Domain.Entities",
            "the ApplicationUser namespace must be imported so the injections resolve");
    }

    // ── Top-level structure ───────────────────────────────────────────────────

    [Fact]
    public void File_HasSingleNavbarNavUl()
    {
        _content.Should().Contain(
            @"<ul class=""navbar-nav"">",
            "the partial must emit exactly one <ul class=\"navbar-nav\"> element");
    }

    // ── Authenticated branch ──────────────────────────────────────────────────

    [Fact]
    public void File_ChecksIsSignedIn_ForAuthenticatedBranch()
    {
        _content.Should().Contain(
            "SignInManager.IsSignedIn(User)",
            "the partial must branch on SignInManager.IsSignedIn(User)");
    }

    [Fact]
    public void File_AuthBranch_HasManageAccountLink_WithCorrectAspAttributes()
    {
        _content.Should().Contain(
            @"asp-area=""Identity""",
            "the Manage link must target the Identity area");

        _content.Should().Contain(
            @"asp-page=""/Account/Manage/Index""",
            "the Manage link must point to /Account/Manage/Index");
    }

    [Fact]
    public void File_AuthBranch_HasManageAccountLink_WithNavLinkClass()
    {
        // The anchor for the Manage page must carry nav-link styling
        var hasNavLinkManage = Regex.IsMatch(
            _content,
            @"asp-page=""/Account/Manage/Index""[^>]*class=""[^""]*nav-link[^""]*""" +
            @"|class=""[^""]*nav-link[^""]*""[^>]*asp-page=""/Account/Manage/Index""",
            RegexOptions.Singleline);

        hasNavLinkManage.Should().BeTrue(
            "the Manage account anchor must include the nav-link CSS class");
    }

    [Fact]
    public void File_AuthBranch_DisplaysUserName()
    {
        _content.Should().Contain(
            "User.Identity",
            "the authenticated branch must display the logged-in user's name via User.Identity");

        _content.Should().Contain(
            ".Name",
            "the authenticated branch must display User.Identity!.Name (or equivalent)");
    }

    [Fact]
    public void File_AuthBranch_HasLogoutForm_WithCorrectAttributes()
    {
        _content.Should().Contain(
            @"asp-page=""/Account/Logout""",
            "the logout form must target /Account/Logout");

        _content.Should().Contain(
            @"method=""post""",
            "the logout form must use POST method");
    }

    [Fact]
    public void File_AuthBranch_LogoutForm_HasReturnUrl()
    {
        _content.Should().Contain(
            "asp-route-returnUrl",
            "the logout form must supply an asp-route-returnUrl parameter");
    }

    [Fact]
    public void File_AuthBranch_HasLogoutButton_WithSubmitType()
    {
        _content.Should().Contain(
            @"type=""submit""",
            "the logout must be triggered by a submit button inside the form");

        // The button text should be 'Logout' (case-insensitive)
        _content.Should().MatchRegex(
            @"(?i)logout",
            "the logout button must be labelled 'Logout'");
    }

    // ── Anonymous branch ──────────────────────────────────────────────────────

    [Fact]
    public void File_AnonBranch_HasRegisterLink_WithCorrectAspPage()
    {
        _content.Should().Contain(
            @"asp-page=""/Account/Register""",
            "the anonymous branch must include a Register link pointing to /Account/Register");
    }

    [Fact]
    public void File_AnonBranch_HasLoginLink_WithCorrectAspPage()
    {
        _content.Should().Contain(
            @"asp-page=""/Account/Login""",
            "the anonymous branch must include a Login link pointing to /Account/Login");
    }

    [Fact]
    public void File_AnonBranch_LinksAreInIdentityArea()
    {
        // Both Register and Login links must target the Identity area.
        // Count occurrences of asp-area="Identity" — expect at least 2 (Register + Login)
        // plus the Manage link, so >= 2 in the anonymous block.
        // A simpler check: the content must contain at least two Identity area references.
        var matches = Regex.Matches(_content, @"asp-area=""Identity""");
        matches.Count.Should().BeGreaterThanOrEqualTo(2,
            "at minimum Register and Login links must declare asp-area=\"Identity\"");
    }
}
