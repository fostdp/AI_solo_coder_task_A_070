using AluminaDetection.Api.Config;
using AluminaDetection.Api.Data;
using AluminaDetection.Api.Hubs;
using AluminaDetection.Api.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Net.Http.Headers;
using Serilog;
using Serilog.Events;

Log.Logger = new LoggerConfiguration()
    .MinimumLevel.Information()
    .MinimumLevel.Override("Microsoft.AspNetCore", LogEventLevel.Warning)
    .MinimumLevel.Override("Microsoft.EntityFrameworkCore", LogEventLevel.Warning)
    .Enrich.FromLogContext()
    .Enrich.WithMachineName()
    .Enrich.WithThreadId()
    .WriteTo.Console(outputTemplate: "[{Timestamp:HH:mm:ss} {Level:u3}] [{SourceContext}] {Message:lj}{NewLine}{Exception}")
    .WriteTo.File(
        path: "/app/logs/alumina-.log",
        rollingInterval: RollingInterval.Day,
        retainedFileCountLimit: 30,
        outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] [{SourceContext}] [ThreadId:{ThreadId}] {Message:lj}{NewLine}{Exception}")
    .CreateBootstrapLogger();

try
{
    Log.Information("正在启动电解铝氧化铝浓度在线检测与槽控优化系统...");
    RunApplication(args);
}
catch (Exception ex)
{
    Log.Fatal(ex, "应用程序启动失败");
}
finally
{
    Log.CloseAndFlush();
}

void RunApplication(string[] args)
{
    var builder = WebApplication.CreateBuilder(args);

    builder.Host.UseSerilog((context, services, loggerConfig) =>
    {
        loggerConfig
            .ReadFrom.Configuration(context.Configuration)
            .ReadFrom.Services(services)
            .Enrich.FromLogContext()
            .Enrich.WithMachineName()
            .Enrich.WithThreadId();

        var connStr = context.Configuration.GetConnectionString("DefaultConnection");
        if (!string.IsNullOrEmpty(connStr))
        {
            loggerConfig.WriteTo.MSSqlServer(
                connectionString: connStr,
                sinkOptions: new Serilog.Sinks.MSSqlServer.MSSqlServerSinkOptions
                {
                    TableName = "ApplicationLogs",
                    AutoCreateSqlTable = true,
                    BatchPostingLimit = 50,
                    BatchPeriod = TimeSpan.FromSeconds(5)
                },
                columnOptions: new Serilog.Sinks.MSSqlServer.ColumnOptions
                {
                    TimeStamp = new Serilog.Sinks.MSSqlServer.ColumnOptions.TimeStampColumnOptions { ConvertToUtc = true }
                });
        }
    });

    builder.Services.AddApplicationInsightsTelemetry(options =>
    {
        options.ConnectionString = builder.Configuration["APPLICATIONINSIGHTS_CONNECTION_STRING"];
        options.EnableAdaptiveSampling = true;
        options.EnableQuickPulseMetricStream = true;
        options.RequestCollectionOptions.TrackExceptions = true;
    });

    builder.Services.AddDbContext<AppDbContext>(options =>
        options.UseSqlServer(builder.Configuration.GetConnectionString("DefaultConnection")));

    builder.Services.AddResponseCompression(options =>
    {
        options.EnableForHttps = true;
        options.Providers.Add<BrotliCompressionProvider>();
        options.Providers.Add<GzipCompressionProvider>();
        options.MimeTypes = ResponseCompressionDefaults.MimeTypes.Concat(new[]
        {
            "application/json",
            "text/html",
            "text/css",
            "application/javascript",
            "text/javascript",
            "application/geo+json",
            "image/svg+xml",
        });
    });

    builder.Services.Configure<BrotliCompressionProviderOptions>(options => options.Level = System.IO.Compression.CompressionLevel.Optimal);
    builder.Services.Configure<GzipCompressionProviderOptions>(options => options.Level = System.IO.Compression.CompressionLevel.Optimal);

    builder.Services.AddControllers();
    builder.Services.AddSignalR();
    builder.Services.AddEndpointsApiExplorer();
    builder.Services.AddSwaggerGen();

    builder.Services.AddCors(options =>
    {
        options.AddPolicy("DevCors", policy =>
        {
            policy.AllowAnyOrigin().AllowAnyMethod().AllowAnyHeader();
        });
    });

    builder.Services.AddMediatR(cfg => cfg.RegisterServicesFromAssembly(typeof(Program).Assembly));

    builder.Services.AddSingleton<SvrConfig>(sp =>
    {
        var config = new SvrConfig();
        builder.Configuration.GetSection("Svr").Bind(config);
        return config;
    });
    builder.Services.AddSingleton<RfConfig>(sp =>
    {
        var config = new RfConfig();
        builder.Configuration.GetSection("Rf").Bind(config);
        return config;
    });
    builder.Services.AddSingleton<AlarmConfig>(sp =>
    {
        var config = new AlarmConfig();
        builder.Configuration.GetSection("Alarm").Bind(config);
        return config;
    });

    builder.Services.AddSingleton<IZigBeeReceiver, ZigBeeReceiver>();
    builder.Services.AddSingleton<IVoltageFeatureExtractor, VoltageFeatureExtractor>();
    builder.Services.AddSingleton<IConcentrationEstimator, ConcentrationEstimator>();
    builder.Services.AddSingleton<IAnodeEffectPredictorService, AnodeEffectPredictorService>();
    builder.Services.AddSingleton<IAlarmController, AlarmControllerService>();
    builder.Services.AddSingleton<IMqttPublishService, MqttPublishService>();

    builder.Services.AddSingleton<PotDataProcessingHostedService>();
    builder.Services.AddHostedService(provider => provider.GetRequiredService<PotDataProcessingHostedService>());
    builder.Services.AddHostedService<ModelTrainingHostedService>();

    var app = builder.Build();

    if (app.Environment.IsDevelopment())
    {
        app.UseSwagger();
        app.UseSwaggerUI();
    }

    app.UseResponseCompression();

    app.UseSerilogRequestLogging(options =>
    {
        options.MessageTemplate = "HTTP {RequestMethod} {RequestPath} responded {StatusCode} in {Elapsed:0.000}ms";
        options.EnrichDiagnosticContext = (diagCtx, httpContext) =>
        {
            diagCtx.Set("RequestHost", httpContext.Request.Host.Value);
            diagCtx.Set("RequestId", httpContext.TraceIdentifier);
        };
    });

    await InitializeDatabaseAsync(app);

    app.UseCors("DevCors");
    app.UseRouting();
    app.MapControllers();
    app.MapHub<PotMonitorHub>("/hubs/pot-monitor");

    app.Use(async (context, next) =>
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        await next();
        sw.Stop();
        if (sw.ElapsedMilliseconds > 2000)
        {
            Log.Warning("慢请求: {Method} {Path} {StatusCode} 耗时{Elapsed}ms",
                context.Request.Method, context.Request.Path, context.Response.StatusCode, sw.ElapsedMilliseconds);
        }
    });

    Log.Information("应用程序启动完成，监听端口5000");
    app.Run();
}

static async Task InitializeDatabaseAsync(WebApplication app)
{
    using var scope = app.Services.CreateScope();
    var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    var logger = scope.ServiceProvider.GetRequiredService<ILogger<Program>>();

    try
    {
        await dbContext.Database.EnsureCreatedAsync();

        var hasPots = await dbContext.PotInfos.AnyAsync();
        if (!hasPots)
        {
            logger.LogInformation("No pots found. Initializing 200 pots...");
            for (int i = 1; i <= 200; i++)
            {
                dbContext.PotInfos.Add(new Models.PotInfo
                {
                    PotCode = $"P-{i:D3}",
                    RowIndex = ((i - 1) / 20) + 1,
                    ColIndex = ((i - 1) % 20) + 1,
                    Status = 1
                });
            }
            await dbContext.SaveChangesAsync();
            logger.LogInformation("200 pots initialized.");
        }
    }
    catch (Exception ex)
    {
        logger.LogError(ex, "Database initialization failed.");
    }
}
