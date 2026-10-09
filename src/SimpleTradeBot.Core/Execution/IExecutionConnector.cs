using SimpleTradeBot.Core.Domain;

namespace SimpleTradeBot.Core.Execution;

/// <summary>
/// Коннектор исполнения: постановка и снятие заявок плюс поток состояний заявок и сделок.
/// </summary>
/// <remarks>
/// <para>Контракт нейтрален к бирже, как и в исходном проекте: стратегия не знает, кто за ним
/// стоит. Идентификация заявки — сквозной <c>ClientOrderId</c>, который мы генерируем сами;
/// коннектор обязан вернуть его в каждом <see cref="OrderUpdate"/> и <see cref="TradeFill"/>,
/// сам разбираясь, как связать его с биржевым номером.</para>
/// <para>Благодаря этому стратегия и интерфейс не меняются при смене площадки, а тесты получают
/// подставной коннектор без единой сетевой операции.</para>
/// </remarks>
public interface IExecutionConnector : IAsyncDisposable
{
    /// <summary>Название площадки — для логов и интерфейса.</summary>
    string Venue { get; }

    /// <summary>
    /// Куда именно подключены: демо-торговля, testnet или реальный счёт. Выводится в интерфейс
    /// крупно — путать эти три вещи дороже всего.
    /// </summary>
    string Environment { get; }

    /// <summary>
    /// Можно ли торговать. false — ключей нет или прав не хватает; стратегия в этом случае не
    /// запускается, вместо тихой работы «в никуда».
    /// </summary>
    bool CanTrade { get; }

    /// <summary>Подключиться и подписаться на поток заявок и сделок.</summary>
    Task StartAsync(string symbol, CancellationToken ct = default);

    /// <summary>Отключиться.</summary>
    Task StopAsync();

    /// <summary>Выставить заявку. false — биржа её не приняла, подробности придут в событии отказа.</summary>
    Task<bool> PlaceAsync(OrderRequest request, CancellationToken ct = default);

    /// <summary>Снять заявку по нашему идентификатору.</summary>
    Task<bool> CancelAsync(Guid clientOrderId, CancellationToken ct = default);

    /// <summary>Снять все заявки по инструменту.</summary>
    Task CancelAllAsync(CancellationToken ct = default);

    /// <summary>
    /// Нетто-позиция по данным биржи, со знаком; <c>null</c> — спросить не удалось.
    /// </summary>
    /// <remarks>
    /// Нужна перед рыночным закрытием. Закрытие по собственному учёту безопасно ровно до тех
    /// пор, пока учёт совпадает со счётом: если позиции на бирже уже нет — её закрыли руками
    /// или по ликвидации — закрывающая заявка ничего не закроет, а ОТКРОЕТ позицию в обратную
    /// сторону. В исходном проекте это стоило двух контрактов.
    /// </remarks>
    Task<decimal?> GetNetPositionAsync(CancellationToken ct = default);

    /// <summary>Состояние заявки изменилось.</summary>
    event Action<OrderUpdate>? OrderUpdated;

    /// <summary>По заявке прошла сделка.</summary>
    event Action<TradeFill>? TradeExecuted;

    /// <summary>Заявку не приняли.</summary>
    event Action<OrderReject>? OrderRejected;
}

/// <summary>Запрос на постановку заявки.</summary>
/// <param name="ClientOrderId">Наш идентификатор, он же ключ для всех событий по заявке.</param>
/// <param name="ReduceOnly">
/// true — заявка может только уменьшить позицию. Обязательна для всех закрывающих заявок: без
/// неё закрытие при расхождении учёта откроет встречную позицию.
/// </param>
public sealed record OrderRequest(
    Guid ClientOrderId,
    string Symbol,
    Side Side,
    OrderKind Kind,
    decimal Price,
    decimal Quantity,
    bool ReduceOnly);

/// <summary>Новое состояние заявки.</summary>
public sealed record OrderUpdate(
    Guid ClientOrderId,
    string ExchangeOrderId,
    OrderStatusKind Status,
    decimal Price,
    decimal Quantity,
    decimal QuantityFilled);

/// <summary>
/// Сделка по заявке. <paramref name="Fee"/> — расход положительным числом; отрицательное
/// значение означает ребейт мейкера, его отдаёт сама биржа.
/// </summary>
public sealed record TradeFill(
    Guid ClientOrderId,
    string TradeId,
    Side Side,
    decimal Price,
    decimal Quantity,
    decimal Fee);

/// <summary>Отказ в постановке.</summary>
public sealed record OrderReject(Guid ClientOrderId, string Message);
