using SimpleTradeBot.Core.Configuration;
using SimpleTradeBot.Core.Domain;

namespace SimpleTradeBot.Tests;

/// <summary>Общие заготовки для тестов: инструмент и настройки сетки.</summary>
internal static class TestFactory
{
    /// <summary>Инструмент вроде BTCUSDT: шаг цены 0.1, шаг объёма 0.001.</summary>
    public static InstrumentSpec Instrument(
        decimal tickSize = 0.1m,
        decimal quantityStep = 0.001m,
        decimal minQuantity = 0.001m)
        => new("BTCUSDT", tickSize, quantityStep, minQuantity, 100m, "BTC", "USDT");

    public static GridOptions Grid(Action<GridOptions>? tune = null)
    {
        var options = new GridOptions
        {
            Direction = GridDirection.Long,
            EntryVolume = 0m,
            LevelVolume = 0.001m,
            Levels = 5,
            StepPercent = 1m,
            VolumeFactor = 1m,
            PriceFactor = 1m,
            MaxOpenOrders = 0,
            MaxPositionVolume = 0m
        };

        tune?.Invoke(options);
        return options;
    }

    public static BotOptions Bot(Action<GridOptions>? tune = null)
        => new()
        {
            Symbol = "BTCUSDT",
            AutoStart = false,
            RequireFlatOnStart = true,
            CancelOrphanOrdersOnStart = false,
            Grid = Grid(tune)
        };
}
