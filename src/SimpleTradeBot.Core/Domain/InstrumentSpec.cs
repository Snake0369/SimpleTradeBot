using System.Globalization;

namespace SimpleTradeBot.Core.Domain;

/// <summary>
/// Спецификация инструмента: всё, что нужно, чтобы цены и объёмы заявок были приняты биржей.
/// </summary>
/// <remarks>
/// <para>Сетка строится по <see cref="TickSize"/>: шаг между уровнями всегда кратен тику, иначе
/// биржа отклонит заявку по цене. Объём округляется по <see cref="QuantityStep"/>.</para>
/// <para>У линейного перпетуала объём заявки измеряется в базовой монете (0.002 BTC, 499800
/// единиц 1000PEPE), а <see cref="QuantityStep"/> — только шаг округления этого объёма, не
/// множитель. Путать их нельзя: в исходном проекте это стоило ошибки в контроле капитала ровно
/// в QuantityStep раз — у инструмента с шагом 100 лимит на заявку оказался в сто раз меньше
/// нужного, и доборы не выставлялись вовсе.</para>
/// </remarks>
public sealed record InstrumentSpec(
    string Symbol,
    decimal TickSize,
    decimal QuantityStep,
    decimal MinQuantity,
    decimal MaxQuantity,
    string BaseAsset,
    string QuoteAsset)
{
    /// <summary>Знаков после запятой в цене — выводится из шага цены (0.01 даёт 2).</summary>
    public int PriceDecimals { get; } = Decimals(TickSize);

    /// <summary>Знаков после запятой в объёме — выводится из шага объёма.</summary>
    public int QuantityDecimals { get; } = Decimals(QuantityStep);

    /// <summary>Цена вниз до ближайшего тика.</summary>
    public decimal FloorPrice(decimal price) => Snap(price, TickSize, up: false, PriceDecimals);

    /// <summary>Цена вверх до ближайшего тика.</summary>
    public decimal CeilPrice(decimal price) => Snap(price, TickSize, up: true, PriceDecimals);

    /// <summary>
    /// Цена к тику в безопасную сторону: заявка на покупку округляется вниз, на продажу — вверх.
    /// Так округление никогда не делает сделку для нас хуже.
    /// </summary>
    public decimal SnapPrice(decimal price, Side side)
        => side == Side.Buy ? FloorPrice(price) : CeilPrice(price);

    /// <summary>
    /// Объём вниз до шага: вверх округлять нельзя — получится больше, чем разрешено лимитами
    /// стратегии.
    /// </summary>
    public decimal FloorQuantity(decimal quantity) => Snap(quantity, QuantityStep, up: false, QuantityDecimals);

    /// <summary>
    /// Объём, пригодный для заявки: округлён к шагу и не меньше минимального лота. Ноль означает
    /// «такой объём биржа не примет» — заявку ставить нельзя.
    /// </summary>
    public decimal NormalizeQuantity(decimal quantity)
    {
        if (quantity <= 0m) return 0m;

        var snapped = FloorQuantity(quantity);
        if (MaxQuantity > 0m && snapped > MaxQuantity) snapped = FloorQuantity(MaxQuantity);
        return snapped < MinQuantity ? 0m : snapped;
    }

    private static decimal Snap(decimal value, decimal step, bool up, int decimals)
    {
        if (step <= 0m) return value;

        var steps = up ? Math.Ceiling(value / step) : Math.Floor(value / step);

        // Округление по знакам после умножения чистит шум decimal-арифметики: без него шаг 0.1,
        // умноженный на число шагов, иногда даёт 0.30000000000000004, и биржа отклоняет цену как
        // некратную тику.
        return Math.Round(steps * step, decimals, MidpointRounding.AwayFromZero);
    }

    private static int Decimals(decimal step)
    {
        if (step <= 0m) return 0;

        var text = step.ToString(CultureInfo.InvariantCulture);
        var dot = text.IndexOf('.');
        return dot < 0 ? 0 : text[(dot + 1)..].TrimEnd('0').Length;
    }
}
