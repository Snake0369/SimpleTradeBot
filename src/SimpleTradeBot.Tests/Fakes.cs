using System.Collections.Concurrent;
using SimpleTradeBot.Core.Domain;
using SimpleTradeBot.Core.Execution;
using SimpleTradeBot.Core.MarketData;

namespace SimpleTradeBot.Tests;

/// <summary>Справочник инструментов, отдающий заранее заданную спецификацию.</summary>
internal sealed class FakeInstrumentProvider(InstrumentSpec? spec) : IInstrumentProvider
{
    public Task<InstrumentSpec?> GetAsync(string symbol, CancellationToken ct = default)
        => Task.FromResult(spec);
}

/// <summary>Фид, в который тест сам подаёт пулы сделок.</summary>
internal sealed class FakeFeed : IMarketDataFeed
{
    public event Action<Tick>? TickReceived;

    public bool Started { get; private set; }

    public Task StartAsync(string symbol, CancellationToken ct = default)
    {
        Started = true;
        return Task.CompletedTask;
    }

    public Task StopAsync()
    {
        Started = false;
        return Task.CompletedTask;
    }

    /// <summary>Подать пул, в котором минимум и максимум равны цене.</summary>
    public void Push(decimal price) => Push(price, price, price);

    public void Push(decimal last, decimal low, decimal high)
        => TickReceived?.Invoke(new Tick(DateTime.UtcNow, last, low, high, 1m, 1));

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

/// <summary>
/// Подставная биржа: принимает заявки и даёт тесту исполнять их вручную. Своей логики
/// сведения заявок у неё нет — тест решает, что и когда исполнилось.
/// </summary>
internal sealed class FakeExecutionConnector : IExecutionConnector
{
    private readonly ConcurrentDictionary<Guid, OrderRequest> _live = new();
    private readonly List<OrderRequest> _history = [];
    private readonly object _sync = new();

    public string Venue => "Fake";

    public string Environment => "Test";

    public bool CanTrade { get; set; } = true;

    /// <summary>Нетто-позиция, которую «видит» биржа. По умолчанию следует за исполнениями.</summary>
    public decimal? NetPosition { get; set; } = 0m;

    public int CancelAllCalls { get; private set; }

    public event Action<OrderUpdate>? OrderUpdated;

    public event Action<TradeFill>? TradeExecuted;

    public event Action<OrderReject>? OrderRejected;

    /// <summary>Заявки, которые биржа считает живыми.</summary>
    public IReadOnlyCollection<OrderRequest> Live => _live.Values.ToList();

    /// <summary>Все заявки, когда-либо поставленные, в порядке поступления.</summary>
    public IReadOnlyList<OrderRequest> History
    {
        get { lock (_sync) return _history.ToList(); }
    }

    public Task StartAsync(string symbol, CancellationToken ct = default) => Task.CompletedTask;

    public Task StopAsync() => Task.CompletedTask;

    public Task<bool> PlaceAsync(OrderRequest request, CancellationToken ct = default)
    {
        _live[request.ClientOrderId] = request;
        lock (_sync) _history.Add(request);

        // Биржа подтверждает постановку — так же, как это делает приватный сокет Bybit.
        OrderUpdated?.Invoke(new OrderUpdate(
            request.ClientOrderId, "x" + request.ClientOrderId.ToString("N")[..8],
            OrderStatusKind.New, request.Price, request.Quantity, 0m));

        return Task.FromResult(true);
    }

    public Task<bool> CancelAsync(Guid clientOrderId, CancellationToken ct = default)
    {
        Cancel(clientOrderId);
        return Task.FromResult(true);
    }

    public Task CancelAllAsync(CancellationToken ct = default)
    {
        CancelAllCalls++;
        foreach (var id in _live.Keys.ToList()) Cancel(id);
        return Task.CompletedTask;
    }

    public Task<decimal?> GetNetPositionAsync(CancellationToken ct = default)
        => Task.FromResult(NetPosition);

    /// <summary>Исполнить заявку целиком по её цене (для рыночной — по указанной).</summary>
    public void Fill(Guid id, decimal? price = null, decimal fee = 0m)
    {
        if (!_live.TryRemove(id, out var request)) return;

        var tradePrice = price ?? request.Price;

        NetPosition += request.Side == Side.Buy ? request.Quantity : -request.Quantity;

        TradeExecuted?.Invoke(new TradeFill(
            id, Guid.NewGuid().ToString("N"), request.Side, tradePrice, request.Quantity, fee));

        OrderUpdated?.Invoke(new OrderUpdate(
            id, "x", OrderStatusKind.Filled, tradePrice, request.Quantity, request.Quantity));
    }

    /// <summary>Исполнить заявку частично: сделка есть, заявка остаётся живой.</summary>
    public void FillPartially(Guid id, decimal quantity, decimal? price = null)
    {
        if (!_live.TryGetValue(id, out var request)) return;

        var tradePrice = price ?? request.Price;

        NetPosition += request.Side == Side.Buy ? quantity : -quantity;

        TradeExecuted?.Invoke(new TradeFill(
            id, Guid.NewGuid().ToString("N"), request.Side, tradePrice, quantity, 0m));

        OrderUpdated?.Invoke(new OrderUpdate(
            id, "x", OrderStatusKind.PartiallyFilled, tradePrice, request.Quantity, quantity));
    }

    public void Cancel(Guid id)
    {
        if (!_live.TryRemove(id, out var request)) return;

        OrderUpdated?.Invoke(new OrderUpdate(
            id, "x", OrderStatusKind.Canceled, request.Price, request.Quantity, 0m));
    }

    public void Reject(Guid id, string message = "отказ")
    {
        _live.TryRemove(id, out _);
        OrderRejected?.Invoke(new OrderReject(id, message));
    }

    /// <summary>Единственная живая заявка с таким назначением; удобно для коротких тестов.</summary>
    public OrderRequest? Single(Func<OrderRequest, bool> predicate)
        => _live.Values.SingleOrDefault(predicate);

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
