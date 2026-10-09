using System.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using SimpleTradeBot.Core.Configuration;
using SimpleTradeBot.Core.Domain;
using SimpleTradeBot.Core.Execution;
using SimpleTradeBot.Core.State;
using SimpleTradeBot.Core.Strategy;
using Xunit;

namespace SimpleTradeBot.Tests;

/// <summary>
/// Тесты торгового цикла целиком: фид и биржа подставные, вся остальная логика настоящая.
/// </summary>
public class GridBotEngineTests
{
    [Fact]
    public async Task Старт_строит_сетку_и_ставит_ближайшие_заявки()
    {
        await using var h = await Harness.StartAsync(o =>
        {
            o.Levels = 5;
            o.StepPercent = 1m;
            o.MaxOpenOrders = 2;
        });

        h.Feed.Push(1_000m);

        await h.WaitFor(() => h.Exchange.Live.Count == 2, "две заявки добора");

        var orders = h.Exchange.Live.OrderByDescending(o => o.Price).ToList();

        Assert.All(orders, o => Assert.Equal(Side.Buy, o.Side));
        Assert.All(orders, o => Assert.Equal(OrderKind.Limit, o.Kind));
        Assert.Equal([990m, 980m], orders.Select(o => o.Price));

        var snapshot = h.Engine.GetSnapshot();
        Assert.Equal(BotStatus.Running, snapshot.Status);
        Assert.Equal(1, snapshot.Cycle);
        Assert.Equal(1_000m, snapshot.AnchorPrice);
        Assert.Equal(10m, snapshot.Step);
        Assert.Equal(5, snapshot.Levels.Count);
    }

    [Fact]
    public async Task Рыночный_вход_открывает_позицию()
    {
        await using var h = await Harness.StartAsync(o =>
        {
            o.EntryVolume = 0.002m;
            o.MaxOpenOrders = 1;
        });

        h.Feed.Push(1_000m);

        await h.WaitFor(() => h.Exchange.Live.Any(o => o.Kind == OrderKind.Market), "рыночный вход");

        var entry = h.Exchange.Single(o => o.Kind == OrderKind.Market)!;
        Assert.Equal(0.002m, entry.Quantity);
        Assert.False(entry.ReduceOnly);

        h.Exchange.Fill(entry.ClientOrderId, 1_000m);

        await h.WaitFor(() => h.Engine.GetSnapshot().Position.Volume == 0.002m, "позиция по рыночному входу");

        var position = h.Engine.GetSnapshot().Position;
        Assert.Equal(Side.Buy, position.Side);
        Assert.Equal(1_000m, position.AveragePrice);
    }

    [Fact]
    public async Task Залитый_уровень_получает_тейк_профит_на_шаг_выше()
    {
        await using var h = await Harness.StartAsync(o =>
        {
            o.MaxOpenOrders = 1;
            o.AlignTakeProfitToGrid = true;
        });

        h.Feed.Push(1_000m);
        await h.WaitFor(() => h.Exchange.Live.Count == 1, "заявка добора");

        var level = h.Exchange.Live.Single();
        h.Exchange.Fill(level.ClientOrderId);

        await h.WaitFor(() => h.Exchange.Live.Any(o => o.ReduceOnly), "тейк-профит уровня");

        var take = h.Exchange.Single(o => o.ReduceOnly)!;
        Assert.Equal(Side.Sell, take.Side);
        Assert.Equal(OrderKind.Limit, take.Kind);
        Assert.Equal(1_000m, take.Price);
        Assert.Equal(level.Quantity, take.Quantity);

        var levelView = h.Engine.GetSnapshot().Levels.Single(l => l.Index == 1);
        Assert.Equal(LevelStatus.TakeProfit, levelView.Status);
        Assert.Equal(1_000m, levelView.TakeProfitPrice);
    }

    [Fact]
    public async Task Выкупленный_тейк_возвращает_уровень_в_строй()
    {
        await using var h = await Harness.StartAsync(o =>
        {
            o.MaxOpenOrders = 1;
            o.AlignTakeProfitToGrid = true;
        });

        h.Feed.Push(1_000m);
        await h.WaitFor(() => h.Exchange.Live.Count == 1, "заявка добора");

        h.Exchange.Fill(h.Exchange.Live.Single().ClientOrderId);
        await h.WaitFor(() => h.Exchange.Live.Any(o => o.ReduceOnly), "тейк-профит уровня");

        h.Exchange.Fill(h.Exchange.Single(o => o.ReduceOnly)!.ClientOrderId);

        await h.WaitFor(() => h.Engine.GetSnapshot().Stats.LevelCycles == 1, "оборот уровня");

        var snapshot = h.Engine.GetSnapshot();
        var level = snapshot.Levels.Single(l => l.Index == 1);

        Assert.Equal(LevelStatus.Idle, level.Status);
        Assert.Equal(1, level.Cycles);
        Assert.Equal(0m, level.FilledVolume);
        // Уровень 990 выкуплен по 1000 объёмом 0.001 — это ровно 0.01 в котируемой валюте.
        Assert.Equal(0.01m, level.RealizedPnl);
        Assert.True(snapshot.Position.IsFlatInView());
    }

    [Fact]
    public async Task Уровень_ставится_заново_когда_цена_возвращается()
    {
        await using var h = await Harness.StartAsync(o =>
        {
            o.MaxOpenOrders = 1;
            o.AlignTakeProfitToGrid = true;
        });

        h.Feed.Push(1_000m);
        await h.WaitFor(() => h.Exchange.Live.Count == 1, "заявка добора");

        h.Exchange.Fill(h.Exchange.Live.Single().ClientOrderId);
        await h.WaitFor(() => h.Exchange.Live.Any(o => o.ReduceOnly), "тейк-профит уровня");
        h.Exchange.Fill(h.Exchange.Single(o => o.ReduceOnly)!.ClientOrderId);
        await h.WaitFor(() => h.Engine.GetSnapshot().Stats.LevelCycles == 1, "оборот уровня");

        h.Feed.Push(1_000m);

        await h.WaitFor(
            () => h.Exchange.Live.Any(o => o is { ReduceOnly: false, Price: 990m }),
            "уровень взведён заново");
    }

    [Fact]
    public async Task MaxPositionVolume_не_даёт_набрать_больше_лимита()
    {
        await using var h = await Harness.StartAsync(o =>
        {
            o.Levels = 5;
            o.LevelVolume = 0.001m;
            o.MaxOpenOrders = 0;
            o.MaxPositionVolume = 0.003m;
        });

        h.Feed.Push(1_000m);

        await h.WaitFor(() => h.Exchange.Live.Count == 3, "три заявки по лимиту объёма");
        await Task.Delay(150);

        Assert.Equal(3, h.Exchange.Live.Count);
        Assert.Equal(0.003m, h.Engine.GetSnapshot().Risk.CommittedVolume);
    }

    [Fact]
    public async Task Стоп_лосс_закрывает_позицию_рыночной_заявкой()
    {
        await using var h = await Harness.StartAsync(o =>
        {
            o.MaxOpenOrders = 1;
            o.StopLossPercent = 2m;
            o.RestartAfterStopLoss = false;
        });

        h.Feed.Push(1_000m);
        await h.WaitFor(() => h.Exchange.Live.Count == 1, "заявка добора");

        h.Exchange.Fill(h.Exchange.Live.Single().ClientOrderId);
        await h.WaitFor(() => h.Engine.GetSnapshot().Position.Volume > 0m, "позиция");

        // Средняя 990, стоп 2% ниже — 970.2.
        await h.WaitFor(() => h.Engine.GetSnapshot().Risk.StopLossPrice == 970.2m, "уровень стопа");

        h.Feed.Push(971m, 969m, 972m);

        await h.WaitFor(
            () => h.Exchange.Live.Any(o => o is { Kind: OrderKind.Market, ReduceOnly: true }),
            "рыночное закрытие");

        var flatten = h.Exchange.Single(o => o.Kind == OrderKind.Market)!;
        Assert.Equal(Side.Sell, flatten.Side);
        Assert.Equal(0.001m, flatten.Quantity);
        Assert.Equal(1, h.Engine.GetSnapshot().Stats.StopLossCycles);
    }

    [Fact]
    public async Task Общий_тейк_профит_закрывает_позицию_и_перезапускает_цикл()
    {
        await using var h = await Harness.StartAsync(o =>
        {
            o.MaxOpenOrders = 1;
            o.TakeProfitPercent = 1m;
            o.RestartAfterTakeProfit = true;
        });

        h.Feed.Push(1_000m);
        await h.WaitFor(() => h.Exchange.Live.Count == 1, "заявка добора");

        h.Exchange.Fill(h.Exchange.Live.Single().ClientOrderId);
        await h.WaitFor(() => h.Engine.GetSnapshot().Risk.TakeProfitPrice == 999.9m, "уровень общего тейка");

        h.Feed.Push(1_000m, 999m, 1_001m);

        await h.WaitFor(
            () => h.Exchange.Live.Any(o => o.Kind == OrderKind.Market),
            "рыночное закрытие по общему тейку");

        h.Exchange.Fill(h.Exchange.Single(o => o.Kind == OrderKind.Market)!.ClientOrderId, 1_000m);

        await h.WaitFor(() => h.Engine.GetSnapshot().Levels.Count == 0, "цикл закрыт");
        Assert.Equal(1, h.Engine.GetSnapshot().Stats.TakeProfitCycles);

        h.Feed.Push(1_200m);

        await h.WaitFor(() => h.Engine.GetSnapshot().Cycle == 2, "новый цикл");
        Assert.Equal(1_200m, h.Engine.GetSnapshot().AnchorPrice);
        Assert.Equal(BotStatus.Running, h.Engine.Status);
    }

    [Fact]
    public async Task Закрытие_отменяется_если_на_бирже_позиции_нет()
    {
        await using var h = await Harness.StartAsync(o =>
        {
            o.MaxOpenOrders = 1;
            o.StopLossPercent = 2m;
        });

        h.Feed.Push(1_000m);
        await h.WaitFor(() => h.Exchange.Live.Count == 1, "заявка добора");

        h.Exchange.Fill(h.Exchange.Live.Single().ClientOrderId);
        await h.WaitFor(() => h.Engine.GetSnapshot().Risk.StopLossPrice == 970.2m, "уровень стопа");

        // Позицию закрыли руками: в учёте бота она есть, на бирже её нет. Закрывающая заявка
        // в этом случае не закрыла бы ничего, а открыла бы встречную позицию.
        h.Exchange.NetPosition = 0m;

        h.Feed.Push(971m, 969m, 972m);

        await h.WaitFor(
            () => h.Events.Recent(50).Any(e => e.Kind == "Flatten" && e.Message.Contains("закрывать нечего")),
            "сверка остановила закрытие");

        Assert.DoesNotContain(h.Exchange.History, o => o.Kind == OrderKind.Market);
        Assert.Equal(0m, h.Engine.GetSnapshot().Position.Volume);
    }

    [Fact]
    public async Task Остановка_снимает_заявки_и_закрывает_позицию()
    {
        await using var h = await Harness.StartAsync(o =>
        {
            o.MaxOpenOrders = 2;
            o.EntryVolume = 0.002m;
        });

        h.Feed.Push(1_000m);
        await h.WaitFor(() => h.Exchange.Live.Count == 3, "вход и две заявки добора");

        h.Exchange.Fill(h.Exchange.Single(o => o.Kind == OrderKind.Market)!.ClientOrderId, 1_000m);
        await h.WaitFor(() => h.Engine.GetSnapshot().Position.Volume == 0.002m, "позиция");

        await h.Engine.StopAsync();

        await h.WaitFor(
            () => h.Exchange.Live.Any(o => o is { Kind: OrderKind.Market, ReduceOnly: true }),
            "рыночное закрытие при остановке");

        Assert.True(h.Exchange.CancelAllCalls > 0);

        h.Exchange.Fill(h.Exchange.Single(o => o.Kind == OrderKind.Market)!.ClientOrderId, 1_000m);

        await h.WaitFor(() => h.Engine.Status == BotStatus.Stopped, "бот остановлен");
        Assert.False(h.Feed.Started);
    }

    [Fact]
    public async Task Остановка_без_позиции_не_ставит_заявок()
    {
        await using var h = await Harness.StartAsync(o => o.MaxOpenOrders = 1);

        h.Feed.Push(1_000m);
        await h.WaitFor(() => h.Exchange.Live.Count == 1, "заявка добора");

        await h.Engine.StopAsync();

        await h.WaitFor(() => h.Engine.Status == BotStatus.Stopped, "бот остановлен");
        Assert.DoesNotContain(h.Exchange.History, o => o.Kind == OrderKind.Market);
    }

    [Fact]
    public async Task Частичное_исполнение_уровня_получает_тейк_на_залитый_объём()
    {
        await using var h = await Harness.StartAsync(o =>
        {
            o.LevelVolume = 0.004m;
            o.MaxOpenOrders = 1;
            o.AlignTakeProfitToGrid = true;
        });

        h.Feed.Push(1_000m);
        await h.WaitFor(() => h.Exchange.Live.Count == 1, "заявка добора");

        var level = h.Exchange.Live.Single();
        h.Exchange.FillPartially(level.ClientOrderId, 0.003m);
        await h.WaitFor(() => h.Engine.GetSnapshot().Position.Volume == 0.003m, "частичная позиция");

        // Пока заявка жива, тейка нет: иначе его пришлось бы переставлять на каждую сделку.
        Assert.DoesNotContain(h.Exchange.Live, o => o.ReduceOnly);

        // Остаток снят биржей — тейк выставляется на фактически залитый объём.
        h.Exchange.Cancel(level.ClientOrderId);

        await h.WaitFor(() => h.Exchange.Live.Any(o => o.ReduceOnly), "тейк на залитый объём");
        Assert.Equal(0.003m, h.Exchange.Single(o => o.ReduceOnly)!.Quantity);
    }

    [Fact]
    public async Task Без_ключей_бот_не_запускается()
    {
        var harness = Harness.Create();
        harness.Exchange.CanTrade = false;

        var error = await harness.Engine.StartAsync();

        Assert.NotNull(error);
        Assert.Contains("не может торговать", error);
        Assert.Equal(BotStatus.Faulted, harness.Engine.Status);

        await harness.DisposeAsync();
    }

    [Fact]
    public async Task Неизвестный_инструмент_не_даёт_запуститься()
    {
        var harness = Harness.Create(withInstrument: false);

        var error = await harness.Engine.StartAsync();

        Assert.NotNull(error);
        Assert.Contains("не найден", error);

        await harness.DisposeAsync();
    }

    [Fact]
    public async Task Открытая_позиция_на_бирже_не_даёт_запуститься()
    {
        var harness = Harness.Create();
        harness.Exchange.NetPosition = 0.5m;

        var error = await harness.Engine.StartAsync();

        Assert.NotNull(error);
        Assert.Contains("уже есть позиция", error);

        await harness.DisposeAsync();
    }

    [Fact]
    public async Task Негодные_настройки_не_дают_запуститься()
    {
        var harness = Harness.Create(tune: o => o.Levels = 0);

        var error = await harness.Engine.StartAsync();

        Assert.NotNull(error);
        Assert.Contains("Grid.Levels", error);

        await harness.DisposeAsync();
    }

    // ───────────────────────────────── обвязка ─────────────────────────────────

    private sealed class Harness : IAsyncDisposable
    {
        public required FakeFeed Feed { get; init; }

        public required FakeExecutionConnector Exchange { get; init; }

        public required BotEventLog Events { get; init; }

        public required GridBotEngine Engine { get; init; }

        public static Harness Create(Action<GridOptions>? tune = null, bool withInstrument = true)
        {
            var feed = new FakeFeed();
            var exchange = new FakeExecutionConnector();
            var events = new BotEventLog(200);
            var options = TestFactory.Bot(tune);

            var engine = new GridBotEngine(
                options,
                new FakeInstrumentProvider(withInstrument ? TestFactory.Instrument() : null),
                feed,
                exchange,
                events,
                NullLogger<GridBotEngine>.Instance);

            return new Harness
            {
                Feed = feed,
                Exchange = exchange,
                Events = events,
                Engine = engine
            };
        }

        public static async Task<Harness> StartAsync(Action<GridOptions>? tune = null)
        {
            var harness = Create(tune);

            var error = await harness.Engine.StartAsync();
            Assert.Null(error);

            return harness;
        }

        /// <summary>
        /// Дождаться условия. Заявки уходят на биржу через очередь, поэтому результат решения,
        /// принятого на тике, появляется не в тот же миг.
        /// </summary>
        public async Task WaitFor(Func<bool> condition, string what, int timeoutMs = 5_000)
        {
            var watch = Stopwatch.StartNew();

            while (watch.ElapsedMilliseconds < timeoutMs)
            {
                if (condition()) return;
                await Task.Delay(10);
            }

            var events = string.Join("\n  ", Events.Recent(20).Select(e => $"{e.Kind}: {e.Message}"));
            throw new TimeoutException($"Не дождались: {what}.\nСобытия:\n  {events}");
        }

        public ValueTask DisposeAsync() => Engine.DisposeAsync();
    }
}

internal static class SnapshotExtensions
{
    public static bool IsFlatInView(this PositionView view) => view.Volume == 0m && view.Side == null;
}
