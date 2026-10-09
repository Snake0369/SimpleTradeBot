using SimpleTradeBot.Core.Domain;

namespace SimpleTradeBot.Core.Configuration;

/// <summary>
/// Настройки сеточной стратегии (секция <c>Bot:Grid</c> в appsettings.json).
/// </summary>
/// <remarks>
/// Это перенесённый один-в-один набор параметров стратегии «сетка1» из закрытого проекта, с
/// говорящими именами вместо исторических. Соответствие:
/// <list type="bullet">
/// <item><see cref="EntryVolume"/> — NumberContractToTrend;</item>
/// <item><see cref="LevelVolume"/> — NumberContractOppositTrend;</item>
/// <item><see cref="Levels"/> — число уровней в сетке;</item>
/// <item><see cref="StepPercent"/> / <see cref="StepTicks"/> / <see cref="FixedStepInTicks"/> —
/// StepPriceOfOrders / StepPriceOfOrdersOfPP / FixPP;</item>
/// <item><see cref="TakeProfitPercent"/> — общий TakeProfit, <see cref="StopLossPercent"/> —
/// StopLoss, <see cref="TrailingStopPercent"/> — TrailingStopPercent.</item>
/// </list>
/// </remarks>
public sealed class GridOptions
{
    /// <summary>Направление сетки: Long набирается покупками вниз, Short — продажами вверх.</summary>
    public GridDirection Direction { get; set; } = GridDirection.Long;

    /// <summary>
    /// Объём рыночного входа в начале цикла, в базовой монете. Ноль — входа нет, сетка просто
    /// ждёт, пока цена дойдёт до первого уровня. Этот объём не привязан ни к одному уровню,
    /// поэтому закрыть его может только общий тейк-профит, стоп-лосс или трейлинг.
    /// </summary>
    public decimal EntryVolume { get; set; }

    /// <summary>Объём одного уровня сетки, в базовой монете.</summary>
    public decimal LevelVolume { get; set; } = 0.001m;

    /// <summary>Сколько уровней строить от цены входа.</summary>
    public int Levels { get; set; } = 10;

    /// <summary>Шаг сетки в процентах от цены входа. Используется, если <see cref="FixedStepInTicks"/> = false.</summary>
    public decimal StepPercent { get; set; } = 0.3m;

    /// <summary>Шаг сетки в тиках. Используется, если <see cref="FixedStepInTicks"/> = true.</summary>
    public decimal StepTicks { get; set; }

    /// <summary>true — считать шаг в тиках (<see cref="StepTicks"/>), игнорируя процент.</summary>
    public bool FixedStepInTicks { get; set; }

    /// <summary>
    /// Прогрессия объёма по уровням: объём уровня N = LevelVolume × VolumeFactor^(N−1).
    /// 1 — все уровни одинаковые, больше 1 — мартингейл (осторожно: объём растёт экспоненциально).
    /// </summary>
    public decimal VolumeFactor { get; set; } = 1m;

    /// <summary>
    /// Прогрессия шага по уровням: расстояние до уровня N = Step × PriceFactor^(N−1).
    /// 1 — равномерная сетка, больше 1 — уровни расходятся при уходе цены.
    /// </summary>
    public decimal PriceFactor { get; set; } = 1m;

    /// <summary>
    /// Общий тейк-профит в процентах от цены входа: закрывает позицию целиком, включая объём
    /// рыночного входа, у которого своего уровня нет. Ноль или null — общего тейка нет.
    /// </summary>
    public decimal? TakeProfitPercent { get; set; }

    /// <summary>От чего отсчитывать общий тейк-профит.</summary>
    public RiskBase TakeProfitBase { get; set; } = RiskBase.Dynamic;

    /// <summary>
    /// true — цена поуровневого тейк-профита считается от цены уровня, false — от фактической
    /// цены исполнения. На ликвидном рынке разница в один-два тика, на проскальзывании — больше.
    /// </summary>
    public bool AlignTakeProfitToGrid { get; set; }

    /// <summary>Стоп-лосс в процентах. Ноль или null — стопа нет.</summary>
    public decimal? StopLossPercent { get; set; }

    /// <summary>От чего отсчитывать стоп-лосс.</summary>
    public RiskBase StopLossBase { get; set; } = RiskBase.Dynamic;

    /// <summary>
    /// Трейлинг-стоп в процентах. Взводится, когда позиция ушла в плюс на столько же процентов,
    /// и срабатывает при откате от достигнутого экстремума. Ноль или null — трейлинга нет.
    /// </summary>
    public decimal? TrailingStopPercent { get; set; }

    /// <summary>
    /// Сколько заявок добора держать в рынке одновременно. Ноль — весь план сразу. Ограничение
    /// нужно не ради лимитов биржи, а чтобы не держать заявки, до которых цена дойдёт через
    /// неделю: лимит заявок на аккаунт не бесконечный.
    /// </summary>
    public int MaxOpenOrders { get; set; } = 5;

    /// <summary>
    /// Максимальный объём позиции в базовой монете. Ноль — без ограничения. Считается по
    /// открытому объёму плюс живые заявки добора: иначе несколько уровней за один тик суммарно
    /// перебрали бы лимит.
    /// </summary>
    public decimal MaxPositionVolume { get; set; }

    /// <summary>true — после общего тейк-профита цикл начинается заново от текущей цены.</summary>
    public bool RestartAfterTakeProfit { get; set; } = true;

    /// <summary>
    /// true — после стоп-лосса цикл начинается заново. По умолчанию false: стоп означает, что
    /// рынок пошёл против сетки, и перезаход без решения человека обычно только усугубляет.
    /// </summary>
    public bool RestartAfterStopLoss { get; set; }

    /// <summary>Проверка настроек. Возвращает список проблем; пустой список — всё в порядке.</summary>
    public IReadOnlyList<string> Validate()
    {
        var problems = new List<string>();

        if (Levels <= 0) problems.Add("Grid.Levels: должно быть больше нуля.");
        if (LevelVolume <= 0m) problems.Add("Grid.LevelVolume: должно быть больше нуля.");
        if (EntryVolume < 0m) problems.Add("Grid.EntryVolume: не может быть отрицательным.");

        if (FixedStepInTicks)
        {
            if (StepTicks <= 0m) problems.Add("Grid.StepTicks: при FixedStepInTicks=true должно быть больше нуля.");
        }
        else if (StepPercent <= 0m)
        {
            problems.Add("Grid.StepPercent: должно быть больше нуля (или задайте шаг в тиках).");
        }

        if (VolumeFactor <= 0m) problems.Add("Grid.VolumeFactor: должно быть больше нуля.");
        if (PriceFactor <= 0m) problems.Add("Grid.PriceFactor: должно быть больше нуля.");
        if (MaxOpenOrders < 0) problems.Add("Grid.MaxOpenOrders: не может быть отрицательным.");
        if (MaxPositionVolume < 0m) problems.Add("Grid.MaxPositionVolume: не может быть отрицательным.");

        if (StopLossPercent is { } sl && sl < 0m) problems.Add("Grid.StopLossPercent: не может быть отрицательным.");
        if (TakeProfitPercent is { } tp && tp < 0m) problems.Add("Grid.TakeProfitPercent: не может быть отрицательным.");
        if (TrailingStopPercent is { } ts && ts < 0m) problems.Add("Grid.TrailingStopPercent: не может быть отрицательным.");

        return problems;
    }
}
