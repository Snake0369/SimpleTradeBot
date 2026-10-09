using SimpleTradeBot.Core.Domain;
using SimpleTradeBot.Core.Strategy;
using Xunit;

namespace SimpleTradeBot.Tests;

public class GridBuilderTests
{
    [Fact]
    public void Шаг_из_процента_округляется_к_целому_числу_тиков()
    {
        var spec = TestFactory.Instrument(tickSize: 0.5m);
        var options = TestFactory.Grid(o => o.StepPercent = 0.3m);

        // 0.3% от 10 000 = 30; при шаге 0.5 это ровно 60 тиков.
        var step = GridBuilder.ResolveStep(options, spec, 10_000m);

        Assert.Equal(30m, step);
        Assert.Equal(0m, step % spec.TickSize);
    }

    [Fact]
    public void Шаг_никогда_не_меньше_одного_тика()
    {
        // Дешёвый инструмент: 0.3% от цены меньше тика, и без страховки все уровни легли бы
        // на одну цену.
        var spec = TestFactory.Instrument(tickSize: 0.0001m);
        var options = TestFactory.Grid(o => o.StepPercent = 0.0001m);

        var step = GridBuilder.ResolveStep(options, spec, 0.01m);

        Assert.Equal(spec.TickSize, step);
    }

    [Fact]
    public void Шаг_в_тиках_не_зависит_от_цены()
    {
        var spec = TestFactory.Instrument(tickSize: 0.1m);
        var options = TestFactory.Grid(o =>
        {
            o.FixedStepInTicks = true;
            o.StepTicks = 25m;
        });

        Assert.Equal(2.5m, GridBuilder.ResolveStep(options, spec, 100m));
        Assert.Equal(2.5m, GridBuilder.ResolveStep(options, spec, 95_000m));
    }

    [Fact]
    public void Long_строит_уровни_вниз_от_цены_входа()
    {
        var spec = TestFactory.Instrument();
        var options = TestFactory.Grid(o =>
        {
            o.Direction = GridDirection.Long;
            o.Levels = 3;
        });

        var levels = GridBuilder.Build(options, spec, 1_000m, step: 10m);

        Assert.Equal(3, levels.Count);
        Assert.Equal([990m, 980m, 970m], levels.Select(l => l.Price));
        Assert.Equal([1, 2, 3], levels.Select(l => l.Index));
    }

    [Fact]
    public void Short_строит_уровни_вверх_от_цены_входа()
    {
        var spec = TestFactory.Instrument();
        var options = TestFactory.Grid(o =>
        {
            o.Direction = GridDirection.Short;
            o.Levels = 3;
        });

        var levels = GridBuilder.Build(options, spec, 1_000m, step: 10m);

        Assert.Equal([1_010m, 1_020m, 1_030m], levels.Select(l => l.Price));
    }

    [Fact]
    public void PriceFactor_разводит_уровни_с_прогрессией()
    {
        var spec = TestFactory.Instrument();
        var options = TestFactory.Grid(o =>
        {
            o.Levels = 3;
            o.PriceFactor = 2m;
        });

        var levels = GridBuilder.Build(options, spec, 1_000m, step: 10m);

        // Шаги 10, 20, 40 — расстояния 10, 30, 70.
        Assert.Equal([990m, 970m, 930m], levels.Select(l => l.Price));
    }

    [Fact]
    public void VolumeFactor_наращивает_объём_с_прогрессией()
    {
        var spec = TestFactory.Instrument(quantityStep: 0.001m, minQuantity: 0.001m);
        var options = TestFactory.Grid(o =>
        {
            o.Levels = 4;
            o.LevelVolume = 0.001m;
            o.VolumeFactor = 2m;
        });

        var levels = GridBuilder.Build(options, spec, 1_000m, step: 10m);

        Assert.Equal([0.001m, 0.002m, 0.004m, 0.008m], levels.Select(l => l.Volume));
    }

    [Fact]
    public void Уровень_с_объёмом_меньше_лота_пропускается()
    {
        // Минимальный лот 0.01, а объём уровня 0.001 — такую заявку биржа не примет.
        var spec = TestFactory.Instrument(quantityStep: 0.01m, minQuantity: 0.01m);
        var options = TestFactory.Grid(o =>
        {
            o.Levels = 3;
            o.LevelVolume = 0.001m;
        });

        var levels = GridBuilder.Build(options, spec, 1_000m, step: 10m);

        Assert.Empty(levels);
    }

    [Fact]
    public void Цены_уровней_кратны_тику()
    {
        var spec = TestFactory.Instrument(tickSize: 0.5m);
        var options = TestFactory.Grid(o =>
        {
            o.Levels = 5;
            o.PriceFactor = 1.3m;
        });

        var levels = GridBuilder.Build(options, spec, 1_234.5m, step: 7m);

        Assert.All(levels, l => Assert.Equal(0m, l.Price % spec.TickSize));
    }

    [Fact]
    public void Уровни_ниже_нуля_не_строятся()
    {
        var spec = TestFactory.Instrument(tickSize: 0.1m);
        var options = TestFactory.Grid(o => o.Levels = 10);

        var levels = GridBuilder.Build(options, spec, 50m, step: 10m);

        Assert.Equal(4, levels.Count);
        Assert.All(levels, l => Assert.True(l.Price > 0m));
    }

    [Fact]
    public void Поуровневый_тейк_профит_равен_одному_шагу_сетки()
    {
        var spec = TestFactory.Instrument();
        var options = TestFactory.Grid(o => o.Direction = GridDirection.Long);
        var level = new GridLevel(1, 990m, 0.001m);
        level.ApplyEntryFill(989.7m, 0.001m);

        // По умолчанию тейк считается от фактической цены исполнения.
        Assert.Equal(999.7m, GridBuilder.LevelTakeProfit(options, spec, level, step: 10m));

        // С выравниванием по сетке — от цены уровня.
        options.AlignTakeProfitToGrid = true;
        Assert.Equal(1_000m, GridBuilder.LevelTakeProfit(options, spec, level, step: 10m));
    }

    [Fact]
    public void Поуровневый_тейк_профит_шорта_идёт_вниз()
    {
        var spec = TestFactory.Instrument();
        var options = TestFactory.Grid(o => o.Direction = GridDirection.Short);
        var level = new GridLevel(1, 1_010m, 0.001m);
        level.ApplyEntryFill(1_010m, 0.001m);

        Assert.Equal(1_000m, GridBuilder.LevelTakeProfit(options, spec, level, step: 10m));
    }

    [Fact]
    public void Общий_тейк_и_стоп_считаются_от_средней_цены()
    {
        var spec = TestFactory.Instrument();
        var options = TestFactory.Grid(o =>
        {
            o.Direction = GridDirection.Long;
            o.TakeProfitPercent = 2m;
            o.StopLossPercent = 5m;
        });

        Assert.Equal(1_020m, GridBuilder.PositionTakeProfit(options, spec, 1_000m, 0m));
        Assert.Equal(950m, GridBuilder.PositionStopLoss(options, spec, 1_000m, 0m));
    }

    [Fact]
    public void В_режиме_Fixed_стоп_считается_от_дальнего_залитого_уровня()
    {
        var spec = TestFactory.Instrument();
        var options = TestFactory.Grid(o =>
        {
            o.Direction = GridDirection.Long;
            o.StopLossPercent = 10m;
            o.StopLossBase = RiskBase.Fixed;
        });

        // Средняя 1000, дальний залитый уровень 900 — стоп от него, то есть ниже.
        Assert.Equal(810m, GridBuilder.PositionStopLoss(options, spec, 1_000m, 900m));
    }

    [Fact]
    public void Без_процента_общий_тейк_и_стоп_не_задаются()
    {
        var spec = TestFactory.Instrument();
        var options = TestFactory.Grid();

        Assert.Null(GridBuilder.PositionTakeProfit(options, spec, 1_000m, 0m));
        Assert.Null(GridBuilder.PositionStopLoss(options, spec, 1_000m, 0m));
    }

    [Fact]
    public void Шорт_разворачивает_стоп_и_тейк()
    {
        var spec = TestFactory.Instrument();
        var options = TestFactory.Grid(o =>
        {
            o.Direction = GridDirection.Short;
            o.TakeProfitPercent = 2m;
            o.StopLossPercent = 5m;
        });

        Assert.Equal(980m, GridBuilder.PositionTakeProfit(options, spec, 1_000m, 0m));
        Assert.Equal(1_050m, GridBuilder.PositionStopLoss(options, spec, 1_000m, 0m));
    }
}
