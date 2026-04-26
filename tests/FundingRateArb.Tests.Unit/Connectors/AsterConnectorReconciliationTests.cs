using System.Net;
using Aster.Net.Interfaces.Clients;
using Aster.Net.Interfaces.Clients.FuturesApi;
using Aster.Net.Objects;
using Aster.Net.Objects.Models;
using CryptoExchange.Net.Objects;
using FluentAssertions;
using FundingRateArb.Application.Common.Exchanges;
using FundingRateArb.Infrastructure.ExchangeConnectors;
using Microsoft.Extensions.Logging;
using Moq;
using Polly;
using Polly.Registry;

namespace FundingRateArb.Tests.Unit.Connectors;

public class AsterConnectorReconciliationTests
{
    private static ResiliencePipelineProvider<string> BuildEmptyPipelineProvider()
    {
        var mock = new Mock<ResiliencePipelineProvider<string>>();
        mock.Setup(p => p.GetPipeline(It.IsAny<string>()))
            .Returns(ResiliencePipeline.Empty);
        return mock.Object;
    }

    private static ILogger<AsterConnector> BuildNullLogger()
        => new Mock<ILogger<AsterConnector>>().Object;

    private static WebCallResult<AsterMarkPrice[]> SuccessMarkPrices(AsterMarkPrice[] data)
        => new WebCallResult<AsterMarkPrice[]>(
            HttpStatusCode.OK, null, null, null, null, null, null, null, null,
            null, null, ResultDataSource.Server, data, null);

    private static WebCallResult<AsterTicker[]> SuccessTickers(AsterTicker[] data)
        => new WebCallResult<AsterTicker[]>(
            HttpStatusCode.OK, null, null, null, null, null, null, null, null,
            null, null, ResultDataSource.Server, data, null);

    private static WebCallResult<AsterFundingInfo[]> SuccessFundingInfo(AsterFundingInfo[] data)
        => new WebCallResult<AsterFundingInfo[]>(
            HttpStatusCode.OK, null, null, null, null, null, null, null, null,
            null, null, ResultDataSource.Server, data, null);

    [Fact]
    public async Task GetFundingRatesAsync_ReconciliationInvariant_HoldsAcrossMixedIntervalFixture()
    {
        // Mixed fixture:
        //   ETHUSDT  — funding-info=4h, nonzero rawRate
        //   BTCUSDT  — funding-info=8h, nonzero rawRate
        //   SOLUSDT  — funding-info missing, NextFundingTime ~30m away (snaps to 4h slot)
        var now = DateTime.UtcNow;

        var prices = new[]
        {
            new AsterMarkPrice { Symbol = "ETHUSDT", MarkPrice = 3500m, IndexPrice = 3495m, FundingRate = 0.000400m, NextFundingTime = now.AddHours(1) },
            new AsterMarkPrice { Symbol = "BTCUSDT", MarkPrice = 65000m, IndexPrice = 64980m, FundingRate = 0.000800m, NextFundingTime = now.AddHours(1) },
            new AsterMarkPrice { Symbol = "SOLUSDT", MarkPrice = 150m, IndexPrice = 149m, FundingRate = 0.000200m, NextFundingTime = now.AddMinutes(30) },
        };

        var exchangeDataMock = new Mock<IAsterRestClientFuturesApiExchangeData>();
        exchangeDataMock
            .Setup(x => x.GetMarkPricesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(SuccessMarkPrices(prices));
        exchangeDataMock
            .Setup(x => x.GetTickersAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(SuccessTickers([]));
        exchangeDataMock
            .Setup(x => x.GetFundingInfoAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(SuccessFundingInfo([
                new AsterFundingInfo { Symbol = "ETHUSDT", FundingIntervalHours = 4 },
                new AsterFundingInfo { Symbol = "BTCUSDT", FundingIntervalHours = 8 },
                // SOLUSDT intentionally absent — interval inferred from NextFundingTime
            ]));

        var futuresApiMock = new Mock<IAsterRestClientFuturesApi>();
        futuresApiMock.SetupGet(f => f.ExchangeData).Returns(exchangeDataMock.Object);

        var clientMock = new Mock<IAsterRestClient>();
        clientMock.SetupGet(c => c.FuturesApi).Returns(futuresApiMock.Object);

        var sut = new AsterConnector(clientMock.Object, BuildEmptyPipelineProvider(), BuildNullLogger(), new SingletonMarkPriceCache());

        var rates = await sut.GetFundingRatesAsync();

        rates.Should().NotBeEmpty();

        foreach (var dto in rates)
        {
            dto.DetectedFundingIntervalHours.Should().NotBeNull(
                $"every DTO in this fixture must have a detected interval (symbol={dto.Symbol})");

            dto.RatePerHour.Should().Be(
                dto.RawRate / (decimal)dto.DetectedFundingIntervalHours!.Value,
                $"invariant RatePerHour == RawRate / DetectedFundingIntervalHours must hold for {dto.Symbol}");
        }
    }
}
