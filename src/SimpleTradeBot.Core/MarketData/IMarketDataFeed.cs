using SimpleTradeBot.Core.Domain;

namespace SimpleTradeBot.Core.MarketData;

/// <summary>Рыночный фид: поток пулов сделок по одному инструменту.</summary>
public interface IMarketDataFeed : IAsyncDisposable
{
    /// <summary>Подключиться и подписаться на сделки инструмента.</summary>
    Task StartAsync(string symbol, CancellationToken ct = default);

    Task StopAsync();

    /// <summary>Пул сделок за очередное окно. Вызывается из потока сокета.</summary>
    event Action<Tick>? TickReceived;
}

/// <summary>Источник спецификаций инструментов.</summary>
public interface IInstrumentProvider
{
    /// <summary>
    /// Спецификация инструмента; <c>null</c> — инструмента нет среди торгуемых или биржа не
    /// ответила. Без неё запускаться нельзя: сетка строится по шагу цены, объём округляется по
    /// шагу объёма, и подставить сюда значения по умолчанию значило бы ставить заявки наугад.
    /// </summary>
    Task<InstrumentSpec?> GetAsync(string symbol, CancellationToken ct = default);
}
