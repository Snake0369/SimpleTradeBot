namespace SimpleTradeBot.Core.Configuration;

/// <summary>Настройки бота (секция <c>Bot</c> в appsettings.json).</summary>
public sealed class BotOptions
{
    public const string SectionName = "Bot";

    /// <summary>
    /// Инструмент. Один на процесс — это принципиальное упрощение: состояние сетки живёт в
    /// памяти, и без второго инструмента в ней нет ни словарей по тикерам, ни блокировок на
    /// каждый из них.
    /// </summary>
    public string Symbol { get; set; } = "BTCUSDT";

    /// <summary>true — запустить бота сразу при старте процесса, не дожидаясь нажатия в UI.</summary>
    public bool AutoStart { get; set; }

    /// <summary>
    /// Снимать все заявки инструмента на бирже при запуске. Полезно после аварийного падения
    /// процесса: состояние сетки живёт в памяти, поэтому заявки прошлого запуска бот уже не
    /// узнает и сопровождать не сможет.
    /// </summary>
    public bool CancelOrphanOrdersOnStart { get; set; } = true;

    /// <summary>
    /// Не запускаться, если на инструменте уже есть позиция. Состояние сетки живёт в памяти, и
    /// чужую позицию бот сопровождать не умеет: он построит сетку так, будто позиции нет, а
    /// общий стоп-лосс посчитает от своей средней цены — то есть не от той, по которой позиция
    /// на самом деле набрана.
    /// </summary>
    public bool RequireFlatOnStart { get; set; } = true;

    /// <summary>Сколько последних событий держать в памяти для ленты в интерфейсе.</summary>
    public int EventLogSize { get; set; } = 300;

    public GridOptions Grid { get; set; } = new();
}
