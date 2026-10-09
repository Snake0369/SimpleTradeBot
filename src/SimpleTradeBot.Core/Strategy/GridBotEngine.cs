using System.Globalization;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using SimpleTradeBot.Core.Configuration;
using SimpleTradeBot.Core.Domain;
using SimpleTradeBot.Core.Execution;
using SimpleTradeBot.Core.MarketData;
using SimpleTradeBot.Core.State;

namespace SimpleTradeBot.Core.Strategy;

/// <summary>
/// Сеточный бот на один инструмент. Полностью в памяти: после перезапуска процесса состояние не
/// восстанавливается, поэтому заявки прошлого запуска снимаются на старте.
/// </summary>
/// <remarks>
/// <para><b>Как работает цикл.</b> На первом тике бот запоминает цену (anchor), строит от неё
/// лесенку уровней и, если задан <c>EntryVolume</c>, входит рыночной заявкой. Дальше на каждом
/// тике он держит в рынке ближайшие <c>MaxOpenOrders</c> лимитных заявок добора, выставляет
/// поуровневый тейк-профит на каждый залитый уровень и проверяет общие тейк-профит, стоп-лосс и
/// трейлинг по всей позиции. Когда общий тейк или стоп срабатывает, позиция закрывается рыночной
/// заявкой, уровни сбрасываются и цикл при необходимости начинается заново от новой цены.</para>
/// <para><b>Почему один замок.</b> События приходят из трёх источников: пулы сделок из фида,
/// состояния заявок и сделки из приватного сокета, команды из интерфейса. Вся логика идёт под
/// одним <c>_sync</c>, поэтому состояние сетки согласовано по определению, без тонкостей с
/// порядком блокировок.</para>
/// <para><b>Почему заявки уходят через очередь.</b> Под замком нельзя ждать сеть: один медленный
/// ответ биржи остановил бы обработку тиков. Поэтому решение принимается под замком, а сама
/// постановка складывается в канал, который разгребает отдельная задача.</para>
/// </remarks>
public sealed class GridBotEngine : IAsyncDisposable
{
    private readonly BotOptions _options;
    private readonly IInstrumentProvider _instruments;
    private readonly IMarketDataFeed _feed;
    private readonly IExecutionConnector _execution;
    private readonly BotEventLog _events;
    private readonly ILogger<GridBotEngine> _logger;

    private readonly object _sync = new();
    private readonly SemaphoreSlim _lifecycle = new(1, 1);

    private readonly Channel<Func<CancellationToken, Task>> _outbox =
        Channel.CreateUnbounded<Func<CancellationToken, Task>>(new UnboundedChannelOptions
        {
            SingleReader = true
        });

    private readonly Dictionary<Guid, LiveOrder> _orders = [];
    private readonly PositionBook _position = new();
    private List<GridLevel> _levels = [];

    private CancellationTokenSource? _runCts;
    private Task? _pumpTask;
    private Timer? _maintenance;

    private InstrumentSpec? _spec;
    private BotStatus _status = BotStatus.Stopped;
    private string? _faultMessage;

    private decimal _lastPrice;
    private DateTime? _lastTickAt;
    private DateTime? _startedAt;

    private decimal _step;
    private decimal _anchorPrice;
    private int _cycle;

    private bool _flattening;
    private bool _stopRequested;
    private bool _stopFinishing;
    private DateTime? _stoppingSince;

    private bool _trailingArmed;
    private decimal? _trailingExtreme;

    private decimal? _takeProfitPrice;
    private decimal? _stopLossPrice;

    private int _levelCycles;
    private int _takeProfitCycles;
    private int _stopLossCycles;
    private decimal _equityPeak;
    private decimal _maxDrawdown;

    /// <summary>Сколько ждать исполнения закрывающей заявки, прежде чем остановиться принудительно.</summary>
    private static readonly TimeSpan StopTimeout = TimeSpan.FromSeconds(60);

    public GridBotEngine(
        BotOptions options,
        IInstrumentProvider instruments,
        IMarketDataFeed feed,
        IExecutionConnector execution,
        BotEventLog events,
        ILogger<GridBotEngine> logger)
    {
        _options = options;
        _instruments = instruments;
        _feed = feed;
        _execution = execution;
        _events = events;
        _logger = logger;

        _feed.TickReceived += OnTick;
        _execution.OrderUpdated += OnOrderUpdated;
        _execution.TradeExecuted += OnTradeExecuted;
        _execution.OrderRejected += OnOrderRejected;
    }

    public BotStatus Status
    {
        get { lock (_sync) return _status; }
    }

    // ───────────────────────────────── жизненный цикл ─────────────────────────────────

    /// <summary>Запустить бота. Возвращает текст ошибки, если запуститься не удалось.</summary>
    public async Task<string?> StartAsync(CancellationToken ct = default)
    {
        await _lifecycle.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            lock (_sync)
            {
                if (_status is BotStatus.Running or BotStatus.Starting) return "Бот уже запущен.";
                if (_status == BotStatus.Stopping) return "Бот останавливается, дождитесь завершения.";

                _status = BotStatus.Starting;
                _faultMessage = null;
            }

            var problems = _options.Grid.Validate();
            if (problems.Count > 0) return Fault(string.Join(" ", problems));

            if (!_execution.CanTrade)
            {
                return Fault(
                    "Коннектор не может торговать: не заданы Bybit:ApiKey и Bybit:ApiSecret "
                    + "(или у ключа нет прав на торговлю).");
            }

            _runCts = new CancellationTokenSource();
            var runToken = _runCts.Token;

            var spec = await _instruments.GetAsync(_options.Symbol, runToken).ConfigureAwait(false);
            if (spec == null)
            {
                return Fault($"Инструмент {_options.Symbol} не найден среди торгуемых USDT-перпетуалов Bybit.");
            }

            _events.Info("Instrument",
                $"{spec.Symbol}: шаг цены {spec.TickSize}, шаг объёма {spec.QuantityStep}, "
                + $"минимальный объём {spec.MinQuantity} {spec.BaseAsset}");

            await _execution.StartAsync(_options.Symbol, runToken).ConfigureAwait(false);

            if (_options.CancelOrphanOrdersOnStart)
            {
                // Состояние сетки живёт в памяти: заявки прошлого запуска бот уже не узнает и
                // сопровождать не сможет, а в рынке они останутся и однажды исполнятся.
                await _execution.CancelAllAsync(runToken).ConfigureAwait(false);
                _events.Info("Start", "Заявки прошлого запуска сняты.");
            }

            var net = await _execution.GetNetPositionAsync(runToken).ConfigureAwait(false);
            if (net is { } signed && signed != 0m && _options.RequireFlatOnStart)
            {
                await _execution.StopAsync().ConfigureAwait(false);
                return Fault(
                    $"На {_options.Symbol} уже есть позиция {signed.ToString(CultureInfo.InvariantCulture)}. "
                    + "Закройте её вручную или выключите Bot:RequireFlatOnStart, если сопровождаете её другим процессом.");
            }

            await _feed.StartAsync(_options.Symbol, runToken).ConfigureAwait(false);

            _pumpTask = Task.Run(() => PumpAsync(runToken), CancellationToken.None);
            _maintenance = new Timer(_ => Maintain(), null, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));

            lock (_sync)
            {
                _spec = spec;
                _orders.Clear();
                _levels = [];
                _position.Reset();
                _step = 0m;
                _anchorPrice = 0m;
                _cycle = 0;
                _levelCycles = 0;
                _takeProfitCycles = 0;
                _stopLossCycles = 0;
                _equityPeak = 0m;
                _maxDrawdown = 0m;
                _flattening = false;
                _stopRequested = false;
                _stoppingSince = null;
                _trailingArmed = false;
                _trailingExtreme = null;
                _takeProfitPrice = null;
                _stopLossPrice = null;
                _startedAt = DateTime.UtcNow;
                _status = BotStatus.Running;
            }

            _events.Info("Start",
                $"Бот запущен: {_options.Symbol}, {_options.Grid.Direction}, "
                + $"{_execution.Venue} / {_execution.Environment}");
            _logger.LogInformation("Бот запущен: {Symbol} {Direction} на {Venue}/{Environment}",
                _options.Symbol, _options.Grid.Direction, _execution.Venue, _execution.Environment);

            return null;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Запуск бота не удался.");
            return Fault(ex.Message);
        }
        finally
        {
            _lifecycle.Release();
        }
    }

    /// <summary>
    /// Остановить бота: снять заявки, закрыть позицию рыночной и отключиться. Возврат не
    /// дожидается исполнения закрывающей заявки — состояние видно в снимке.
    /// </summary>
    public async Task<string?> StopAsync(CancellationToken ct = default)
    {
        await _lifecycle.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            bool flat;
            lock (_sync)
            {
                if (_status is BotStatus.Stopped or BotStatus.Faulted) return "Бот не запущен.";
                if (_status == BotStatus.Stopping) return "Бот уже останавливается.";

                _status = BotStatus.Stopping;
                _stopRequested = true;
                _stoppingSince = DateTime.UtcNow;
                flat = _position.IsFlat;
            }

            _events.Info("Stop", "Остановка: снимаем заявки и закрываем позицию.");

            if (flat)
            {
                // Заявки снимает сама FinishStopAsync — напрямую, а не через очередь: очередь к
                // этому моменту уже останавливается, и команда из неё могла не успеть уйти.
                await FinishStopAsync().ConfigureAwait(false);
            }
            else
            {
                // Снятие идёт первым в очереди, закрытие — следом: порядок в канале сохраняется,
                // и позиция закрывается уже без живых заявок добора.
                Enqueue(ct2 => _execution.CancelAllAsync(ct2));
                BeginFlatten("остановка бота");
            }

            return null;
        }
        finally
        {
            _lifecycle.Release();
        }
    }

    /// <summary>
    /// Закрыть позицию и перестроить сетку от текущей цены, не останавливая бота. Нужно, когда
    /// цена ушла далеко от anchor и дальние уровни уже бессмысленны.
    /// </summary>
    public string? ClosePositionAndRestartCycle()
    {
        lock (_sync)
        {
            if (_status != BotStatus.Running) return "Бот не запущен.";
            if (_flattening) return "Закрытие позиции уже выполняется.";

            _events.Info("ClosePosition", "Ручное закрытие позиции, цикл будет перестроен от текущей цены.");
            CloseCycle("ручное закрытие", restart: true, countAsStop: false);
        }

        return null;
    }

    private async Task FinishStopAsync()
    {
        lock (_sync)
        {
            // Доводить остановку до конца может и исполнение закрывающей заявки, и watchdog по
            // таймауту — второй проход освободил бы таймер и коннектор повторно.
            if (_stopFinishing) return;
            _stopFinishing = true;
        }

        try
        {
            // Снимаем заявки здесь, а не через очередь: очередь сейчас останавливается, и
            // команда из неё могла бы не выполниться вовсе — заявки остались бы в рынке, а
            // сопровождать их после остановки уже некому.
            await _execution.CancelAllAsync().ConfigureAwait(false);
            await _feed.StopAsync().ConfigureAwait(false);
            await _execution.StopAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Отключение при остановке прошло с ошибкой.");
        }

        if (_maintenance != null)
        {
            await _maintenance.DisposeAsync().ConfigureAwait(false);
            _maintenance = null;
        }

        _runCts?.Cancel();

        // Недоехавшие команды выбрасываем: канал переживает остановку, и постановка заявки,
        // решённая до остановки, иначе ушла бы на биржу уже при следующем запуске.
        while (_outbox.Reader.TryRead(out _))
        {
        }

        lock (_sync)
        {
            _orders.Clear();
            _levels = [];
            _status = BotStatus.Stopped;
            _stoppingSince = null;
            _stopRequested = false;
            _stopFinishing = false;
            _flattening = false;
        }

        _events.Info("Stop", "Бот остановлен.");
        _logger.LogInformation("Бот остановлен.");
    }

    private string Fault(string message)
    {
        lock (_sync)
        {
            _status = BotStatus.Faulted;
            _faultMessage = message;
        }

        _events.Error("Fault", message);
        _logger.LogError("Бот не запущен: {Message}", message);
        return message;
    }

    // ───────────────────────────────── торговый цикл ─────────────────────────────────

    private void OnTick(Tick tick)
    {
        if (tick.Last <= 0m) return;

        lock (_sync)
        {
            _lastPrice = tick.Last;
            _lastTickAt = tick.Timestamp;

            if (_status != BotStatus.Running || _spec == null) return;

            TrackEquity();

            // Пока закрывающая заявка не доехала, сетку не трогаем: новый цикл поверх
            // закрывающейся позиции — это две встречные ноги на одном инструменте.
            if (_flattening) return;

            if (_levels.Count == 0)
            {
                StartCycle(tick.Last);
                return;
            }

            RefreshRiskLevels();

            if (CheckRisk(tick)) return;

            MaintainTakeProfits();
            MaintainLadder();
        }
    }

    /// <summary>
    /// Обслуживание раз в секунду: не зависит от того, идут ли сделки по инструменту. Нужно,
    /// чтобы тейк-профит на залитый уровень выставился и в тишине, а остановка не висела вечно,
    /// если закрывающая заявка потерялась.
    /// </summary>
    private void Maintain()
    {
        try
        {
            lock (_sync)
            {
                if (_status == BotStatus.Stopping && _stoppingSince is { } since
                    && DateTime.UtcNow - since > StopTimeout)
                {
                    _events.Warning("Stop",
                        "Закрывающая заявка не исполнилась за минуту — останавливаемся принудительно. "
                        + "Проверьте позицию на бирже.");
                    _stoppingSince = null;
                    _ = Task.Run(FinishStopAsync);
                    return;
                }

                if (_status != BotStatus.Running || _spec == null || _flattening) return;

                MaintainTakeProfits();
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Ошибка в обслуживающем проходе.");
        }
    }

    private void StartCycle(decimal price)
    {
        var spec = _spec!;
        var grid = _options.Grid;

        _step = GridBuilder.ResolveStep(grid, spec, price);
        _levels = GridBuilder.Build(grid, spec, price, _step);

        if (_levels.Count == 0)
        {
            _events.Error("Grid",
                $"Сетку построить не удалось: при цене {price} и шаге {_step} ни один уровень не "
                + "прошёл по минимальному объёму. Увеличьте Grid:LevelVolume.");
            _status = BotStatus.Faulted;
            _faultMessage = "Не удалось построить сетку: объём уровня меньше минимального лота.";
            return;
        }

        _anchorPrice = price;
        _cycle++;
        _trailingArmed = false;
        _trailingExtreme = null;
        _orders.Clear();

        _events.Info("Cycle",
            $"Цикл {_cycle}: anchor {price}, шаг {_step}, уровней {_levels.Count} "
            + $"({_levels[0].Price} … {_levels[^1].Price})");

        if (grid.EntryVolume > 0m)
        {
            var volume = spec.NormalizeQuantity(grid.EntryVolume);
            if (volume <= 0m)
            {
                _events.Warning("Entry",
                    $"EntryVolume {grid.EntryVolume} меньше минимального лота {spec.MinQuantity} — "
                    + "рыночного входа не будет, сетка работает только лимитными доборами.");
            }
            else
            {
                Place(OrderPurpose.Entry, grid.Direction.ToEntrySide(), OrderKind.Market, 0m, volume,
                    reduceOnly: false, levelIndex: null);
            }
        }

        MaintainLadder();
    }

    /// <summary>Пересчитать уровни общего тейк-профита и стоп-лосса по текущей позиции.</summary>
    private void RefreshRiskLevels()
    {
        if (_position.IsFlat)
        {
            _takeProfitPrice = null;
            _stopLossPrice = null;
            return;
        }

        var spec = _spec!;
        var farthest = FarthestFilledPrice();

        _takeProfitPrice = GridBuilder.PositionTakeProfit(_options.Grid, spec, _position.AveragePrice, farthest);
        _stopLossPrice = GridBuilder.PositionStopLoss(_options.Grid, spec, _position.AveragePrice, farthest);
    }

    /// <summary>
    /// Общие тейк-профит, стоп-лосс и трейлинг по всей позиции. Возвращает true, если цикл
    /// закрывается — тогда лесенку в этом тике трогать уже нельзя.
    /// </summary>
    /// <remarks>
    /// Пробой проверяется по интервалу пула (<see cref="Tick.Low"/>/<see cref="Tick.High"/>), а не
    /// по последней цене: внутри окна цена могла задеть стоп и вернуться, и по последней цене мы
    /// бы этого не увидели.
    /// </remarks>
    private bool CheckRisk(Tick tick)
    {
        if (_position.IsFlat) return false;

        var isLong = _position.Side == Side.Buy;

        if (_stopLossPrice is { } stop
            && (isLong ? tick.Low <= stop : tick.High >= stop))
        {
            _events.Warning("StopLoss",
                $"Стоп-лосс {stop}: цена {(isLong ? tick.Low : tick.High)}, средняя входа {_position.AveragePrice}.");
            CloseCycle("стоп-лосс", _options.Grid.RestartAfterStopLoss, countAsStop: true);
            return true;
        }

        if (_takeProfitPrice is { } take
            && (isLong ? tick.High >= take : tick.Low <= take))
        {
            _events.Trade("TakeProfit",
                $"Общий тейк-профит {take}: цена {(isLong ? tick.High : tick.Low)}, средняя входа {_position.AveragePrice}.");
            _takeProfitCycles++;
            CloseCycle("общий тейк-профит", _options.Grid.RestartAfterTakeProfit, countAsStop: false);
            return true;
        }

        return CheckTrailing(tick, isLong);
    }

    /// <summary>
    /// Трейлинг-стоп. Взводится, когда позиция ушла в плюс на <c>TrailingStopPercent</c>%, и
    /// срабатывает при откате от достигнутого экстремума на столько же.
    /// </summary>
    private bool CheckTrailing(Tick tick, bool isLong)
    {
        if (_options.Grid.TrailingStopPercent is not { } percent || percent <= 0m) return false;

        var entry = _position.AveragePrice;
        if (entry <= 0m) return false;

        var favourable = isLong ? tick.High : tick.Low;
        var profitPercent = isLong
            ? (favourable - entry) / entry * 100m
            : (entry - favourable) / entry * 100m;

        if (!_trailingArmed)
        {
            if (profitPercent < percent) return false;

            _trailingArmed = true;
            _trailingExtreme = favourable;
            _events.Info("Trailing", $"Трейлинг взведён на {favourable} (плюс {profitPercent:F2}%).");
            return false;
        }

        _trailingExtreme = isLong
            ? Math.Max(_trailingExtreme ?? favourable, favourable)
            : Math.Min(_trailingExtreme ?? favourable, favourable);

        var extreme = _trailingExtreme!.Value;
        var trigger = isLong
            ? extreme * (1m - percent / 100m)
            : extreme * (1m + percent / 100m);

        if (isLong ? tick.Low > trigger : tick.High < trigger) return false;

        _events.Trade("Trailing", $"Трейлинг-стоп {trigger:F8}: экстремум {extreme}, цена {tick.Last}.");
        _stopLossCycles++;
        CloseCycle("трейлинг-стоп", _options.Grid.RestartAfterTakeProfit, countAsStop: false);
        return true;
    }

    /// <summary>
    /// Держать в рынке ближайшие к цене заявки добора. Уровни по ту сторону цены не ставятся:
    /// лимитная покупка выше рынка исполнилась бы сразу и не по цене уровня.
    /// </summary>
    private void MaintainLadder()
    {
        var grid = _options.Grid;
        var spec = _spec!;
        var isLong = grid.Direction == GridDirection.Long;
        var entrySide = grid.Direction.ToEntrySide();

        var live = _orders.Values.Count(o => o.Purpose == OrderPurpose.LevelEntry);
        var limit = grid.MaxOpenOrders > 0 ? grid.MaxOpenOrders : int.MaxValue;
        var committed = CommittedVolume();
        var epsilon = spec.QuantityStep / 2m;

        var candidates = _levels
            .Where(l => l.Status == LevelStatus.Idle && l.EntryOrderId == null && l.FilledVolume <= 0m)
            .Where(l => isLong ? l.Price < _lastPrice : l.Price > _lastPrice)
            .OrderBy(l => Math.Abs(l.Price - _lastPrice))
            .ToList();

        foreach (var level in candidates)
        {
            if (live >= limit) break;

            if (grid.MaxPositionVolume > 0m && committed + level.Volume > grid.MaxPositionVolume + epsilon)
            {
                // Лимит объёма позиции: следующий уровень не влезает. Дальние уровни объёмнее
                // (VolumeFactor), поэтому и они не влезут — выходим, а не перебираем весь план.
                break;
            }

            var id = Place(OrderPurpose.LevelEntry, entrySide, OrderKind.Limit, level.Price, level.Volume,
                reduceOnly: false, levelIndex: level.Index);

            level.EntryOrderId = id;
            level.Status = LevelStatus.Placing;
            live++;
            committed += level.Volume;
        }
    }

    /// <summary>
    /// Выставить тейк-профит на каждый залитый уровень, у которого его нет.
    /// </summary>
    /// <remarks>
    /// Тейк ставится только когда заявка добора дошла до конечного состояния: иначе на частичном
    /// исполнении пришлось бы снимать и переставлять тейк на каждую сделку, а при гонке отмены с
    /// исполнением уровень остался бы вообще без выхода. До этого момента позицию держат общий
    /// стоп-лосс и трейлинг.
    /// </remarks>
    private void MaintainTakeProfits()
    {
        var spec = _spec!;
        var closingSide = _options.Grid.Direction.ToEntrySide().Invert();

        foreach (var level in _levels)
        {
            if (level.FilledVolume <= 0m) continue;
            if (level.EntryOrderId != null) continue;
            if (level.TakeProfitOrderId != null) continue;

            var volume = spec.NormalizeQuantity(level.FilledVolume);
            if (volume <= 0m)
            {
                // Остаток меньше минимального лота: отдельной заявкой его не закрыть, его заберёт
                // общее закрытие позиции.
                level.Status = LevelStatus.PartiallyFilled;
                continue;
            }

            var price = GridBuilder.LevelTakeProfit(_options.Grid, spec, level, _step);

            level.TakeProfitPrice = price;
            level.TakeProfitOrderId = Place(OrderPurpose.LevelTakeProfit, closingSide, OrderKind.Limit,
                price, volume, reduceOnly: true, levelIndex: level.Index);
            level.Status = LevelStatus.TakeProfit;
        }
    }

    /// <summary>
    /// Закрыть цикл: снять заявки, закрыть позицию рыночной, сбросить уровни.
    /// </summary>
    /// <param name="restart">true — после закрытия построить сетку заново от текущей цены.</param>
    /// <param name="countAsStop">Учитывать ли цикл как закрытый стопом — только для статистики.</param>
    private void CloseCycle(string reason, bool restart, bool countAsStop)
    {
        if (countAsStop) _stopLossCycles++;

        // Остановку, уже запрошенную из интерфейса, перезапуск цикла не отменяет.
        if (_status != BotStatus.Stopping) _stopRequested = !restart;

        Enqueue(ct => _execution.CancelAllAsync(ct));

        foreach (var level in _levels)
        {
            level.EntryOrderId = null;
            level.TakeProfitOrderId = null;
            level.Status = level.FilledVolume > 0m ? LevelStatus.PartiallyFilled : LevelStatus.Idle;
        }

        if (_position.IsFlat)
        {
            CompleteCycle(restart);
            return;
        }

        BeginFlatten(reason);
    }

    /// <summary>Поставить рыночную заявку на закрытие всей позиции.</summary>
    private void BeginFlatten(string reason)
    {
        _flattening = true;
        Enqueue(ct => FlattenAsync(reason, ct));
    }

    /// <summary>
    /// Рыночное закрытие позиции со сверкой со счётом.
    /// </summary>
    /// <remarks>
    /// Закрытие по собственному учёту безопасно ровно до тех пор, пока учёт совпадает со счётом.
    /// Если позиции на бирже уже нет — её закрыли руками, сняло ликвидацией, правили записи —
    /// закрывающая заявка ничего не закроет, а ОТКРОЕТ позицию в обратную сторону. В исходном
    /// проекте это однажды стоило двух лишних контрактов, поэтому сверка здесь обязательна, а не
    /// «на всякий случай».
    /// </remarks>
    private async Task FlattenAsync(string reason, CancellationToken ct)
    {
        Side side;
        decimal volume;
        lock (_sync)
        {
            if (_position.IsFlat)
            {
                FlattenFinished();
                return;
            }

            side = _position.Side!.Value;
            volume = _position.Volume;
        }

        decimal? net = null;
        try
        {
            net = await _execution.GetNetPositionAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // Нет связи — не повод оставлять позицию без присмотра: закрываем по учёту.
            _logger.LogWarning(ex, "Сверка позиции перед закрытием не удалась.");
        }

        var ledger = side == Side.Buy ? volume : -volume;

        if (net is { } exchange)
        {
            if (exchange == 0m)
            {
                _events.Warning("Flatten",
                    $"На бирже позиции нет, в учёте {ledger} — закрывать нечего. Учёт исправлен.");
                lock (_sync)
                {
                    _position.MarkFlat();
                    FlattenFinished();
                }

                return;
            }

            if (Math.Sign(exchange) != Math.Sign(ledger))
            {
                _events.Error("Flatten",
                    $"На бирже {exchange}, в учёте {ledger} — стороны не совпадают, закрытие отменено. "
                    + "Разберитесь с позицией вручную.");
                lock (_sync)
                {
                    FlattenFinished();
                }

                return;
            }

            if (Math.Abs(exchange) < Math.Abs(ledger))
            {
                _events.Warning("Flatten",
                    $"На бирже {exchange}, в учёте {ledger} — закрываем по бирже, расхождение останется в логе.");
                volume = Math.Abs(exchange);
            }
        }

        var spec = _spec;
        var quantity = spec?.NormalizeQuantity(volume) ?? volume;
        if (quantity <= 0m)
        {
            _events.Warning("Flatten", $"Остаток {volume} меньше минимального лота — закрывать нечем.");
            lock (_sync)
            {
                _position.MarkFlat();
                FlattenFinished();
            }

            return;
        }

        _events.Info("Flatten", $"Закрываем {quantity} по рынку ({reason}).");

        Guid id;
        lock (_sync)
        {
            id = Register(OrderPurpose.Flatten, side.Invert(), OrderKind.Market, 0m, quantity, null);
        }

        var request = new OrderRequest(id, _options.Symbol, side.Invert(), OrderKind.Market, 0m, quantity, true);
        var ok = await _execution.PlaceAsync(request, ct).ConfigureAwait(false);
        if (ok) return;

        lock (_sync)
        {
            _orders.Remove(id);
            FlattenFinished();
        }

        _events.Error("Flatten", "Биржа не приняла закрывающую заявку. Позиция осталась открытой.");
    }

    /// <summary>Закрытие доработало: либо позиция закрыта, либо закрывать было нечего.</summary>
    private void FlattenFinished()
    {
        _flattening = false;

        if (_status == BotStatus.Stopping || _stopRequested)
        {
            _ = Task.Run(FinishStopAsync);
            return;
        }

        CompleteCycle(restart: true);
    }

    private void CompleteCycle(bool restart)
    {
        _levels = [];
        _orders.Clear();
        _takeProfitPrice = null;
        _stopLossPrice = null;
        _trailingArmed = false;
        _trailingExtreme = null;

        if (restart)
        {
            _events.Info("Cycle", "Цикл закрыт, сетка будет построена заново от текущей цены.");
            return;
        }

        _events.Info("Cycle", "Цикл закрыт, перезапуск выключен — останавливаемся.");
        _stopRequested = true;
        _status = BotStatus.Stopping;
        _stoppingSince = DateTime.UtcNow;
        _ = Task.Run(FinishStopAsync);
    }

    // ───────────────────────────────── события коннектора ─────────────────────────────────

    private void OnOrderUpdated(OrderUpdate update)
    {
        lock (_sync)
        {
            if (!_orders.TryGetValue(update.ClientOrderId, out var order)) return;

            order.Confirmed = true;
            order.Filled = update.QuantityFilled;

            var level = order.LevelIndex is { } index
                ? _levels.FirstOrDefault(l => l.Index == index)
                : null;

            if (update.Status is OrderStatusKind.New or OrderStatusKind.PartiallyFilled)
            {
                if (level != null && order.Purpose == OrderPurpose.LevelEntry && level.FilledVolume <= 0m)
                {
                    level.Status = LevelStatus.Working;
                }

                return;
            }

            _orders.Remove(update.ClientOrderId);

            switch (order.Purpose)
            {
                case OrderPurpose.LevelEntry when level != null:
                    level.EntryOrderId = null;
                    level.Status = level.FilledVolume > 0m
                        ? LevelStatus.PartiallyFilled
                        : LevelStatus.Idle;
                    // Тейк на залитый объём — сразу, не дожидаясь следующего тика.
                    if (!_flattening && _status == BotStatus.Running) MaintainTakeProfits();
                    break;

                case OrderPurpose.LevelTakeProfit when level != null:
                    level.TakeProfitOrderId = null;
                    level.Status = level.FilledVolume > 0m
                        ? LevelStatus.PartiallyFilled
                        : LevelStatus.Idle;
                    break;

                case OrderPurpose.Flatten:
                    if (_flattening) FlattenFinished();
                    break;
            }

            if (update.Status == OrderStatusKind.Rejected)
            {
                _events.Warning("Order",
                    $"Заявка отклонена биржей: {order.Purpose} {order.Side} {order.Quantity} @ {order.Price}.");
            }
        }
    }

    private void OnTradeExecuted(TradeFill fill)
    {
        lock (_sync)
        {
            if (!_orders.TryGetValue(fill.ClientOrderId, out var order))
            {
                // Сделка по заявке, которой у нас уже нет: заявка дошла до конечного состояния
                // раньше, чем сделка по ней. В позицию её учесть обязательно — она состоялась.
                _position.Apply(fill.Side, fill.Price, fill.Quantity, fill.Fee);
                _events.Trade("Fill",
                    $"{fill.Side} {fill.Quantity} @ {fill.Price} (заявка уже закрыта)");
                TrackEquity();
                return;
            }

            var pnl = _position.Apply(fill.Side, fill.Price, fill.Quantity, fill.Fee);
            var level = order.LevelIndex is { } index
                ? _levels.FirstOrDefault(l => l.Index == index)
                : null;

            switch (order.Purpose)
            {
                case OrderPurpose.LevelEntry when level != null:
                    level.ApplyEntryFill(fill.Price, fill.Quantity);
                    _events.Trade("LevelFill",
                        $"Уровень {level.Index}: залито {fill.Quantity} @ {fill.Price}");
                    break;

                case OrderPurpose.LevelTakeProfit when level != null:
                    level.FilledVolume = Math.Max(0m, level.FilledVolume - fill.Quantity);
                    level.RealizedPnl += pnl - fill.Fee;
                    if (level.FilledVolume <= 0m)
                    {
                        level.Cycles++;
                        _levelCycles++;
                        level.Rearm();
                    }

                    _events.Trade("LevelTakeProfit",
                        $"Уровень {level.Index}: выкуплено {fill.Quantity} @ {fill.Price}, результат {pnl - fill.Fee:F4}");
                    break;

                case OrderPurpose.Entry:
                    _events.Trade("Entry", $"Рыночный вход: {fill.Quantity} @ {fill.Price}");
                    break;

                case OrderPurpose.Flatten:
                    _events.Trade("Flatten",
                        $"Закрытие: {fill.Quantity} @ {fill.Price}, результат {pnl - fill.Fee:F4}");
                    break;
            }

            RefreshRiskLevels();
            TrackEquity();
        }
    }

    private void OnOrderRejected(OrderReject reject)
    {
        lock (_sync)
        {
            if (!_orders.TryGetValue(reject.ClientOrderId, out var order)) return;

            _orders.Remove(reject.ClientOrderId);

            if (order.LevelIndex is { } index)
            {
                var level = _levels.FirstOrDefault(l => l.Index == index);
                if (level != null)
                {
                    if (order.Purpose == OrderPurpose.LevelEntry) level.EntryOrderId = null;
                    if (order.Purpose == OrderPurpose.LevelTakeProfit) level.TakeProfitOrderId = null;
                    level.Status = level.FilledVolume > 0m ? LevelStatus.PartiallyFilled : LevelStatus.Idle;
                }
            }

            if (order.Purpose == OrderPurpose.Flatten && _flattening) FlattenFinished();
        }

        _events.Error("Order", $"Отказ в постановке: {reject.Message}");
    }

    // ───────────────────────────────── заявки ─────────────────────────────────

    private Guid Register(
        OrderPurpose purpose,
        Side side,
        OrderKind kind,
        decimal price,
        decimal quantity,
        int? levelIndex)
    {
        var order = new LiveOrder
        {
            Id = Guid.NewGuid(),
            Purpose = purpose,
            Side = side,
            Kind = kind,
            Price = price,
            Quantity = quantity,
            LevelIndex = levelIndex,
            CreatedAt = DateTime.UtcNow
        };

        _orders[order.Id] = order;
        return order.Id;
    }

    private Guid Place(
        OrderPurpose purpose,
        Side side,
        OrderKind kind,
        decimal price,
        decimal quantity,
        bool reduceOnly,
        int? levelIndex)
    {
        var id = Register(purpose, side, kind, price, quantity, levelIndex);
        var request = new OrderRequest(id, _options.Symbol, side, kind, price, quantity, reduceOnly);

        Enqueue(async ct =>
        {
            var ok = await _execution.PlaceAsync(request, ct).ConfigureAwait(false);
            if (ok) return;

            // Биржа заявку не приняла, и события отказа может не быть (ошибка REST): убираем её
            // из учёта сами, иначе уровень навсегда останется с висящей ссылкой на заявку.
            OnOrderRejected(new OrderReject(id, $"{purpose} {side} {quantity} @ {price}: биржа отказала."));
        });

        return id;
    }

    private void Enqueue(Func<CancellationToken, Task> action) => _outbox.Writer.TryWrite(action);

    private async Task PumpAsync(CancellationToken ct)
    {
        try
        {
            while (await _outbox.Reader.WaitToReadAsync(ct).ConfigureAwait(false))
            {
                while (_outbox.Reader.TryRead(out var action))
                {
                    if (ct.IsCancellationRequested) return;

                    try
                    {
                        await action(ct).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        return;
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Команда биржи не выполнена.");
                        _events.Error("Exchange", ex.Message);
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    // ───────────────────────────────── состояние для интерфейса ─────────────────────────────────

    /// <summary>Занятый объём: открытая позиция плюс неисполненные заявки добора.</summary>
    private decimal CommittedVolume()
    {
        var open = _position.Side == _options.Grid.Direction.ToEntrySide() ? _position.Volume : 0m;
        var pending = _orders.Values
            .Where(o => o.Purpose is OrderPurpose.LevelEntry or OrderPurpose.Entry)
            .Sum(o => Math.Max(0m, o.Quantity - o.Filled));

        return open + pending;
    }

    /// <summary>Цена самого дальнего залитого уровня — база для Fixed-режима стопа и тейка.</summary>
    private decimal FarthestFilledPrice()
    {
        var filled = _levels.Where(l => l.FilledVolume > 0m && l.FilledPrice > 0m).ToList();
        if (filled.Count == 0) return 0m;

        return _options.Grid.Direction == GridDirection.Long
            ? filled.Min(l => l.FilledPrice)
            : filled.Max(l => l.FilledPrice);
    }

    private void TrackEquity()
    {
        var equity = _position.RealizedPnl - _position.Fees + _position.UnrealizedPnl(_lastPrice);

        if (equity > _equityPeak) _equityPeak = equity;

        var drawdown = _equityPeak - equity;
        if (drawdown > _maxDrawdown) _maxDrawdown = drawdown;
    }

    public BotSnapshot GetSnapshot()
    {
        lock (_sync)
        {
            var unrealized = _position.UnrealizedPnl(_lastPrice);

            return new BotSnapshot
            {
                Status = _status,
                Symbol = _options.Symbol,
                Venue = _execution.Venue,
                Environment = _execution.Environment,
                Direction = _options.Grid.Direction,
                FaultMessage = _faultMessage,
                LastPrice = _lastPrice,
                LastTickAt = _lastTickAt,
                StartedAt = _startedAt,
                Step = _step,
                AnchorPrice = _anchorPrice,
                Cycle = _cycle,
                Position = new PositionView
                {
                    Side = _position.Side,
                    Volume = _position.Volume,
                    AveragePrice = _position.AveragePrice,
                    UnrealizedPnl = unrealized,
                    RealizedPnl = _position.RealizedPnl,
                    Fees = _position.Fees,
                    NetPnl = _position.RealizedPnl - _position.Fees
                },
                Risk = new RiskView
                {
                    TakeProfitPrice = _takeProfitPrice,
                    StopLossPrice = _stopLossPrice,
                    TrailingStopPrice = TrailingTriggerPrice(),
                    TrailingExtreme = _trailingExtreme,
                    CommittedVolume = CommittedVolume(),
                    MaxPositionVolume = _options.Grid.MaxPositionVolume
                },
                Stats = new StatsView
                {
                    LevelCycles = _levelCycles,
                    Fills = _position.Fills,
                    TakeProfitCycles = _takeProfitCycles,
                    StopLossCycles = _stopLossCycles,
                    MaxDrawdown = _maxDrawdown
                },
                Levels = _levels.Select(l => new LevelView
                {
                    Index = l.Index,
                    Price = l.Price,
                    Volume = l.Volume,
                    Status = l.Status,
                    FilledVolume = l.FilledVolume,
                    FilledPrice = l.FilledPrice,
                    TakeProfitPrice = l.TakeProfitPrice,
                    Cycles = l.Cycles,
                    RealizedPnl = l.RealizedPnl
                }).ToList(),
                Orders = _orders.Values
                    .OrderBy(o => o.CreatedAt)
                    .Select(o => new OrderView
                    {
                        Id = o.Id,
                        Purpose = o.Purpose,
                        Side = o.Side,
                        Kind = o.Kind,
                        Price = o.Price,
                        Quantity = o.Quantity,
                        Filled = o.Filled,
                        LevelIndex = o.LevelIndex,
                        Confirmed = o.Confirmed,
                        CreatedAt = o.CreatedAt
                    }).ToList(),
                Events = _events.Recent(80)
            };
        }
    }

    private decimal? TrailingTriggerPrice()
    {
        if (!_trailingArmed || _trailingExtreme is not { } extreme) return null;
        if (_options.Grid.TrailingStopPercent is not { } percent || percent <= 0m) return null;

        return _position.Side == Side.Buy
            ? extreme * (1m - percent / 100m)
            : extreme * (1m + percent / 100m);
    }

    public async ValueTask DisposeAsync()
    {
        _feed.TickReceived -= OnTick;
        _execution.OrderUpdated -= OnOrderUpdated;
        _execution.TradeExecuted -= OnTradeExecuted;
        _execution.OrderRejected -= OnOrderRejected;

        if (_maintenance != null) await _maintenance.DisposeAsync().ConfigureAwait(false);

        _runCts?.Cancel();
        _outbox.Writer.TryComplete();

        if (_pumpTask != null)
        {
            try
            {
                await _pumpTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
        }

        await _feed.DisposeAsync().ConfigureAwait(false);
        await _execution.DisposeAsync().ConfigureAwait(false);

        _runCts?.Dispose();
        _lifecycle.Dispose();
    }

    /// <summary>Заявка, которую бот считает живой.</summary>
    private sealed class LiveOrder
    {
        public required Guid Id { get; init; }

        public required OrderPurpose Purpose { get; init; }

        public required Side Side { get; init; }

        public required OrderKind Kind { get; init; }

        public required decimal Price { get; init; }

        public required decimal Quantity { get; init; }

        public int? LevelIndex { get; init; }

        public decimal Filled { get; set; }

        /// <summary>Биржа подтвердила постановку.</summary>
        public bool Confirmed { get; set; }

        public DateTime CreatedAt { get; init; }
    }
}
