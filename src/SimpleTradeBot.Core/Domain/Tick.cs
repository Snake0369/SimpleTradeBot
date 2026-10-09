namespace SimpleTradeBot.Core.Domain;

/// <summary>
/// Пул сделок за одно окно: минимум, максимум и последняя цена.
/// </summary>
/// <remarks>
/// Биржа шлёт сделки потоком — на ликвидном символе это несколько десятков в секунду.
/// Сопровождать сетку на каждой сделке незачем: решение от этого не изменится, а проходов
/// торгового цикла станет на два порядка больше. Поэтому сделки склеиваются в пул за
/// <c>Bybit.TickPoolMs</c>, и стратегия видит интервал цены целиком: <see cref="Low"/> и
/// <see cref="High"/> нужны, чтобы не пропустить пробой стопа или тейка внутри окна.
/// </remarks>
public sealed record Tick(
    DateTime Timestamp,
    decimal Last,
    decimal Low,
    decimal High,
    decimal Volume,
    int Trades);
