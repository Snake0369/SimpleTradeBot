using System.Text.Json;
using System.Text.Json.Serialization;
using SimpleTradeBot.Bybit;
using SimpleTradeBot.Core.Configuration;
using SimpleTradeBot.Core.Execution;
using SimpleTradeBot.Core.MarketData;
using SimpleTradeBot.Core.State;
using SimpleTradeBot.Core.Strategy;
using SimpleTradeBot.Web;

var builder = WebApplication.CreateBuilder(args);

// Настройки целиком живут в appsettings.json. Ключи оттуда лучше не брать: переменные окружения
// (Bybit__ApiKey, Bybit__ApiSecret) и dotnet user-secrets перекрывают файл, и секрет не попадает
// в репозиторий.
var bybitOptions = builder.Configuration.GetSection(BybitOptions.SectionName).Get<BybitOptions>()
                   ?? new BybitOptions();
var botOptions = builder.Configuration.GetSection(BotOptions.SectionName).Get<BotOptions>()
                 ?? new BotOptions();

builder.Services.AddSingleton(bybitOptions);
builder.Services.AddSingleton(botOptions);
builder.Services.AddSingleton(new BotEventLog(botOptions.EventLogSize));

builder.Services.AddSingleton<IInstrumentProvider, BybitInstrumentProvider>();
builder.Services.AddSingleton<IMarketDataFeed, BybitMarketDataFeed>();
builder.Services.AddSingleton<IExecutionConnector, BybitExecutionConnector>();
builder.Services.AddSingleton<GridBotEngine>();
builder.Services.AddHostedService<BotLifecycleService>();

builder.Services.ConfigureHttpJsonOptions(o =>
{
    o.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
    o.SerializerOptions.Converters.Add(new JsonStringEnumConverter());
    o.SerializerOptions.DefaultIgnoreCondition = JsonIgnoreCondition.Never;
});

var app = builder.Build();

app.UseDefaultFiles();
app.UseStaticFiles();

var api = app.MapGroup("/api");

api.MapGet("/state", (GridBotEngine engine) => Results.Ok(engine.GetSnapshot()));

api.MapPost("/start", async (GridBotEngine engine, CancellationToken ct) =>
{
    var error = await engine.StartAsync(ct);
    return error == null
        ? Results.Ok(new { ok = true })
        : Results.BadRequest(new { ok = false, error });
});

api.MapPost("/stop", async (GridBotEngine engine, CancellationToken ct) =>
{
    var error = await engine.StopAsync(ct);
    return error == null
        ? Results.Ok(new { ok = true })
        : Results.BadRequest(new { ok = false, error });
});

api.MapPost("/close", (GridBotEngine engine) =>
{
    var error = engine.ClosePositionAndRestartCycle();
    return error == null
        ? Results.Ok(new { ok = true })
        : Results.BadRequest(new { ok = false, error });
});

// Действующие настройки — чтобы в интерфейсе было видно, с чем бот реально работает. Ключи
// отдаются только как признак «заданы или нет»: показывать их незачем, а утечь они могут.
api.MapGet("/settings", (BotOptions bot, BybitOptions bybit) => Results.Ok(new
{
    bot.Symbol,
    bot.AutoStart,
    bot.RequireFlatOnStart,
    bot.CancelOrphanOrdersOnStart,
    Grid = bot.Grid,
    Bybit = new
    {
        Environment = BybitClientFactory.EnvironmentLabel(bybit),
        bybit.Leverage,
        bybit.TickPoolMs,
        CredentialsConfigured = bybit.HasCredentials
    }
}));

app.MapGet("/health", (GridBotEngine engine) => Results.Ok(new
{
    status = "ok",
    bot = engine.Status.ToString()
}));

app.Run();
