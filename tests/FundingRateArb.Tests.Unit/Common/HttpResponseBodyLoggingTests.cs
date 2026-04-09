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

    // ── review-v133 NB2: extended redaction (JSON + alt headers + bearer) ──

    [Fact]
    public void TruncateAndSanitize_JsonApiKeyField_RedactsValue()
    {
        var input = "{\"apiKey\":\"secret123\"}";

        var result = HttpResponseBodyLogging.TruncateAndSanitize(input);

        result.Should().NotContain("secret123");
        result.Should().Contain("REDACTED");
    }

    [Fact]
    public void TruncateAndSanitize_JsonSnakeCaseField_RedactsValue()
    {
        var input = "{\"api_key\":\"secret123\"}";

        var result = HttpResponseBodyLogging.TruncateAndSanitize(input);

        result.Should().NotContain("secret123");
        result.Should().Contain("REDACTED");
    }

    [Fact]
    public void TruncateAndSanitize_JsonTokenField_RedactsValue()
    {
        var input = "{\"token\":\"abc123xyz\"}";

        var result = HttpResponseBodyLogging.TruncateAndSanitize(input);

        result.Should().NotContain("abc123xyz");
        result.Should().Contain("REDACTED");
    }

    [Fact]
    public void TruncateAndSanitize_NakedBearerToken_RedactsValue()
    {
        var input = "unauthorized Bearer abc123def456";

        var result = HttpResponseBodyLogging.TruncateAndSanitize(input);

        result.Should().NotContain("abc123def456");
        result.Should().Contain("REDACTED");
    }

    [Fact]
    public void TruncateAndSanitize_XApiKeyHeader_RedactsValue()
    {
        var input = "X-API-Key: secret123";

        var result = HttpResponseBodyLogging.TruncateAndSanitize(input);

        result.Should().NotContain("secret123");
        result.Should().Contain("REDACTED");
    }

    [Fact]
    public void TruncateAndSanitize_ProxyAuthorizationHeader_RedactsValue()
    {
        var input = "Proxy-Authorization: Bearer abc";

        var result = HttpResponseBodyLogging.TruncateAndSanitize(input);

        result.Should().NotContain(": Bearer abc");
        result.Should().NotContain("abc");
        result.Should().Contain("REDACTED");
    }

    // ── review-v133 NB3: surrogate-pair-safe truncation ──

    [Fact]
    public void TruncateAndSanitize_HighSurrogateAtBoundary_DoesNotSplitPair()
    {
        // 499 'a' chars + a 2-char smiley emoji => total length 501. Default maxChars=500
        // would slice at index 500, landing in the middle of the surrogate pair. The
        // implementation must back off one char so no lone surrogate is emitted.
        var input = new string('a', 499) + "\uD83D\uDE00";

        var result = HttpResponseBodyLogging.TruncateAndSanitize(input);

        // Must be valid UTF-16: no lone high surrogate at the end.
        result.Should().NotBeEmpty();
        var lastChar = result[result.Length - "...".Length - 1];
        char.IsHighSurrogate(lastChar).Should().BeFalse(
            "truncation must not leave a lone high surrogate at the end of the sliced portion");

        // Round-tripping through UTF-8 must not lose data (invalid UTF-16 would lose the
        // lone surrogate as a replacement character).
        var bytes = System.Text.Encoding.UTF8.GetBytes(result);
        var roundTripped = System.Text.Encoding.UTF8.GetString(bytes);
        roundTripped.Should().Be(result);
    }
}
