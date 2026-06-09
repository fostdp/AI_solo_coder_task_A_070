using AluminaDetection.Api.Config;
using AluminaDetection.Api.Data;
using AluminaDetection.Api.Hubs;
using AluminaDetection.Api.Services;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddDbContext<AppDbContext>(options =>
    options.UseSqlServer(builder.Configuration.GetConnectionString("DefaultConnection")));

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

await InitializeDatabaseAsync(app);

app.UseCors("DevCors");
app.UseRouting();
app.MapControllers();
app.MapHub<PotMonitorHub>("/hubs/pot-monitor");

app.Run();

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
