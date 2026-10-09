using Bybit.Net.Clients;
using Bybit.Net.Objects.Models.V5;
using CryptoExchange.Net.Objects.Sockets;
using Microsoft.Extensions.Logging;
using SimpleTradeBot.Core.Configuration;
using SimpleTradeBot.Core.Domain;
using SimpleTradeBot.Core.MarketData;

namespace SimpleTradeBot.Bybit;

/// <summary>
/// Публичный поток сделок Bybit, склеенный в пулы за фиксированное окно.
/// </summary>
/// <remarks>
/// <para>Перенос <c>BybitTickFeeder</c> из исходного проекта, упрощённый до одного инструмента и
/// без записи сделок в базу. Склейка оставлена: биржа шлёт сделки потоком, и без неё на
/// ликвидном символе получалось бы несколько десятков проходов торгового цикла в секунду.</para>
/// <para>Объём остаётся в том виде, в каком его печатает биржа, — в базовой монете. Пересчёт
/// здесь был бы тихой сменой единиц у рыночных данных.</para>
/// </remarks>
public sealed class BybitMarketDataFeed(BybitOptions options, ILogger<BybitMarketDataFeed> logger)
    : IMarketDataFeed
{
    private readonly object _poolLock = new();
    private readonly Pool _pool = new();

    private BybitSocketClient? _socket;
    private CancellationTokenSource? _cts;
    private Task? _flushTask;
    private string _symbol = string.Empty;

    public event Action<Tick>? TickReceived;

    public async Task StartAsync(string symbol, CancellationToken ct = default)
    {
        // Фид поднимается заново при каждом запуске бота: сокет и токен прошлого запуска
        // освобождаем, иначе каждая остановка оставляла бы за собой живое соединение.
        await DisposeSocketAsync().ConfigureAwait(false);

        _symbol = symbol;
        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _socket = BybitClientFactory.CreatePublicSocket(options);

        var result = await _socket.V5LinearApi
            .SubscribeToTradeUpdatesAsync(symbol, OnTrades, _cts.Token)
            .ConfigureAwait(false);

        if (!result.Success)
        {
            throw new InvalidOperationException(
                $"Не удалось подписаться на сделки {symbol}: {result.Error}");
        }

        _flushTask = Task.Run(() => FlushLoopAsync(_cts.Token), CancellationToken.None);

        logger.LogInformation(
            "Фид Bybit запущен: {Symbol}, площадка {Environment}, окно пула {Pool} мс",
            symbol, BybitClientFactory.PublicEnvironment(options).Name, options.TickPoolMs);
    }

    public async Task StopAsync()
    {
        if (_cts != null) await _cts.CancelAsync().ConfigureAwait(false);

        if (_socket != null)
        {
            await _socket.UnsubscribeAllAsync().ConfigureAwait(false);
        }

        if (_flushTask != null)
        {
            try
            {
                await _flushTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }

            _flushTask = null;
        }

        logger.LogInformation("Фид Bybit остановлен.");
    }

    private void OnTrades(DataEvent<BybitTrade[]> data)
    {
        if (data.Data.Length == 0) return;

        lock (_poolLock)
        {
            foreach (var trade in data.Data)
            {
                if (trade.Price <= 0m) continue;
                if (!string.Equals(trade.Symbol, _symbol, StringComparison.OrdinalIgnoreCase)) continue;

                _pool.Add(trade);
            }
        }
    }

    private async Task FlushLoopAsync(CancellationToken ct)
    {
        var interval = TimeSpan.FromMilliseconds(Math.Max(50, options.TickPoolMs));

        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(interval, ct).ConfigureAwait(false);

                Tick? tick;
                lock (_poolLock)
                {
                    tick = _pool.Take();
                }

                if (tick != null) TickReceived?.Invoke(tick);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Ошибка раздачи пула сделок.");
            }
        }
    }

    private async Task DisposeSocketAsync()
    {
        if (_socket == null && _cts == null) return;

        await StopAsync().ConfigureAwait(false);

        _socket?.Dispose();
        _socket = null;

        _cts?.Dispose();
        _cts = null;
    }

    public ValueTask DisposeAsync() => new(DisposeSocketAsync());

    /// <summary>Накопитель сделок за текущее окно. Все обращения — под замком вызывающего.</summary>
    private sealed class Pool
    {
        private int _count;
        private decimal _low;
        private decimal _high;
        private decimal _last;
        private decimal _volume;
        private DateTime _timestamp;

        public void Add(BybitTrade trade)
        {
            if (_count == 0)
            {
                _low = trade.Price;
                _high = trade.Price;
            }
            else
            {
                if (trade.Price < _low) _low = trade.Price;
                if (trade.Price > _high) _high = trade.Price;
            }

            _count++;
            _volume += trade.Quantity;

            // Последняя цена пула — по самой свежей сделке: внутри пачки сделки идут по времени,
            // но сами пачки от биржи могут прийти внахлёст, и «последней» должна остаться поздняя.
            var stamp = DateTime.SpecifyKind(trade.Timestamp, DateTimeKind.Utc);
            if (stamp < _timestamp) return;

            _timestamp = stamp;
            _last = trade.Price;
        }

        /// <summary>Забрать накопленное и обнулиться. <c>null</c> — за окно сделок не было.</summary>
        public Tick? Take()
        {
            if (_count == 0) return null;

            var tick = new Tick(_timestamp, _last, _low, _high, _volume, _count);

            _count = 0;
            _low = 0m;
            _high = 0m;
            _last = 0m;
            _volume = 0m;
            _timestamp = default;

            return tick;
        }
    }
}
