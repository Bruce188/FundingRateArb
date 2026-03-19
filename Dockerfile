FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

# Copy solution and all project files for layer-cached restore
COPY FundingRateArb.sln ./
COPY src/FundingRateArb.Domain/FundingRateArb.Domain.csproj src/FundingRateArb.Domain/
COPY src/FundingRateArb.Application/FundingRateArb.Application.csproj src/FundingRateArb.Application/
COPY src/FundingRateArb.Infrastructure/FundingRateArb.Infrastructure.csproj src/FundingRateArb.Infrastructure/
COPY src/FundingRateArb.Web/FundingRateArb.Web.csproj src/FundingRateArb.Web/
COPY tests/FundingRateArb.Tests.Unit/FundingRateArb.Tests.Unit.csproj tests/FundingRateArb.Tests.Unit/
COPY tests/FundingRateArb.Tests.Integration/FundingRateArb.Tests.Integration.csproj tests/FundingRateArb.Tests.Integration/
RUN dotnet restore src/FundingRateArb.Web/FundingRateArb.Web.csproj

# Copy source and publish (tests excluded via .dockerignore)
COPY src/ src/
RUN dotnet publish src/FundingRateArb.Web/FundingRateArb.Web.csproj \
    -c Release -o /app/publish --no-restore

# Runtime image
FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS runtime
WORKDIR /app

# Create non-root user
RUN groupadd -r appuser && useradd -r -g appuser -d /app appuser

# Create directories for data protection keys and logs
RUN mkdir -p /app/DataProtection-Keys /app/logs \
    && chown -R appuser:appuser /app

COPY --from=build /app/publish .

USER appuser

EXPOSE 8080
ENV ASPNETCORE_URLS=http://+:8080
ENV ASPNETCORE_ENVIRONMENT=Production

ENTRYPOINT ["dotnet", "FundingRateArb.Web.dll"]
