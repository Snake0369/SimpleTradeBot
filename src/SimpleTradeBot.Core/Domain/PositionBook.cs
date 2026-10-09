namespace SimpleTradeBot.Core.Domain;

/// <summary>
/// Нетто-позиция по инструменту: объём, средняя цена, реализованный результат и комиссии.
/// Считается по сделкам, которые пришли с биржи, а не по нашим ожиданиям.
/// </summary>
/// <remarks>
/// Линейный перпетуал: результат = (цена выхода − цена входа) × объём в базовой монете, в
/// котируемой валюте. Комиссия вычитается отдельной строкой, а не прячется в цене — иначе
/// средняя цена позиции перестала бы совпадать с биржевой, и сверка стала бы бесполезной.
/// </remarks>
public sealed class PositionBook
{
    /// <summary>Сторона открытой позиции; <c>null</c> — позиции нет.</summary>
    public Side? Side { get; private set; }

    /// <summary>Открытый объём, всегда положительный.</summary>
    public decimal Volume { get; private set; }

    /// <summary>Средневзвешенная цена входа.</summary>
    public decimal AveragePrice { get; private set; }

    /// <summary>Реализованный результат без комиссий.</summary>
    public decimal RealizedPnl { get; private set; }

    /// <summary>
    /// Уплаченные комиссии. Отрицательное значение возможно и означает ребейт мейкера: на
    /// лимитных заявках Bybit иногда платит, а не берёт, и превращать это в расход было бы
    /// неправдой.
    /// </summary>
    public decimal Fees { get; private set; }

    /// <summary>Сколько сделок прошло через книгу.</summary>
    public int Fills { get; private set; }

    public bool IsFlat => Volume <= 0m || Side is null;

    /// <summary>Нетто со знаком: плюс — лонг, минус — шорт. В этом виде позиция сверяется с биржей.</summary>
    public decimal SignedVolume => Side switch
    {
        Domain.Side.Buy => Volume,
        Domain.Side.Sell => -Volume,
        _ => 0m
    };

    /// <summary>
    /// Применить сделку. Возвращает реализованный по ней результат без комиссии — ноль, если
    /// сделка только набирала позицию.
    /// </summary>
    public decimal Apply(Side side, decimal price, decimal volume, decimal fee)
    {
        if (volume <= 0m) return 0m;

        Fills++;
        Fees += fee;

        if (Side is null || Volume <= 0m)
        {
            Side = side;
            Volume = volume;
            AveragePrice = price;
            return 0m;
        }

        if (Side == side)
        {
            // Добор: средняя цена пересчитывается по объёму.
            var total = Volume + volume;
            AveragePrice = (AveragePrice * Volume + price * volume) / total;
            Volume = total;
            return 0m;
        }

        // Встречная сделка: закрывает позицию целиком или частично.
        var closed = Math.Min(Volume, volume);
        var pnl = Side == Domain.Side.Buy
            ? (price - AveragePrice) * closed
            : (AveragePrice - price) * closed;

        RealizedPnl += pnl;
        Volume -= closed;

        var leftover = volume - closed;
        if (Volume > 0m) return pnl;

        if (leftover > 0m)
        {
            // Переворот: остатком встречной сделки открылась позиция в другую сторону. У сетки
            // этого быть не должно (закрывающие заявки идут с ReduceOnly), но если биржа всё же
            // перевернула позицию, учёт обязан показать именно это, а не ноль.
            Side = side;
            Volume = leftover;
            AveragePrice = price;
        }
        else
        {
            Side = null;
            Volume = 0m;
            AveragePrice = 0m;
        }

        return pnl;
    }

    /// <summary>Нереализованный результат по текущей цене.</summary>
    public decimal UnrealizedPnl(decimal lastPrice)
    {
        if (IsFlat || lastPrice <= 0m) return 0m;

        return Side == Domain.Side.Buy
            ? (lastPrice - AveragePrice) * Volume
            : (AveragePrice - lastPrice) * Volume;
    }

    /// <summary>
    /// Объявить позицию закрытой, сохранив накопленный результат. Нужно ровно для одного случая:
    /// сверка перед закрытием показала, что на бирже позиции нет, а в учёте она есть. Тогда прав
    /// счёт, а не учёт, и закрывать нечего.
    /// </summary>
    public void MarkFlat()
    {
        Side = null;
        Volume = 0m;
        AveragePrice = 0m;
    }

    /// <summary>Сбросить книгу. Вызывается при запуске бота, не между циклами сетки.</summary>
    public void Reset()
    {
        Side = null;
        Volume = 0m;
        AveragePrice = 0m;
        RealizedPnl = 0m;
        Fees = 0m;
        Fills = 0;
    }
}
