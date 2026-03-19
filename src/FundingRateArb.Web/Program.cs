using FundingRateArb.Application.Common.Exchanges;
using FundingRateArb.Application.Common.Repositories;
using FundingRateArb.Application.Services;
using FundingRateArb.Domain.Entities;
using FundingRateArb.Infrastructure.BackgroundServices;
using FundingRateArb.Infrastructure.Services;
using FundingRateArb.Infrastructure.Data;
using FundingRateArb.Infrastructure.ExchangeConnectors;
using FundingRateArb.Infrastructure.Hubs;
using FundingRateArb.Infrastructure.Repositories;
using FundingRateArb.Infrastructure.Seed;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Http.Resilience;
using Polly;
using Polly.CircuitBreaker;
using Polly.Retry;
using Polly.Timeout;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.RateLimiting;
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

    // --- Serilog (replaces default ILogger) ---
    var isDevelopment = builder.Environment.IsDevelopment();
    builder.Services.AddSerilog((_, lc) =>
    {
        lc  .ReadFrom.Configuration(builder.Configuration)
            .MinimumLevel.Information()
            .MinimumLevel.Override("Microsoft", LogEventLevel.Warning)
            .MinimumLevel.Override("Microsoft.EntityFrameworkCore", LogEventLevel.Warning)
            .Enrich.FromLogContext()
            .Enrich.WithMachineName()
            .Enrich.WithThreadId()
            .Enrich.WithProperty("Application", "FundingRateArb")

            // Console sink
            .WriteTo.Console(outputTemplate:
                "[{Timestamp:HH:mm:ss} {Level:u3}] {SourceContext}{NewLine}" +
                "  {Message:lj}{NewLine}{Exception}")

            // Rolling daily file sink (30-day retention)
            .WriteTo.File(
                path: "logs/fundingratearb-.log",
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 30,
                outputTemplate:
                    "{Timestamp:yyyy-MM-dd HH:mm:ss.fff} [{Level:u3}] " +
                    "{SourceContext} | {Message:lj} | {Properties:j}{NewLine}{Exception}");

        // SQL Server audit sink — only when a real SQL Server is available (not LocalDB on Linux)
        if (!isDevelopment)
        {
            lc.WriteTo.MSSqlServer(
                connectionString: builder.Configuration.GetConnectionString("DefaultConnection"),
                sinkOptions: new MSSqlServerSinkOptions
                {
                    TableName          = "AuditLogs",
                    AutoCreateSqlTable = true,
                    BatchPostingLimit  = 50,
                    BatchPeriod        = TimeSpan.FromSeconds(5),
                },
                restrictedToMinimumLevel: LogEventLevel.Warning);
        }
    });

    // --- Database ---
    builder.Services.AddDbContext<AppDbContext>(options =>
        options.UseSqlServer(builder.Configuration.GetConnectionString("DefaultConnection")));

    // --- Identity ---
    builder.Services.AddDefaultIdentity<ApplicationUser>(options =>
    {
        options.Password.RequireDigit           = true;
        options.Password.RequiredLength         = 12;
        options.Password.RequireLowercase       = true;
        options.Password.RequireUppercase       = true;
        options.Password.RequireNonAlphanumeric = true;
        options.SignIn.RequireConfirmedAccount  = false;
        options.Lockout.DefaultLockoutTimeSpan  = TimeSpan.FromMinutes(15);
        options.Lockout.MaxFailedAccessAttempts = 5;
        options.Lockout.AllowedForNewUsers      = true;
    })
    .AddRoles<IdentityRole>()
    .AddEntityFrameworkStores<AppDbContext>();

    // --- Cookie security ---
    builder.Services.ConfigureApplicationCookie(options =>
    {
        options.Cookie.SecurePolicy = Microsoft.AspNetCore.Http.CookieSecurePolicy.Always;
        options.Cookie.SameSite     = Microsoft.AspNetCore.Http.SameSiteMode.Strict;
        options.Cookie.HttpOnly     = true;
        options.ExpireTimeSpan      = TimeSpan.FromHours(8);
        options.SlidingExpiration   = true;
    });

    // --- Data Protection (for IApiKeyVault) ---
    builder.Services.AddDataProtection()
        .PersistKeysToFileSystem(new System.IO.DirectoryInfo(
            Path.Combine(builder.Environment.ContentRootPath, "DataProtection-Keys")))
        .SetApplicationName("FundingRateArb");
    builder.Services.AddScoped<IApiKeyVault, ApiKeyVault>();

    // --- Unit of Work (cursus BankingApp pattern) ---
    builder.Services.AddScoped<IUnitOfWork, UnitOfWork>();

    // --- Services ---
    builder.Services.AddScoped<ISignalEngine, SignalEngine>();
    builder.Services.AddScoped<IPositionSizer, PositionSizer>();
    builder.Services.AddScoped<IExecutionEngine, ExecutionEngine>();
    builder.Services.AddScoped<IPositionHealthMonitor, PositionHealthMonitor>();
    builder.Services.AddSingleton<IYieldCalculator, YieldCalculator>();

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
            Delay            = TimeSpan.FromSeconds(2),
            BackoffType      = DelayBackoffType.Exponential,
            UseJitter        = true,
        });

        pipelineBuilder.AddCircuitBreaker(new CircuitBreakerStrategyOptions
        {
            FailureRatio      = 0.5,
            SamplingDuration  = TimeSpan.FromSeconds(30),
            MinimumThroughput = 5,
            BreakDuration     = TimeSpan.FromSeconds(30),
            ShouldHandle = new PredicateBuilder()
                .Handle<HttpRequestException>()
                .Handle<TimeoutRejectedException>(),
        });

        pipelineBuilder.AddTimeout(TimeSpan.FromSeconds(15));
    });

    // "OrderExecution" — critical path; single retry only (market orders must not double-fill)
    builder.Services.AddResiliencePipeline("OrderExecution", static pipelineBuilder =>
    {
        pipelineBuilder.AddRetry(new RetryStrategyOptions
        {
            ShouldHandle = new PredicateBuilder()
                .Handle<HttpRequestException>()
                .Handle<TimeoutRejectedException>()
                .Handle<TaskCanceledException>(),
            MaxRetryAttempts = 1,
            Delay            = TimeSpan.FromSeconds(1),
            BackoffType      = DelayBackoffType.Exponential,
            UseJitter        = true,
        });

        pipelineBuilder.AddTimeout(TimeSpan.FromSeconds(30));
    });

    // --- Exchange SDK Clients (CryptoExchange.Net DI pattern) ---
    builder.Services.AddHyperLiquid(builder.Configuration);
    builder.Services.AddAster(builder.Configuration);

    // --- Exchange Connectors ---
    // API credentials come from User Secrets — never appsettings
    // See: dotnet user-secrets set "Exchanges:Hyperliquid:WalletAddress" "0x..."
    builder.Services.AddScoped<HyperliquidConnector>();
    builder.Services.AddHttpClient<LighterConnector>(client =>
    {
        client.BaseAddress = new Uri("https://mainnet.zklighter.elliot.ai/api/v1/");
        client.Timeout     = TimeSpan.FromSeconds(30);
    })
    .AddStandardResilienceHandler(options =>
    {
        options.Retry.MaxRetryAttempts               = 3;
        options.Retry.Delay                          = TimeSpan.FromSeconds(1);
        options.Retry.BackoffType                    = DelayBackoffType.Exponential;
        options.Retry.UseJitter                      = true;
        options.CircuitBreaker.FailureRatio          = 0.5;
        options.CircuitBreaker.SamplingDuration      = TimeSpan.FromSeconds(30);
        options.CircuitBreaker.MinimumThroughput     = 5;
        options.CircuitBreaker.BreakDuration         = TimeSpan.FromSeconds(30);
        options.AttemptTimeout.Timeout               = TimeSpan.FromSeconds(10);
        options.TotalRequestTimeout.Timeout          = TimeSpan.FromSeconds(30);
    });
    builder.Services.AddScoped<AsterConnector>();
    builder.Services.AddScoped<IExchangeConnectorFactory, ExchangeConnectorFactory>();

    // --- SignalR ---
    builder.Services.AddSignalR();

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
    });

    // --- Background Services ---
    builder.Services.AddHostedService<FundingRateFetcher>();
    builder.Services.AddHostedService<BotOrchestrator>();

    // --- MVC ---
    builder.Services.AddControllersWithViews();

    var app = builder.Build();

    // --- Seed Database ---
    using (var scope = app.Services.CreateScope())
        await DbSeeder.SeedAsync(scope.ServiceProvider);

    // --- Middleware Pipeline ---
    if (!app.Environment.IsDevelopment())
    {
        app.UseExceptionHandler("/Home/Error");
        app.UseHsts();
    }

    // --- Security response headers ---
    app.Use(async (ctx, next) =>
    {
        ctx.Response.Headers["X-Frame-Options"]         = "DENY";
        ctx.Response.Headers["X-Content-Type-Options"]  = "nosniff";
        ctx.Response.Headers["Referrer-Policy"]         = "strict-origin-when-cross-origin";
        ctx.Response.Headers["Permissions-Policy"]      = "geolocation=(), microphone=(), camera=()";
        ctx.Response.Headers["Content-Security-Policy"] =
            "default-src 'self'; " +
            "script-src 'self' https://cdnjs.cloudflare.com; " +
            "style-src 'self' 'unsafe-inline'; " +
            "connect-src 'self' wss: ws:; " +
            "img-src 'self' data:; " +
            "frame-ancestors 'none';";
        await next();
    });

    app.UseSerilogRequestLogging();  // Structured HTTP request logs
    app.UseHttpsRedirection();
    app.UseStaticFiles();
    app.UseRouting();
    app.UseAuthentication();
    app.UseAuthorization();
    app.UseRateLimiter();

    app.MapControllerRoute("areas", "{area:exists}/{controller=Home}/{action=Index}/{id?}");
    app.MapControllerRoute("default", "{controller=Dashboard}/{action=Index}/{id?}");
    app.MapRazorPages()
        .RequireRateLimiting("auth");
    app.MapHub<DashboardHub>("/hubs/dashboard");

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
