namespace SimpleTradeBot.Core.Domain;

/// <summary>Сторона заявки.</summary>
public enum Side
{
    Buy = 1,
    Sell = 2
}

/// <summary>Направление сетки. Long — добираем вниз, Short — добираем вверх.</summary>
public enum GridDirection
{
    Long = 1,
    Short = 2
}

/// <summary>Тип заявки. Рыночные используются только для входа и для закрытия позиции.</summary>
public enum OrderKind
{
    Limit = 1,
    Market = 2
}

/// <summary>Состояние бота. Живёт в памяти процесса: после перезапуска всё начинается заново.</summary>
public enum BotStatus
{
    /// <summary>Остановлен: заявок в рынке нет, позиции нет.</summary>
    Stopped = 0,

    /// <summary>Поднимается: тянем спецификацию инструмента, подключаем фид и коннектор.</summary>
    Starting = 1,

    /// <summary>Работает: сетка сопровождается на каждом тике.</summary>
    Running = 2,

    /// <summary>Останавливается: заявки снимаются, позиция закрывается рыночной.</summary>
    Stopping = 3,

    /// <summary>Авария: подъём не удался. Причина — в FaultMessage снимка состояния.</summary>
    Faulted = 4
}

/// <summary>Состояние уровня сетки.</summary>
public enum LevelStatus
{
    /// <summary>Свободен: заявки нет, уровень ждёт своей очереди в лесенке.</summary>
    Idle = 0,

    /// <summary>Заявка отправлена, подтверждения с биржи ещё нет.</summary>
    Placing = 1,

    /// <summary>Заявка стоит в рынке.</summary>
    Working = 2,

    /// <summary>Уровень залит частично.</summary>
    PartiallyFilled = 3,

    /// <summary>Уровень залит, выставлен поуровневый тейк-профит.</summary>
    TakeProfit = 4
}

/// <summary>Зачем выставлена заявка. Определяет, что делать с её исполнением.</summary>
public enum OrderPurpose
{
    /// <summary>Рыночный вход в начале цикла (EntryVolume). Закрывается только общим TP/SL.</summary>
    Entry = 0,

    /// <summary>Лимитный добор на уровне сетки.</summary>
    LevelEntry = 1,

    /// <summary>Поуровневый тейк-профит: закрывает объём своего уровня.</summary>
    LevelTakeProfit = 2,

    /// <summary>Рыночное закрытие всей позиции (общий TP/SL, трейлинг, ручная остановка).</summary>
    Flatten = 3
}

/// <summary>Состояние заявки, приведённое к общему виду.</summary>
public enum OrderStatusKind
{
    New = 0,
    PartiallyFilled = 1,
    Filled = 2,
    Canceled = 3,
    Rejected = 4
}

/// <summary>От чего отсчитывать стоп-лосс и общий тейк-профит.</summary>
public enum RiskBase
{
    /// <summary>От средневзвешенной цены входа — уровень двигается вместе с доборами.</summary>
    Dynamic = 0,

    /// <summary>От цены самого дальнего залитого уровня — не двигается при доборах.</summary>
    Fixed = 1
}

public static class SideExtensions
{
    public static Side Invert(this Side side) => side == Side.Buy ? Side.Sell : Side.Buy;

    /// <summary>Сторона, которой сетка набирает позицию: Long набирается покупками.</summary>
    public static Side ToEntrySide(this GridDirection direction)
        => direction == GridDirection.Long ? Side.Buy : Side.Sell;
}
