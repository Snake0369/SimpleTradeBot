using SimpleTradeBot.Core.Domain;

namespace SimpleTradeBot.Core.State;

/// <summary>
/// Снимок состояния бота — то, что показывает интерфейс. Собирается под замком стратегии и
/// дальше неизменяем, поэтому его можно спокойно сериализовать в ответ HTTP.
/// </summary>
public sealed record BotSnapshot
{
    public required BotStatus Status { get; init; }

    public required string Symbol { get; init; }

    public required string Venue { get; init; }

    /// <summary>Площадка: DemoTrading, Testnet или Live. Выводится в интерфейс крупно.</summary>
    public required string Environment { get; init; }

    public required GridDirection Direction { get; init; }

    /// <summary>Причина аварии, если <see cref="Status"/> = Faulted.</summary>
    public string? FaultMessage { get; init; }

    public decimal LastPrice { get; init; }

    public DateTime? LastTickAt { get; init; }

    public DateTime? StartedAt { get; init; }

    /// <summary>Шаг сетки в единицах цены текущего цикла.</summary>
    public decimal Step { get; init; }

    /// <summary>Цена, от которой построена сетка текущего цикла.</summary>
    public decimal AnchorPrice { get; init; }

    /// <summary>Номер цикла сетки с момента запуска.</summary>
    public int Cycle { get; init; }

    public PositionView Position { get; init; } = new();

    public RiskView Risk { get; init; } = new();

    public StatsView Stats { get; init; } = new();

    public IReadOnlyList<LevelView> Levels { get; init; } = [];

    public IReadOnlyList<OrderView> Orders { get; init; } = [];

    public IReadOnlyList<BotEvent> Events { get; init; } = [];
}

public sealed record PositionView
{
    public Side? Side { get; init; }

    public decimal Volume { get; init; }

    public decimal AveragePrice { get; init; }

    public decimal UnrealizedPnl { get; init; }

    public decimal RealizedPnl { get; init; }

    public decimal Fees { get; init; }

    /// <summary>Реализованный результат минус комиссии — то, что реально заработано.</summary>
    public decimal NetPnl { get; init; }
}

public sealed record RiskView
{
    /// <summary>Цена общего тейк-профита по позиции.</summary>
    public decimal? TakeProfitPrice { get; init; }

    /// <summary>Цена стоп-лосса по позиции.</summary>
    public decimal? StopLossPrice { get; init; }

    /// <summary>Цена срабатывания трейлинга; <c>null</c> — трейлинг не взведён.</summary>
    public decimal? TrailingStopPrice { get; init; }

    /// <summary>Экстремум цены, от которого считается трейлинг.</summary>
    public decimal? TrailingExtreme { get; init; }

    /// <summary>Занятый объём: открытая позиция плюс живые заявки добора.</summary>
    public decimal CommittedVolume { get; init; }

    /// <summary>Лимит объёма позиции; 0 — без ограничения.</summary>
    public decimal MaxPositionVolume { get; init; }
}

public sealed record StatsView
{
    /// <summary>Сколько раз уровни отработали оборот «залился, выкупился по тейку».</summary>
    public int LevelCycles { get; init; }

    /// <summary>Сколько сделок прошло через книгу позиции.</summary>
    public int Fills { get; init; }

    /// <summary>Сколько циклов сетки закрыто общим тейк-профитом.</summary>
    public int TakeProfitCycles { get; init; }

    /// <summary>Сколько циклов закрыто стоп-лоссом или трейлингом.</summary>
    public int StopLossCycles { get; init; }

    /// <summary>Максимальная просадка собственного результата (реализованный плюс открытый).</summary>
    public decimal MaxDrawdown { get; init; }
}

public sealed record LevelView
{
    public required int Index { get; init; }

    public required decimal Price { get; init; }

    public required decimal Volume { get; init; }

    public required LevelStatus Status { get; init; }

    public decimal FilledVolume { get; init; }

    public decimal FilledPrice { get; init; }

    public decimal? TakeProfitPrice { get; init; }

    public int Cycles { get; init; }

    public decimal RealizedPnl { get; init; }
}

public sealed record OrderView
{
    public required Guid Id { get; init; }

    public required OrderPurpose Purpose { get; init; }

    public required Side Side { get; init; }

    public required OrderKind Kind { get; init; }

    public required decimal Price { get; init; }

    public required decimal Quantity { get; init; }

    public decimal Filled { get; init; }

    public int? LevelIndex { get; init; }

    public bool Confirmed { get; init; }

    public DateTime CreatedAt { get; init; }
}
