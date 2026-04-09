using FluentAssertions;
using FundingRateArb.Infrastructure.Common;

namespace FundingRateArb.Tests.Unit.Common;

public class HttpResponseBodyLoggingTests
{
    [Fact]
    public void TruncateAndSanitize_BodyUnder500Chars_ReturnsUnchanged()
    {
        var input = "short body";

        var result = HttpResponseBodyLogging.TruncateAndSanitize(input);

        result.Should().Be("short body");
    }

    [Fact]
    public void TruncateAndSanitize_BodyOver500Chars_TruncatesAt500WithEllipsis()
    {
        var input = new string('a', 600);

        var result = HttpResponseBodyLogging.TruncateAndSanitize(input);

        result.Should().HaveLength(503);
        result.Should().EndWith("...");
        result.Should().StartWith(new string('a', 500));
    }

    [Fact]
    public void TruncateAndSanitize_ContainsCgApiKey_RedactsValue()
    {
        // Header redaction is line-greedy: anything after "CG-API-KEY:" until end-of-line
        // is replaced with REDACTED so "Bearer <token>" / "key secret" forms don't leak.
        var input = "CG-API-KEY: secret123\nother text";

        var result = HttpResponseBodyLogging.TruncateAndSanitize(input);

        result.Should().NotContain("secret123");
        result.Should().Contain("REDACTED");
        result.Should().Contain("other text");
    }

    [Fact]
    public void TruncateAndSanitize_ContainsAuthorization_RedactsValue()
    {
        var input = "Authorization: Bearer abc123\nremaining";

        var result = HttpResponseBodyLogging.TruncateAndSanitize(input);

        result.Should().NotContain("Bearer");
        result.Should().NotContain("abc123");
        result.Should().Contain("REDACTED");
        result.Should().Contain("remaining");
    }

    [Fact]
    public void TruncateAndSanitize_NullBody_ReturnsEmpty()
    {
        var result = HttpResponseBodyLogging.TruncateAndSanitize(null);

        result.Should().Be(string.Empty);
    }

    [Fact]
    public void TruncateAndSanitize_EmptyBody_ReturnsEmpty()
    {
        var result = HttpResponseBodyLogging.TruncateAndSanitize(string.Empty);

        result.Should().Be(string.Empty);
    }

    [Fact]
    public void TruncateAndSanitize_CaseInsensitiveHeaderMatch_Redacts()
    {
        var input = "cg-api-key: lower_case_secret plus rest";

        var result = HttpResponseBodyLogging.TruncateAndSanitize(input);

        result.Should().NotContain("lower_case_secret");
        result.Should().Contain("REDACTED");
    }
}
