using System.Collections.Concurrent;

namespace SimpleTradeBot.Core.State;

public enum BotEventLevel
{
    Info = 0,
    Trade = 1,
    Warning = 2,
    Error = 3
}

/// <summary>Событие бота для ленты в интерфейсе.</summary>
public sealed record BotEvent(DateTime Timestamp, BotEventLevel Level, string Kind, string Message);

/// <summary>
/// Лента последних событий. Кольцевой буфер в памяти: без базы история живёт столько, сколько
/// живёт процесс, и это осознанный выбор, а не упущение.
/// </summary>
public sealed class BotEventLog(int capacity)
{
    private readonly ConcurrentQueue<BotEvent> _events = new();
    private readonly int _capacity = Math.Max(10, capacity);

    public void Add(BotEventLevel level, string kind, string message)
    {
        _events.Enqueue(new BotEvent(DateTime.UtcNow, level, kind, message));

        while (_events.Count > _capacity && _events.TryDequeue(out _))
        {
        }
    }

    public void Info(string kind, string message) => Add(BotEventLevel.Info, kind, message);

    public void Trade(string kind, string message) => Add(BotEventLevel.Trade, kind, message);

    public void Warning(string kind, string message) => Add(BotEventLevel.Warning, kind, message);

    public void Error(string kind, string message) => Add(BotEventLevel.Error, kind, message);

    /// <summary>Последние события, свежие сверху.</summary>
    public IReadOnlyList<BotEvent> Recent(int count)
        => _events.Reverse().Take(Math.Max(1, count)).ToList();

    public void Clear()
    {
        while (_events.TryDequeue(out _))
        {
        }
    }
}
