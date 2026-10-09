using SimpleTradeBot.Core.Configuration;
using SimpleTradeBot.Core.Domain;

namespace SimpleTradeBot.Core.Strategy;

/// <summary>
/// Построение сетки: цены и объёмы уровней, цены тейк-профитов, стопа и общего тейка.
/// Чистая арифметика без состояния — поэтому её целиком покрывают тесты.
/// </summary>
public static class GridBuilder
{
    /// <summary>
    /// Шаг сетки в единицах цены, кратный тику.
    /// </summary>
    /// <remarks>
    /// <para>Процент считается от цены входа и снапится к целому числу тиков: сетка с шагом
    /// «1.37 тика» ставила бы уровни, которые биржа отклоняет по цене.</para>
    /// <para>Минимум — один тик. Без этой страховки сетка на дешёвом инструменте вырождается:
    /// 0.3% от цены 0.00002 после снапа к тику дают ноль, и все уровни оказываются на одной
    /// цене.</para>
    /// </remarks>
    public static decimal ResolveStep(GridOptions options, InstrumentSpec spec, decimal basePrice)
    {
        var tick = spec.TickSize;

        var raw = options.FixedStepInTicks
            ? options.StepTicks * tick
            : Math.Abs(basePrice * options.StepPercent / 100m);

        var step = tick > 0m
            ? Math.Round(raw / tick, MidpointRounding.AwayFromZero) * tick
            : raw;

        step = Math.Round(step, spec.PriceDecimals, MidpointRounding.AwayFromZero);

        return step <= 0m && tick > 0m ? tick : step;
    }

    /// <summary>
    /// Уровни сетки от цены входа. Для Long — вниз от <paramref name="anchorPrice"/>, для
    /// Short — вверх.
    /// </summary>
    /// <remarks>
    /// <para>Расстояние до уровня N накапливается с прогрессией: шаг уровня N равен
    /// <c>step × PriceFactor^(N−1)</c>. При PriceFactor = 1 это равномерная сетка, при большем
    /// значении уровни расходятся — дальние доборы оказываются дальше друг от друга.</para>
    /// <para>Объём растёт так же: <c>LevelVolume × VolumeFactor^(N−1)</c>. Это классический
    /// мартингейл, и при VolumeFactor &gt; 1 объём последних уровней растёт экспоненциально —
    /// ограничивать его должен MaxPositionVolume.</para>
    /// <para>Уровень, объём которого после округления к шагу оказался меньше минимального лота,
    /// пропускается: заявку с таким объёмом биржа не примет, а молча поставить вместо него
    /// минимальный лот значило бы торговать не тем объёмом, который задал человек.</para>
    /// </remarks>
    public static List<GridLevel> Build(
        GridOptions options,
        InstrumentSpec spec,
        decimal anchorPrice,
        decimal step)
    {
        var levels = new List<GridLevel>(Math.Max(0, options.Levels));
        if (anchorPrice <= 0m || step <= 0m) return levels;

        var side = options.Direction.ToEntrySide();
        var distance = 0m;
        var levelStep = step;
        var volume = options.LevelVolume;

        for (var index = 1; index <= options.Levels; index++)
        {
            distance += levelStep;
            levelStep *= options.PriceFactor;

            var raw = options.Direction == GridDirection.Long
                ? anchorPrice - distance
                : anchorPrice + distance;

            // Цена уровня ниже нуля означает, что сетка длиннее, чем расстояние до нуля: дальше
            // строить нечего.
            if (raw <= 0m) break;

            var price = spec.SnapPrice(raw, side);
            var levelVolume = spec.NormalizeQuantity(volume);
            volume *= options.VolumeFactor;

            if (levelVolume <= 0m) continue;

            levels.Add(new GridLevel(index, price, levelVolume));
        }

        return levels;
    }

    /// <summary>
    /// Цена поуровневого тейк-профита: один шаг сетки в сторону прибыли.
    /// </summary>
    /// <remarks>
    /// Расстояние тейка равно шагу сетки, а не общему TakeProfitPercent — так устроена «сетка1»:
    /// уровень выкупается ровно там, где стоит следующий уровень выше, и в боковике сетка
    /// снимает шаг за шагом. Общий TakeProfitPercent — отдельная, верхняя граница по всей
    /// позиции, см. <see cref="PositionTakeProfit"/>.
    /// </remarks>
    public static decimal LevelTakeProfit(
        GridOptions options,
        InstrumentSpec spec,
        GridLevel level,
        decimal step)
    {
        var basePrice = options.AlignTakeProfitToGrid || level.FilledPrice <= 0m
            ? level.Price
            : level.FilledPrice;

        var closingSide = options.Direction.ToEntrySide().Invert();

        var target = options.Direction == GridDirection.Long
            ? basePrice + step
            : basePrice - step;

        return spec.SnapPrice(target, closingSide);
    }

    /// <summary>
    /// Цена общего тейк-профита по позиции; <c>null</c> — общий тейк не задан.
    /// </summary>
    /// <remarks>
    /// Нужен потому, что объём рыночного входа (<c>EntryVolume</c>) не привязан ни к одному
    /// уровню — поуровневые тейки его не касаются, и без общего тейка у него не было бы выхода
    /// по прибыли вовсе.
    /// </remarks>
    public static decimal? PositionTakeProfit(
        GridOptions options,
        InstrumentSpec spec,
        decimal averagePrice,
        decimal farthestFilledPrice)
    {
        if (options.TakeProfitPercent is not { } percent || percent <= 0m) return null;
        if (averagePrice <= 0m) return null;

        var basePrice = options.TakeProfitBase == RiskBase.Fixed && farthestFilledPrice > 0m
            ? farthestFilledPrice
            : averagePrice;

        var closingSide = options.Direction.ToEntrySide().Invert();

        var target = options.Direction == GridDirection.Long
            ? basePrice * (1m + percent / 100m)
            : basePrice * (1m - percent / 100m);

        return spec.SnapPrice(target, closingSide);
    }

    /// <summary>
    /// Цена стоп-лосса по позиции; <c>null</c> — стоп не задан.
    /// </summary>
    /// <remarks>
    /// <see cref="RiskBase.Dynamic"/> — от средневзвешенной цены входа: стоп едет вместе с
    /// доборами, и каждый добор отодвигает его. <see cref="RiskBase.Fixed"/> — от цены самого
    /// дальнего залитого уровня: стоп стоит на месте, и сетка не может «уползти» вместе с ним.
    /// </remarks>
    public static decimal? PositionStopLoss(
        GridOptions options,
        InstrumentSpec spec,
        decimal averagePrice,
        decimal farthestFilledPrice)
    {
        if (options.StopLossPercent is not { } percent || percent <= 0m) return null;
        if (averagePrice <= 0m) return null;

        var basePrice = options.StopLossBase == RiskBase.Fixed && farthestFilledPrice > 0m
            ? farthestFilledPrice
            : averagePrice;

        var entrySide = options.Direction.ToEntrySide();

        var target = options.Direction == GridDirection.Long
            ? basePrice * (1m - percent / 100m)
            : basePrice * (1m + percent / 100m);

        // Округляем в сторону входа: стоп от этого срабатывает не раньше, чем задумано.
        return spec.SnapPrice(target, entrySide);
    }
}
