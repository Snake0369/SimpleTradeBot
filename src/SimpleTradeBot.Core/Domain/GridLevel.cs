namespace SimpleTradeBot.Core.Domain;

/// <summary>
/// Уровень сетки: запланированная цена добора, её объём и состояние отработки.
/// </summary>
/// <remarks>
/// Уровень живёт весь цикл сетки и переиспользуется: после того как поуровневый тейк-профит
/// выкуплен, уровень возвращается в <see cref="LevelStatus.Idle"/> и может залиться снова.
/// Именно на этом сетка и зарабатывает в боковике — <see cref="Cycles"/> считает такие обороты.
/// </remarks>
public sealed class GridLevel(int index, decimal price, decimal volume)
{
    /// <summary>Номер уровня от цены входа: 1 — ближайший к ней.</summary>
    public int Index { get; } = index;

    /// <summary>Запланированная цена добора.</summary>
    public decimal Price { get; } = price;

    /// <summary>Запланированный объём добора.</summary>
    public decimal Volume { get; } = volume;

    public LevelStatus Status { get; set; } = LevelStatus.Idle;

    /// <summary>Идентификатор живой заявки добора; <c>null</c> — заявки в рынке нет.</summary>
    public Guid? EntryOrderId { get; set; }

    /// <summary>Идентификатор живой заявки тейк-профита.</summary>
    public Guid? TakeProfitOrderId { get; set; }

    /// <summary>Фактически залитый объём уровня.</summary>
    public decimal FilledVolume { get; set; }

    /// <summary>Средняя цена фактического залива уровня.</summary>
    public decimal FilledPrice { get; set; }

    /// <summary>Цена поуровневого тейк-профита; <c>null</c> — уровень не залит.</summary>
    public decimal? TakeProfitPrice { get; set; }

    /// <summary>Сколько раз уровень отработал оборот «залился, выкупился по тейку».</summary>
    public int Cycles { get; set; }

    /// <summary>Накопленный результат уровня за все обороты, в котируемой валюте.</summary>
    public decimal RealizedPnl { get; set; }

    /// <summary>Учесть частичное или полное исполнение заявки добора.</summary>
    public void ApplyEntryFill(decimal price, decimal volume)
    {
        if (volume <= 0m) return;

        var total = FilledVolume + volume;
        FilledPrice = (FilledPrice * FilledVolume + price * volume) / total;
        FilledVolume = total;
    }

    /// <summary>Вернуть уровень в строй после выкупа тейк-профита.</summary>
    public void Rearm()
    {
        Status = LevelStatus.Idle;
        EntryOrderId = null;
        TakeProfitOrderId = null;
        FilledVolume = 0m;
        FilledPrice = 0m;
        TakeProfitPrice = null;
    }
}
