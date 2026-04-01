using System.Threading.RateLimiting;
using AspNet.Security.OAuth.GitHub;
using Azure.Identity;
using CryptoExchange.Net.Authentication;
using FundingRateArb.Application.Common.Exchanges;
using FundingRateArb.Application.Common.Interfaces;
using FundingRateArb.Application.Common.Repositories;
using FundingRateArb.Application.Interfaces;
using FundingRateArb.Application.Services;
using FundingRateArb.Domain.Entities;
using FundingRateArb.Infrastructure.BackgroundServices;
using FundingRateArb.Infrastructure.Data;
using FundingRateArb.Infrastructure.HealthChecks;
using FundingRateArb.Infrastructure.ExchangeConnectors;
using FundingRateArb.Infrastructure.Hubs;
using FundingRateArb.Infrastructure.Repositories;
using FundingRateArb.Infrastructure.Seed;
using FundingRateArb.Infrastructure.Services;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Http.Resilience;
using Polly;
using Polly.CircuitBreaker;
using Polly.Retry;
using Polly.Timeout;
using Serilog;
using Serilog.Events;
using Serilog.Sinks.MSSqlServer;

// Bootstrap logger — captures startup errors before full Serilog is configured
Log.Logger = new LoggerConfiguration()
    .MinimumLevel.Information()
    .MinimumLevel.Override("Microsoft", LogEventLevel.Warning)
    .MinimumLevel.Override("Microsoft.EntityFrameworkCore", LogEventLevel.Warning)
    .Enrich.FromLogContext()
    .WriteTo.Console()
    .CreateBootstrapLogger();

try
{
    Log.Information("Starting FundingRateArb application");

    var builder = WebApplication.CreateBuilder(args);

    // --- Azure Key Vault (production secrets) ---
    var keyVaultName = builder.Configuration["KeyVaultName"];
    if (!string.IsNullOrEmpty(keyVaultName))
    {
        builder.Configuration.AddAzureKeyVault(
            new Uri($"https://{keyVaultName}.vault.azure.net/"),
            new DefaultAzureCredential());
    }

    // --- HttpContextAccessor (needed by CorrelationIdEnricher and other services) ---
    builder.Services.AddHttpContextAccessor();

    // --- Serilog (replaces default ILogger) ---
    var isDevelopment = builder.Environment.IsDevelopment();
    builder.Services.AddSerilog((sp, lc) =>
    {
        lc.ReadFrom.Configuration(builder.Configuration)
            .MinimumLevel.Information()
            .MinimumLevel.Override("Microsoft", LogEventLevel.Warning)
            .MinimumLevel.Override("Microsoft.EntityFrameworkCore", LogEventLevel.Warning)
            .Enrich.FromLogContext()
            .Enrich.WithMachineName()
            .Enrich.WithThreadId()
            .Enrich.WithProperty("Application", "FundingRateArb")
            .Enrich.With<FundingRateArb.Infrastructure.Logging.SensitiveDataMaskingEnricher>()
            .Enrich.With(new FundingRateArb.Infrastructure.Logging.CorrelationIdEnricher(
                sp.GetRequiredService<Microsoft.AspNetCore.Http.IHttpContextAccessor>()))

            // Console sink
            .WriteTo.Console(outputTemplate:
                "[{Timestamp:HH:mm:ss} {Level:u3}] [{CorrelationId}] {SourceContext}{NewLine}" +
                "  {Message:lj}{NewLine}{Exception}")

            ;

        if (isDevelopment)
        {
            lc.WriteTo.File(
                path: "logs/fundingratearb-.log",
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 30,
                outputTemplate:
                    "{Timestamp:yyyy-MM-dd HH:mm:ss.fff} [{Level:u3}] " +
                    "{SourceContext} | {Message:lj} | {Properties:j}{NewLine}{Exception}");
        }

        // SQL Server audit sink — only when a real SQL Server is available (not LocalDB on Linux)
        if (!isDevelopment)
        {
            lc.WriteTo.MSSqlServer(
                connectionString: builder.Configuration.GetConnectionString("DefaultConnection"),
                sinkOptions: new MSSqlServerSinkOptions
                {
                    TableName = "AuditLogs",
                    AutoCreateSqlTable = true,
                    BatchPostingLimit = 50,
                    BatchPeriod = TimeSpan.FromSeconds(5),
                },
                restrictedToMinimumLevel: LogEventLevel.Warning);
        }
    });

    // --- Application Insights (auto-collects when ConnectionString is configured) ---
    builder.Services.AddApplicationInsightsTelemetry();

    // --- Database ---
    builder.Services.AddDbContext<AppDbContext>(options =>
        options.UseSqlServer(
            builder.Configuration.GetConnectionString("DefaultConnection"),
            sqlOpts => sqlOpts.EnableRetryOnFailure(
                maxRetryCount: 5,
                maxRetryDelay: TimeSpan.FromSeconds(30),
                errorNumbersToAdd: null)));

    // --- Identity ---
    builder.Services.AddDefaultIdentity<ApplicationUser>(options =>
    {
        options.Password.RequireDigit = true;
        options.Password.RequiredLength = 12;
        options.Password.RequireLowercase = true;
        options.Password.RequireUppercase = true;
        options.Password.RequireNonAlphanumeric = true;
        options.SignIn.RequireConfirmedAccount = false;
        options.Lockout.DefaultLockoutTimeSpan = TimeSpan.FromMinutes(15);
        options.Lockout.MaxFailedAccessAttempts = 5;
        options.Lockout.AllowedForNewUsers = true;
    })
    .AddRoles<IdentityRole>()
    .AddEntityFrameworkStores<AppDbContext>();

    // --- External OAuth Providers ---
    var googleClientId = builder.Configuration["Authentication:Google:ClientId"];
    var googleClientSecret = builder.Configuration["Authentication:Google:ClientSecret"];
    var githubClientId = builder.Configuration["Authentication:GitHub:ClientId"];
    var githubClientSecret = builder.Configuration["Authentication:GitHub:ClientSecret"];

    var authBuilder = builder.Services.AddAuthentication();
    if (!string.IsNullOrEmpty(googleClientId) && !string.IsNullOrEmpty(googleClientSecret))
    {
        authBuilder.AddGoogle(options =>
        {
            options.ClientId = googleClientId;
            options.ClientSecret = googleClientSecret;
        });
    }
    if (!string.IsNullOrEmpty(githubClientId) && !string.IsNullOrEmpty(githubClientSecret))
    {
        authBuilder.AddGitHub(options =>
        {
            options.ClientId = githubClientId;
            options.ClientSecret = githubClientSecret;
        });
    }

    // --- Cookie security ---
    builder.Services.ConfigureApplicationCookie(options =>
    {
        options.Cookie.SecurePolicy = isDevelopment
            ? Microsoft.AspNetCore.Http.CookieSecurePolicy.SameAsRequest
            : Microsoft.AspNetCore.Http.CookieSecurePolicy.Always;
        options.Cookie.SameSite = Microsoft.AspNetCore.Http.SameSiteMode.Strict;
        options.Cookie.HttpOnly = true;
        options.ExpireTimeSpan = TimeSpan.FromHours(8);
        options.SlidingExpiration = true;
    });

    // --- Data Protection (for IApiKeyVault) ---
    var dataProtection = builder.Services.AddDataProtection()
        .SetApplicationName("FundingRateArb");

    if (isDevelopment)
    {
        var dpKeysDir = new DirectoryInfo(Path.Combine(builder.Environment.ContentRootPath, "dp-keys"));
        if (!dpKeysDir.Exists)
        {
            dpKeysDir.Create();
        }

        if (!OperatingSystem.IsWindows())
        {
            dpKeysDir.UnixFileMode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
        }

        dataProtection.PersistKeysToFileSystem(dpKeysDir);
    }
    else
    {
        var dpBlobConn = builder.Configuration["DataProtection:BlobStorageConnection"];
        if (!string.IsNullOrEmpty(dpBlobConn))
        {
            dataProtection.PersistKeysToAzureBlobStorage(dpBlobConn, "dataprotection", "keys.xml");
        }
    }
    builder.Services.AddScoped<IApiKeyVault, ApiKeyVault>();

    // --- Caching ---
    builder.Services.AddMemoryCache();

    // --- Unit of Work (cursus BankingApp pattern) ---
    builder.Services.AddScoped<IUnitOfWork, UnitOfWork>();

    // --- Services ---
    builder.Services.AddScoped<ISignalEngine, SignalEngine>();
    builder.Services.AddScoped<IPositionSizer, PositionSizer>();
    builder.Services.AddScoped<IExecutionEngine, ExecutionEngine>();
    builder.Services.AddScoped<IPositionHealthMonitor, PositionHealthMonitor>();
    builder.Services.AddSingleton<IYieldCalculator, YieldCalculator>();
    builder.Services.AddSingleton<IConfigValidator, ConfigValidator>();
    builder.Services.AddScoped<IUserSettingsService, UserSettingsService>();
    builder.Services.AddScoped<IBalanceAggregator, BalanceAggregator>();
    builder.Services.AddScoped<ITradeAnalyticsService, TradeAnalyticsService>();
    builder.Services.AddScoped<IRateAnalyticsService, RateAnalyticsService>();
    builder.Services.AddScoped<IRatePredictionService, RatePredictionService>();
    builder.Services.AddScoped<IPortfolioRebalancer, PortfolioRebalancer>();
    builder.Services.AddSingleton<IEmailService, EmailService>();
    builder.Services.AddScoped<IConnectivityTestService, ConnectivityTestService>();

    // --- Polly Resilience Pipelines ---
    // "ExchangeSdk" — wraps HyperLiquid.Net and Aster.Net SDK calls
    builder.Services.AddResiliencePipeline("ExchangeSdk", static pipelineBuilder =>
    {
        pipelineBuilder.AddRetry(new RetryStrategyOptions
        {
            ShouldHandle = new PredicateBuilder()
                .Handle<HttpRequestException>()
                .Handle<TimeoutRejectedException>()
                .Handle<TaskCanceledException>(),
            MaxRetryAttempts = 3,
            Delay = TimeSpan.FromSeconds(2),
            BackoffType = DelayBackoffType.Exponential,
            UseJitter = true,
        });

        pipelineBuilder.AddCircuitBreaker(new CircuitBreakerStrategyOptions
        {
            FailureRatio = 0.5,
            SamplingDuration = TimeSpan.FromSeconds(30),
            MinimumThroughput = 5,
            BreakDuration = TimeSpan.FromSeconds(30),
            ShouldHandle = new PredicateBuilder()
                .Handle<HttpRequestException>()
                .Handle<TimeoutRejectedException>(),
        });

        pipelineBuilder.AddTimeout(TimeSpan.FromSeconds(15));
    });

    // "OrderExecution" — critical path; no retry — market orders must never be retried to prevent double fills
    builder.Services.AddResiliencePipeline("OrderExecution", static pipelineBuilder =>
    {
        pipelineBuilder.AddCircuitBreaker(new CircuitBreakerStrategyOptions
        {
            FailureRatio = 0.5,
            SamplingDuration = TimeSpan.FromSeconds(30),
            MinimumThroughput = 3,
            BreakDuration = TimeSpan.FromSeconds(60),
            ShouldHandle = new PredicateBuilder()
                .Handle<HttpRequestException>()
                .Handle<TimeoutRejectedException>(),
        });

        pipelineBuilder.AddTimeout(TimeSpan.FromSeconds(30));
    });

    // "OrderClose" — close operations must not be blocked by the circuit breaker
    builder.Services.AddResiliencePipeline("OrderClose", static pipelineBuilder =>
    {
        pipelineBuilder.AddTimeout(TimeSpan.FromSeconds(30));
    });

    // --- Exchange SDK Clients (CryptoExchange.Net DI pattern) ---
    // Credentials live in User Secrets — the IConfiguration overload reads from
    // "HyperLiquid"/"Aster" root sections which don't exist, so we bind explicitly.
    builder.Services.AddHyperLiquid(options =>
    {
        var addr = builder.Configuration["Exchanges:Hyperliquid:WalletAddress"];
        var key = builder.Configuration["Exchanges:Hyperliquid:PrivateKey"];
        if (!string.IsNullOrEmpty(addr) && addr != "PLACEHOLDER"
            && !string.IsNullOrEmpty(key) && key != "PLACEHOLDER")
        {
            options.ApiCredentials = new ApiCredentials(addr, key);
        }
    });
    builder.Services.AddAster(options =>
    {
        var apiKey = builder.Configuration["Exchanges:Aster:ApiKey"];
        var apiSecret = builder.Configuration["Exchanges:Aster:ApiSecret"];
        if (!string.IsNullOrEmpty(apiKey) && !string.IsNullOrEmpty(apiSecret))
        {
            options.ApiCredentials = new ApiCredentials(apiKey, apiSecret);
        }
    });

    // --- Exchange Connectors ---
    // API credentials come from User Secrets — never appsettings
    // See: dotnet user-secrets set "Exchanges:Hyperliquid:WalletAddress" "0x..."
    builder.Services.AddScoped<HyperliquidConnector>();
    builder.Services.AddHttpClient<LighterConnector>(client =>
    {
        client.BaseAddress = new Uri("https://mainnet.zklighter.elliot.ai/api/v1/");
        client.Timeout = TimeSpan.FromSeconds(30);
    })
    .AddStandardResilienceHandler(options =>
    {
        options.Retry.MaxRetryAttempts = 3;
        options.Retry.Delay = TimeSpan.FromSeconds(1);
        options.Retry.BackoffType = DelayBackoffType.Exponential;
        options.Retry.UseJitter = true;
        // C4: Never retry POST requests — sendTx is a non-idempotent on-chain transaction
        options.Retry.ShouldHandle = args =>
        {
            if (args.Outcome.Result is HttpResponseMessage resp
                && resp.RequestMessage?.Method == HttpMethod.Post)
            {
                return ValueTask.FromResult(false);
            }

            if (args.Outcome.Exception is not null)
            {
                return ValueTask.FromResult(true);
            }

            return ValueTask.FromResult(args.Outcome.Result?.IsSuccessStatusCode == false);
        };
        options.CircuitBreaker.FailureRatio = 0.5;
        options.CircuitBreaker.SamplingDuration = TimeSpan.FromSeconds(30);
        options.CircuitBreaker.MinimumThroughput = 5;
        options.CircuitBreaker.BreakDuration = TimeSpan.FromSeconds(30);
        options.AttemptTimeout.Timeout = TimeSpan.FromSeconds(10);
        options.TotalRequestTimeout.Timeout = TimeSpan.FromSeconds(30);
    });
    builder.Services.AddScoped<AsterConnector>();
    builder.Services.AddHttpClient<CoinGlassConnector>(client =>
    {
        client.BaseAddress = new Uri("https://open-api-v3.coinglass.com/");
        client.Timeout = TimeSpan.FromSeconds(30);
    });
    builder.Services.AddScoped<IExchangeConnectorFactory, ExchangeConnectorFactory>();

    // --- WebSocket Market Data Streaming ---
    builder.Services.AddSingleton<MarketDataCache>();
    builder.Services.AddSingleton<IMarketDataCache>(sp => sp.GetRequiredService<MarketDataCache>());
    builder.Services.AddSingleton<AsterMarketDataStream>();
    builder.Services.AddSingleton<HyperliquidMarketDataStream>();
    builder.Services.AddSingleton<LighterWebSocketClient>();
    builder.Services.AddSingleton<LighterMarketDataStream>();
    builder.Services.AddSingleton<IMarketDataStream>(sp => sp.GetRequiredService<AsterMarketDataStream>());
    builder.Services.AddSingleton<IMarketDataStream>(sp => sp.GetRequiredService<HyperliquidMarketDataStream>());
    builder.Services.AddSingleton<IMarketDataStream>(sp => sp.GetRequiredService<LighterMarketDataStream>());

    // --- SignalR ---
    builder.Services.AddSignalR(options =>
    {
        options.MaximumReceiveMessageSize = 64 * 1024;
    });

    // --- Rate Limiting ---
    builder.Services.AddRateLimiter(options =>
    {
        options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
        options.AddFixedWindowLimiter("auth", opt =>
        {
            opt.Window = TimeSpan.FromMinutes(1);
            opt.PermitLimit = 10;
            opt.QueueLimit = 0;
        });
        options.AddFixedWindowLimiter("signalr", opt =>
        {
            opt.Window = TimeSpan.FromSeconds(10);
            opt.PermitLimit = 20;
            opt.QueueLimit = 5;
        });
        options.AddFixedWindowLimiter("general", opt =>
        {
            opt.Window = TimeSpan.FromMinutes(1);
            opt.PermitLimit = 200;
            opt.QueueLimit = 0;
        });
    });

    // --- Background Services ---
    builder.Services.AddSingleton<IFundingRateReadinessSignal, FundingRateReadinessSignal>();
    builder.Services.AddHostedService<MarketDataStreamManager>();
    builder.Services.AddHostedService<FundingRateFetcher>();
    builder.Services.AddHostedService<BotOrchestrator>();
    builder.Services.AddSingleton<IBotControl>(sp =>
        sp.GetServices<IHostedService>().OfType<BotOrchestrator>().Single());
    builder.Services.AddHostedService<DailySummaryService>();

    // --- MVC ---
    builder.Services.AddControllersWithViews(options =>
        options.Filters.Add(new Microsoft.AspNetCore.Mvc.AutoValidateAntiforgeryTokenAttribute()));

    builder.Services.AddHealthChecks()
        .AddDbContextCheck<AppDbContext>()
        .AddCheck<WebSocketStreamHealthCheck>("websocket-streams");

    var app = builder.Build();

    // Validate connection string is configured (not still using placeholder)
    var connStr = app.Configuration.GetConnectionString("DefaultConnection") ?? "";
    if (connStr.Contains("CHANGE_ME", StringComparison.OrdinalIgnoreCase))
    {
        Log.Fatal("Database password not configured. Set the connection string via User Secrets: " +
                   "dotnet user-secrets set \"ConnectionStrings:DefaultConnection\" \"Server=...; Password=YOUR_PASSWORD\"");
        throw new InvalidOperationException(
            "Database password not configured. Update the connection string via User Secrets.");
    }

    // --- Apply Pending Migrations ---
    if (app.Environment.IsDevelopment())
    {
        using var scope = app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        if (db.Database.IsRelational())
        {
            await db.Database.MigrateAsync();
        }
        else
        {
            await db.Database.EnsureCreatedAsync();
        }
        await DbSeeder.SeedAsync(scope.ServiceProvider);
    }
    else
    {
        Log.Information("Skipping automatic migrations in {Environment} environment", app.Environment.EnvironmentName);
    }

    // --- Middleware Pipeline ---
    if (!app.Environment.IsDevelopment())
    {
        app.UseExceptionHandler("/Home/Error");
        app.UseHsts();
        app.UseHttpsRedirection();
    }

    // --- Security response headers ---
    app.Use(async (ctx, next) =>
    {
        ctx.Response.Headers["X-Frame-Options"] = "DENY";
        ctx.Response.Headers["X-Content-Type-Options"] = "nosniff";
        ctx.Response.Headers["Referrer-Policy"] = "strict-origin-when-cross-origin";
        ctx.Response.Headers["Permissions-Policy"] = "geolocation=(), microphone=(), camera=()";
        // F5: CSP URLs are pinned to CDN script versions in Views/Analytics/RateAnalytics.cshtml.
        // When bumping chart.js or adapter versions, update BOTH the CSP URLs here AND the script tags in the view.
        // SRI integrity hashes in the HTML provide an additional verification layer.
        ctx.Response.Headers["Content-Security-Policy"] =
            "default-src 'self'; " +
            "script-src 'self' https://cdn.jsdelivr.net/npm/chart.js@4.5.1/dist/chart.umd.min.js https://cdn.jsdelivr.net/npm/chartjs-adapter-date-fns@3.0.0/dist/chartjs-adapter-date-fns.bundle.min.js; " +
            "style-src 'self' 'unsafe-inline'; " +
            "connect-src 'self' wss: ws:; " +
            "img-src 'self' data:; " +
            "frame-ancestors 'none';";
        await next();
    });

    app.UseSerilogRequestLogging();  // Structured HTTP request logs
    app.UseStaticFiles();
    app.UseRouting();
    app.UseAuthentication();
    app.UseAuthorization();
    app.UseRateLimiter();
    app.MapHealthChecks("/healthz", new Microsoft.AspNetCore.Diagnostics.HealthChecks.HealthCheckOptions
    {
        ResponseWriter = async (context, report) =>
        {
            context.Response.ContentType = "text/plain";
            await context.Response.WriteAsync(report.Status.ToString());
        }
    });

    app.MapHealthChecks("/health", new Microsoft.AspNetCore.Diagnostics.HealthChecks.HealthCheckOptions
    {
        ResponseWriter = async (context, report) =>
        {
            var result = new
            {
                status = report.Status.ToString(),
                totalDuration = report.TotalDuration.TotalMilliseconds,
                entries = report.Entries.Select(e => new
                {
                    name = e.Key,
                    status = e.Value.Status.ToString(),
                    description = e.Value.Description,
                    duration = e.Value.Duration.TotalMilliseconds
                })
            };
            await context.Response.WriteAsJsonAsync(result);
        }
    })
        .RequireAuthorization(policy => policy.RequireRole("Admin"))
        .RequireRateLimiting("general");

    app.MapControllerRoute("areas", "{area:exists}/{controller=Home}/{action=Index}/{id?}")
        .RequireRateLimiting("general");
    app.MapControllerRoute("default", "{controller=Dashboard}/{action=Index}/{id?}")
        .RequireRateLimiting("general");
    app.MapRazorPages()
        .RequireRateLimiting("auth");
    app.MapHub<DashboardHub>("/hubs/dashboard")
        .RequireRateLimiting("signalr");

    app.Run();
}
catch (Exception ex)
{
    Log.Fatal(ex, "Application terminated unexpectedly");
}
finally
{
    await Log.CloseAndFlushAsync();
}

public partial class Program { }
