using System.Net;
using FluentAssertions;
using FundingRateArb.Application.Common.Exchanges;
using FundingRateArb.Application.Interfaces;
using FundingRateArb.Domain.Enums;
using FundingRateArb.Infrastructure.ExchangeConnectors;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace FundingRateArb.Tests.Unit.Connectors;

/// <summary>
/// Tests for LighterConnector.GetOrderbookDepthAsync — the depth gate.
/// Covers WS-cache path, REST-fallback path, insufficient-depth path,
/// REST-failure degrade-gracefully path, and side-to-ladder-walk mapping.
/// </summary>
public class LighterDepthGateTests
{
    private readonly Mock<ILogger<LighterConnector>> _logger = new();
    private readonly Mock<IConfiguration> _config = new();

    // ── Helpers ──────────────────────────────────────────────────────────────

    /// <summary>ETH at market_id=0, BestBid=3500, BestAsk=3501.</summary>
    private static readonly string OrderBookDetailsJson = """
        {
            "code": 200,
            "order_book_details": [
                {
                    "market_id": 0,
                    "symbol": "ETH",
                    "taker_fee": "0.0000",
                    "maker_fee": "0.0000",
                    "min_base_amount": "0.001",
                    "supported_size_decimals": 4,
                    "supported_price_decimals": 2,
                    "size_decimals": 4,
                    "price_decimals": 2,
                    "last_trade_price": 3500.50,
                    "best_bid": 3500.00,
                    "best_ask": 3501.00,
                    "default_initial_margin_fraction": 1000,
                    "min_initial_margin_fraction": 500,
                    "maintenance_margin_fraction": 300
                }
            ]
        }
        """;

    private static string MakeOrderBookJson(
        (decimal price, decimal size)[] bids,
        (decimal price, decimal size)[] asks,
        int marketId = 0)
    {
        static string LevelArr((decimal price, decimal size)[] levels)
        {
            var parts = levels.Select(l => $$$"""{"price": "{{{l.price}}}", "size": "{{{l.size}}}"}""");
            return "[" + string.Join(", ", parts) + "]";
        }

        return $$"""
            {
                "market_id": {{marketId}},
                "timestamp": 1000000,
                "bids": {{LevelArr(bids)}},
                "asks": {{LevelArr(asks)}}
            }
            """;
    }

    private LighterConnector CreateConnectorMultiRoute(Action<MultiRouteHttpMessageHandler> configure)
    {
        var handler = new MultiRouteHttpMessageHandler();
        configure(handler);
        var httpClient = new HttpClient(handler) { BaseAddress = new Uri("https://test.invalid/api/v1/") };
        return new LighterConnector(httpClient, _logger.Object, _config.Object);
    }

    private static LighterMarketDataStream CreateStream()
    {
        var wsClient = new Mock<LighterWebSocketClient>(NullLogger<LighterWebSocketClient>.Instance);
        var cache = new Mock<IMarketDataCache>();
        var factory = new Mock<IHttpClientFactory>();
        return new LighterMarketDataStream(
            wsClient.Object,
            cache.Object,
            factory.Object,
            NullLogger<LighterMarketDataStream>.Instance);
    }

    // ── WS-fresh path ────────────────────────────────────────────────────────

    [Fact]
    public async Task GetOrderbookDepthAsync_WsFreshLadder_ReturnsWsCache()
    {
        var sut = CreateConnectorMultiRoute(h =>
        {
            h.AddRoute("orderBookDetails", OrderBookDetailsJson);
            // REST orderBook should NOT be called on WS hit
        });

        var stream = CreateStream();
        stream.SetMarketMapping(0, "ETH");
        // Inject a 2-level ask ladder: 0.1 @ 3501, 0.5 @ 3502
        stream.SimulateWsBookLadder("ETH",
            bids: new[] { (3500m, 0.5m) },
            asks: new[] { (3501m, 0.1m), (3502m, 1.0m) },
            receivedAt: DateTime.UtcNow);
        sut.SetMarketDataStream(stream);

        // Populate market cache via first call
        await sut.GetMarkPriceAsync("ETH");

        // Request 0.05 ETH long (buy) — should walk asks
        var result = await sut.GetOrderbookDepthAsync("ETH", Side.Long, 0.05m);

        result.Should().NotBeNull();
        result!.Source.Should().Be(OrderbookDepthSource.WsCache);
        result.Asset.Should().Be("ETH");
        result.Side.Should().Be(Side.Long);
        // 0.05 filled entirely at best ask 3501 → avg = 3501
        result.EstimatedAvgFillPrice.Should().Be(3501m);
    }

    // ── REST-fallback path ───────────────────────────────────────────────────

    [Fact]
    public async Task GetOrderbookDepthAsync_NoWsData_FallsBackToRest()
    {
        var orderBookJson = MakeOrderBookJson(
            bids: new[] { (3500m, 2.0m) },
            asks: new[] { (3501m, 2.0m) });

        var sut = CreateConnectorMultiRoute(h =>
        {
            h.AddRoute("orderBookDetails", OrderBookDetailsJson);
            h.AddRoute("orderBook", orderBookJson);
        });

        // No WS stream wired → falls back to REST
        await sut.GetMarkPriceAsync("ETH");

        var result = await sut.GetOrderbookDepthAsync("ETH", Side.Long, 0.5m);

        result.Should().NotBeNull();
        result!.Source.Should().Be(OrderbookDepthSource.RestFallback);
        result.EstimatedAvgFillPrice.Should().Be(3501m);
    }

    // ── Insufficient depth ───────────────────────────────────────────────────

    [Fact]
    public async Task GetOrderbookDepthAsync_InsufficientDepth_ReturnsInsufficientSnapshot()
    {
        // Only 0.1 ETH depth on asks — not enough for 1.0 ETH order
        var orderBookJson = MakeOrderBookJson(
            bids: new[] { (3500m, 0.1m) },
            asks: new[] { (3501m, 0.1m) });

        var sut = CreateConnectorMultiRoute(h =>
        {
            h.AddRoute("orderBookDetails", OrderBookDetailsJson);
            h.AddRoute("orderBook", orderBookJson);
        });

        await sut.GetMarkPriceAsync("ETH");

        var result = await sut.GetOrderbookDepthAsync("ETH", Side.Long, 1.0m);

        result.Should().NotBeNull();
        result!.Source.Should().Be(OrderbookDepthSource.Insufficient);
    }

    // ── REST failure degrade gracefully ─────────────────────────────────────

    [Fact]
    public async Task GetOrderbookDepthAsync_RestFails_ReturnsNull()
    {
        var sut = CreateConnectorMultiRoute(h =>
        {
            h.AddRoute("orderBookDetails", OrderBookDetailsJson);
            h.AddRoute("orderBook", "{}", HttpStatusCode.InternalServerError);
        });

        await sut.GetMarkPriceAsync("ETH");

        var result = await sut.GetOrderbookDepthAsync("ETH", Side.Long, 0.5m);

        // REST failed → gate is skipped (returns null, not Insufficient)
        result.Should().BeNull();
    }

    // ── Side-to-ladder mapping ───────────────────────────────────────────────

    [Fact]
    public async Task GetOrderbookDepthAsync_LongSide_WalksAsks()
    {
        // Asks: [3501 @ 0.5, 3502 @ 0.5], Bids: [3500 @ 2.0]
        var orderBookJson = MakeOrderBookJson(
            bids: new[] { (3500m, 2.0m) },
            asks: new[] { (3501m, 0.5m), (3502m, 0.5m) });

        var sut = CreateConnectorMultiRoute(h =>
        {
            h.AddRoute("orderBookDetails", OrderBookDetailsJson);
            h.AddRoute("orderBook", orderBookJson);
        });

        await sut.GetMarkPriceAsync("ETH");

        // Buy 0.5 ETH → fill 0.5 @ 3501 → avg = 3501
        var result = await sut.GetOrderbookDepthAsync("ETH", Side.Long, 0.5m);

        result!.Source.Should().Be(OrderbookDepthSource.RestFallback);
        result.EstimatedAvgFillPrice.Should().Be(3501m);
    }

    [Fact]
    public async Task GetOrderbookDepthAsync_ShortSide_WalksBids()
    {
        // Bids: [3500 @ 0.5, 3499 @ 0.5], Asks: [3501 @ 2.0]
        var orderBookJson = MakeOrderBookJson(
            bids: new[] { (3500m, 0.5m), (3499m, 0.5m) },
            asks: new[] { (3501m, 2.0m) });

        var sut = CreateConnectorMultiRoute(h =>
        {
            h.AddRoute("orderBookDetails", OrderBookDetailsJson);
            h.AddRoute("orderBook", orderBookJson);
        });

        await sut.GetMarkPriceAsync("ETH");

        // Sell 0.5 ETH → fill 0.5 @ 3500 (best bid) → avg = 3500
        var result = await sut.GetOrderbookDepthAsync("ETH", Side.Short, 0.5m);

        result!.Source.Should().Be(OrderbookDepthSource.RestFallback);
        result.EstimatedAvgFillPrice.Should().Be(3500m);
    }

    [Fact]
    public async Task GetOrderbookDepthAsync_MultiLevelFill_ReturnsWeightedAvg()
    {
        // Asks: [3501 @ 0.3, 3502 @ 0.7] — fill 0.5 ETH
        var orderBookJson = MakeOrderBookJson(
            bids: new[] { (3500m, 2.0m) },
            asks: new[] { (3501m, 0.3m), (3502m, 0.7m) });

        var sut = CreateConnectorMultiRoute(h =>
        {
            h.AddRoute("orderBookDetails", OrderBookDetailsJson);
            h.AddRoute("orderBook", orderBookJson);
        });

        await sut.GetMarkPriceAsync("ETH");

        // Buy 0.5 ETH: take 0.3 @ 3501 + 0.2 @ 3502 = (3501*0.3 + 3502*0.2) / 0.5 = 3501.4
        var result = await sut.GetOrderbookDepthAsync("ETH", Side.Long, 0.5m);

        result!.EstimatedAvgFillPrice.Should().BeApproximately(3501.4m, 0.001m);
    }

    // ── No market data → returns null ────────────────────────────────────────

    [Fact]
    public async Task GetOrderbookDepthAsync_NoMarketData_ReturnsNull()
    {
        var sut = CreateConnectorMultiRoute(h =>
        {
            h.AddRoute("orderBookDetails", """{"code":200,"order_book_details":[]}""");
        });

        var result = await sut.GetOrderbookDepthAsync("ETH", Side.Long, 0.1m);
        result.Should().BeNull();
    }

    // ── WS stale → falls back to REST ────────────────────────────────────────

    [Fact]
    public async Task GetOrderbookDepthAsync_WsStale_FallsBackToRest()
    {
        var orderBookJson = MakeOrderBookJson(
            bids: new[] { (3500m, 2.0m) },
            asks: new[] { (3501m, 2.0m) });

        var sut = CreateConnectorMultiRoute(h =>
        {
            h.AddRoute("orderBookDetails", OrderBookDetailsJson);
            h.AddRoute("orderBook", orderBookJson);
        });

        var stream = CreateStream();
        stream.SetMarketMapping(0, "ETH");
        // Inject an old (stale) WS ladder
        stream.SimulateWsBookLadder("ETH",
            bids: new[] { (3500m, 2.0m) },
            asks: new[] { (3501m, 2.0m) },
            receivedAt: DateTime.UtcNow - TimeSpan.FromMinutes(10));
        sut.SetMarketDataStream(stream);

        await sut.GetMarkPriceAsync("ETH");

        var result = await sut.GetOrderbookDepthAsync("ETH", Side.Long, 0.5m);

        // Stale WS → REST fallback
        result.Should().NotBeNull();
        result!.Source.Should().Be(OrderbookDepthSource.RestFallback);
    }
}
