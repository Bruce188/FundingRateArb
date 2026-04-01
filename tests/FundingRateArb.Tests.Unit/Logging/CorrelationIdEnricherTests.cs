using System.Diagnostics;
using FluentAssertions;
using FundingRateArb.Infrastructure.Logging;
using Microsoft.AspNetCore.Http;
using Moq;
using Serilog.Core;
using Serilog.Events;
using Serilog.Parsing;

namespace FundingRateArb.Tests.Unit.Logging;

public class CorrelationIdEnricherTests
{
    private static LogEvent CreateLogEvent()
    {
        return new LogEvent(
            DateTimeOffset.UtcNow,
            LogEventLevel.Information,
            null,
            new MessageTemplate("Test message", new List<MessageTemplateToken>()),
            new List<LogEventProperty>());
    }

    [Fact]
    public void Enrich_WithHttpContext_UsesTraceIdentifier()
    {
        // Arrange
        var httpContext = new DefaultHttpContext();
        httpContext.TraceIdentifier = "test-trace-123";

        var accessor = new Mock<IHttpContextAccessor>();
        accessor.Setup(a => a.HttpContext).Returns(httpContext);

        var enricher = new CorrelationIdEnricher(accessor.Object);
        var logEvent = CreateLogEvent();
        var propertyFactory = new LogEventPropertyFactory();

        // Act
        enricher.Enrich(logEvent, propertyFactory);

        // Assert
        logEvent.Properties.Should().ContainKey("CorrelationId");
        logEvent.Properties["CorrelationId"].ToString().Should().Be("\"test-trace-123\"");
    }

    [Fact]
    public void Enrich_WithActivityCurrent_UsesActivityId()
    {
        // Arrange
        var accessor = new Mock<IHttpContextAccessor>();
        accessor.Setup(a => a.HttpContext).Returns((HttpContext?)null);

        var activity = new Activity("test-operation");
        activity.Start();

        try
        {
            var enricher = new CorrelationIdEnricher(accessor.Object);
            var logEvent = CreateLogEvent();
            var propertyFactory = new LogEventPropertyFactory();

            // Act
            enricher.Enrich(logEvent, propertyFactory);

            // Assert
            logEvent.Properties.Should().ContainKey("CorrelationId");
            var correlationId = logEvent.Properties["CorrelationId"].ToString().Trim('"');
            correlationId.Should().Be(activity.Id);
        }
        finally
        {
            activity.Stop();
        }
    }

    [Fact]
    public void Enrich_WithNoContextOrActivity_GeneratesGuid()
    {
        // Arrange
        var accessor = new Mock<IHttpContextAccessor>();
        accessor.Setup(a => a.HttpContext).Returns((HttpContext?)null);

        // Ensure no Activity is current
        Activity.Current?.Stop();
        Activity.Current = null;

        var enricher = new CorrelationIdEnricher(accessor.Object);
        var logEvent = CreateLogEvent();
        var propertyFactory = new LogEventPropertyFactory();

        // Act
        enricher.Enrich(logEvent, propertyFactory);

        // Assert
        logEvent.Properties.Should().ContainKey("CorrelationId");
        var correlationId = logEvent.Properties["CorrelationId"].ToString().Trim('"');
        correlationId.Should().NotBeNullOrEmpty();
        Guid.TryParse(correlationId, out _).Should().BeTrue("fallback should be a valid GUID");
    }

    /// <summary>
    /// Simple implementation of ILogEventPropertyFactory for testing.
    /// </summary>
    private sealed class LogEventPropertyFactory : ILogEventPropertyFactory
    {
        public LogEventProperty CreateProperty(string name, object? value, bool destructureObjects = false)
        {
            return new LogEventProperty(name, new ScalarValue(value));
        }
    }
}
