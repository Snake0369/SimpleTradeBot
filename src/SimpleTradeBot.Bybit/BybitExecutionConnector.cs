using Bybit.Net.Clients;
using Bybit.Net.Enums;
using Bybit.Net.Objects.Models.V5;
using CryptoExchange.Net.Objects.Sockets;
using Microsoft.Extensions.Logging;
using SimpleTradeBot.Core.Configuration;
using SimpleTradeBot.Core.Domain;
using SimpleTradeBot.Core.Execution;
using BybitOrderStatus = Bybit.Net.Enums.OrderStatus;
using BybitSide = Bybit.Net.Enums.OrderSide;

namespace SimpleTradeBot.Bybit;

/// <summary>
/// Исполнение заявок на Bybit (V5, категория linear, односторонний режим позиции).
/// </summary>
/// <remarks>
/// <para>Заявки ставятся по REST, состояния и сделки приходят приватным сокетом. Связка наша ↔
/// биржевая держится на <c>orderLinkId</c>: туда уходит наш <c>ClientOrderId</c> в формате
/// 32 hex-символа, и биржа возвращает его в каждом событии. Поэтому хранить соответствие
/// номеров нигде не нужно — ни в памяти, ни тем более в базе.</para>
/// <para>Односторонний режим (<see cref="PositionIdx.OneWayMode"/>) выбран намеренно: сетка
/// работает в одну сторону, и в хедж-режиме пришлось бы отдельно решать, к какой ноге относится
/// каждая заявка.</para>
/// <para>Закрывающие заявки всегда идут с <c>reduceOnly</c>. Это не перестраховка: если учёт
/// бота разойдётся с биржей, заявка без этого флага не закроет позицию, а откроет встречную.</para>
/// </remarks>
public sealed class BybitExecutionConnector : IExecutionConnector
{
    private const Category Market = Category.Linear;

    private readonly BybitOptions _options;
    private readonly ILogger<BybitExecutionConnector> _logger;
    private readonly BybitRestClient _rest;

    private BybitSocketClient? _socket;
    private CancellationTokenSource? _cts;
    private string _symbol = string.Empty;

    public BybitExecutionConnector(BybitOptions options, ILogger<BybitExecutionConnector> logger)
    {
        _options = options;
        _logger = logger;
        _rest = BybitClientFactory.CreateTradingRest(options);
    }

    public string Venue => "Bybit";

    public string Environment => BybitClientFactory.EnvironmentLabel(_options);

    public bool CanTrade => _options.HasCredentials;

    public event Action<OrderUpdate>? OrderUpdated;

    public event Action<TradeFill>? TradeExecuted;

    public event Action<OrderReject>? OrderRejected;

    public async Task StartAsync(string symbol, CancellationToken ct = default)
    {
        if (!CanTrade)
        {
            throw new InvalidOperationException(
                "Нет ключей Bybit: задайте Bybit:ApiKey и Bybit:ApiSecret.");
        }

        // Бота останавливают и запускают из интерфейса, поэтому подъём обязан быть
        // повторяемым: сокет и токен прошлого запуска освобождаем, а не бросаем.
        await DisposeSocketAsync().ConfigureAwait(false);

        _symbol = symbol;
        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _socket = BybitClientFactory.CreateTradingSocket(_options);

        var orders = await _socket.V5PrivateApi
            .SubscribeToOrderUpdatesAsync(OnOrderUpdates, _cts.Token)
            .ConfigureAwait(false);

        if (!orders.Success)
        {
            throw new InvalidOperationException(
                $"Не удалось подписаться на поток заявок: {orders.Error}. "
                + "Проверьте, что у ключа есть права на торговлю и он выпущен в той же среде "
                + $"({Environment}).");
        }

        var trades = await _socket.V5PrivateApi
            .SubscribeToUserTradeUpdatesAsync(OnUserTrades, _cts.Token)
            .ConfigureAwait(false);

        if (!trades.Success)
        {
            throw new InvalidOperationException($"Не удалось подписаться на поток сделок: {trades.Error}");
        }

        await ApplyLeverageAsync(symbol, _cts.Token).ConfigureAwait(false);

        _logger.LogInformation("Коннектор Bybit поднят: {Symbol}, площадка {Environment}", symbol, Environment);
    }

    /// <summary>
    /// Выставить плечо на инструмент.
    /// </summary>
    /// <remarks>
    /// Биржа отвечает ошибкой 110043, если плечо уже такое — это не отказ, а «менять нечего»,
    /// и падать на ней нельзя: при каждом втором запуске бот бы не поднимался.
    /// </remarks>
    private async Task ApplyLeverageAsync(string symbol, CancellationToken ct)
    {
        if (_options.Leverage <= 0m) return;

        var result = await _rest.V5Api.Account
            .SetLeverageAsync(Market, symbol, _options.Leverage, _options.Leverage, ct)
            .ConfigureAwait(false);

        if (result.Success)
        {
            _logger.LogInformation("Плечо {Leverage} выставлено на {Symbol}.", _options.Leverage, symbol);
            return;
        }

        if (result.Error?.Code == 110043)
        {
            _logger.LogInformation("Плечо на {Symbol} уже равно {Leverage}.", symbol, _options.Leverage);
            return;
        }

        _logger.LogWarning(
            "Плечо на {Symbol} выставить не удалось: {Error}. Торговля продолжится с текущим плечом счёта.",
            symbol, result.Error);
    }

    public async Task StopAsync()
    {
        if (_cts != null) await _cts.CancelAsync().ConfigureAwait(false);

        if (_socket != null)
        {
            await _socket.UnsubscribeAllAsync().ConfigureAwait(false);
        }

        _logger.LogInformation("Коннектор Bybit остановлен.");
    }

    public async Task<bool> PlaceAsync(OrderRequest request, CancellationToken ct = default)
    {
        var result = await _rest.V5Api.Trading.PlaceOrderAsync(
            Market,
            request.Symbol,
            ToBybit(request.Side),
            request.Kind == OrderKind.Market ? NewOrderType.Market : NewOrderType.Limit,
            request.Quantity,
            price: request.Kind == OrderKind.Market ? null : request.Price,
            timeInForce: request.Kind == OrderKind.Market
                ? TimeInForce.ImmediateOrCancel
                : TimeInForce.GoodTillCanceled,
            positionIdx: PositionIdx.OneWayMode,
            clientOrderId: ToLinkId(request.ClientOrderId),
            reduceOnly: request.ReduceOnly ? true : null,
            ct: ct).ConfigureAwait(false);

        if (result.Success) return true;

        var message = $"{result.Error?.Code}: {result.Error?.Message}";
        _logger.LogError(
            "Заявка не принята: {Side} {Kind} {Quantity} @ {Price} — {Error}",
            request.Side, request.Kind, request.Quantity, request.Price, message);

        OrderRejected?.Invoke(new OrderReject(request.ClientOrderId, message));
        return false;
    }

    public async Task<bool> CancelAsync(Guid clientOrderId, CancellationToken ct = default)
    {
        var result = await _rest.V5Api.Trading.CancelOrderAsync(
            Market,
            _symbol,
            orderId: null,
            clientOrderId: ToLinkId(clientOrderId),
            ct: ct).ConfigureAwait(false);

        if (result.Success) return true;

        // 110001 — заявки уже нет (исполнилась или снята). Для снятия это успех, а не ошибка.
        if (result.Error?.Code == 110001) return true;

        _logger.LogWarning("Снять заявку {Id} не удалось: {Error}", clientOrderId, result.Error);
        return false;
    }

    public async Task CancelAllAsync(CancellationToken ct = default)
    {
        var result = await _rest.V5Api.Trading
            .CancelAllOrderAsync(Market, _symbol, ct: ct)
            .ConfigureAwait(false);

        if (!result.Success)
        {
            _logger.LogWarning("Снять все заявки по {Symbol} не удалось: {Error}", _symbol, result.Error);
        }
    }

    public async Task<decimal?> GetNetPositionAsync(CancellationToken ct = default)
    {
        var result = await _rest.V5Api.Trading
            .GetPositionsAsync(Market, _symbol, ct: ct)
            .ConfigureAwait(false);

        if (!result.Success)
        {
            _logger.LogWarning("Позицию по {Symbol} получить не удалось: {Error}", _symbol, result.Error);
            return null;
        }

        var position = result.Data.List.FirstOrDefault(p =>
            string.Equals(p.Symbol, _symbol, StringComparison.OrdinalIgnoreCase));

        if (position == null) return 0m;

        return position.Side switch
        {
            PositionSide.Buy => position.Quantity,
            PositionSide.Sell => -position.Quantity,
            _ => 0m
        };
    }

    // ───────────────────────────────── события биржи ─────────────────────────────────

    private void OnOrderUpdates(DataEvent<BybitOrderUpdate[]> data)
    {
        foreach (var order in data.Data)
        {
            if (!Matches(order.Symbol)) continue;
            if (!TryParseLinkId(order.ClientOrderId, out var id)) continue;

            var status = Map(order.Status);

            OrderUpdated?.Invoke(new OrderUpdate(
                id,
                order.OrderId ?? string.Empty,
                status,
                order.Price ?? 0m,
                order.Quantity,
                order.QuantityFilled ?? 0m));

            if (status == OrderStatusKind.Rejected)
            {
                OrderRejected?.Invoke(new OrderReject(
                    id,
                    string.IsNullOrWhiteSpace(order.RejectReason)
                        ? "биржа отклонила заявку"
                        : order.RejectReason));
            }
        }
    }

    private void OnUserTrades(DataEvent<BybitUserTradeUpdate[]> data)
    {
        foreach (var trade in data.Data)
        {
            if (!Matches(trade.Symbol)) continue;
            if (!TryParseLinkId(trade.ClientOrderId, out var id)) continue;
            if (trade.Quantity <= 0m) continue;

            TradeExecuted?.Invoke(new TradeFill(
                id,
                trade.TradeId ?? string.Empty,
                FromBybit(trade.Side),
                trade.Price,
                trade.Quantity,
                trade.Fee ?? 0m));
        }
    }

    private bool Matches(string? symbol)
        => string.Equals(symbol, _symbol, StringComparison.OrdinalIgnoreCase);

    // ───────────────────────────────── преобразования ─────────────────────────────────

    /// <summary>
    /// Наш идентификатор в биржевой <c>orderLinkId</c>: 32 hex-символа без разделителей.
    /// Укладывается в лимит Bybit (36 символов) и не содержит запрещённых знаков.
    /// </summary>
    private static string ToLinkId(Guid id) => id.ToString("N");

    private static bool TryParseLinkId(string? linkId, out Guid id)
    {
        id = Guid.Empty;
        return !string.IsNullOrWhiteSpace(linkId) && Guid.TryParseExact(linkId, "N", out id);
    }

    private static BybitSide ToBybit(Side side) => side == Side.Buy ? BybitSide.Buy : BybitSide.Sell;

    private static Side FromBybit(BybitSide side) => side == BybitSide.Buy ? Side.Buy : Side.Sell;

    /// <summary>
    /// Биржевое состояние заявки в наше.
    /// </summary>
    /// <remarks>
    /// <see cref="BybitOrderStatus.PartiallyFilledCanceled"/> — конечное состояние: заявка снята
    /// после частичного исполнения. Трактовать его как «частично исполнена» значило бы ждать
    /// остатка, которого уже не будет, и держать уровень без тейк-профита вечно.
    /// </remarks>
    private static OrderStatusKind Map(BybitOrderStatus status) => status switch
    {
        BybitOrderStatus.Filled => OrderStatusKind.Filled,
        BybitOrderStatus.PartiallyFilled => OrderStatusKind.PartiallyFilled,
        BybitOrderStatus.Cancelled or BybitOrderStatus.PartiallyFilledCanceled
            or BybitOrderStatus.Deactivated => OrderStatusKind.Canceled,
        BybitOrderStatus.Rejected => OrderStatusKind.Rejected,
        _ => OrderStatusKind.New
    };

    private async Task DisposeSocketAsync()
    {
        if (_socket == null && _cts == null) return;

        await StopAsync().ConfigureAwait(false);

        _socket?.Dispose();
        _socket = null;

        _cts?.Dispose();
        _cts = null;
    }

    public async ValueTask DisposeAsync()
    {
        await DisposeSocketAsync().ConfigureAwait(false);

        _rest.Dispose();
    }
}
