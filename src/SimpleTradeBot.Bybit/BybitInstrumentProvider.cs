using Bybit.Net.Enums;
using Microsoft.Extensions.Logging;
using SimpleTradeBot.Core.Configuration;
using SimpleTradeBot.Core.Domain;
using SimpleTradeBot.Core.MarketData;

namespace SimpleTradeBot.Bybit;

/// <summary>
/// Спецификация USDT-перпетуала Bybit: шаг цены, шаг объёма, минимальный и максимальный объём.
/// </summary>
/// <remarks>
/// <para>Эндпоинт публичный, ключей не требует. В исходном проекте тем же занимался
/// <c>BybitInstrumentSync</c>, только он заводил все шестьсот перпетуалов в таблицу контрактов —
/// здесь инструмент один и живёт в памяти, поэтому и запрос один, поимённый.</para>
/// <para>Фильтр по типу контракта обязателен: в категории <c>linear</c> лежат ещё и срочные
/// контракты с поставкой (<c>BTC-26DEC25</c>) — у них есть дата экспирации, и вести их как
/// перпетуал нельзя.</para>
/// </remarks>
public sealed class BybitInstrumentProvider(BybitOptions options, ILogger<BybitInstrumentProvider> logger)
    : IInstrumentProvider
{
    public async Task<InstrumentSpec?> GetAsync(string symbol, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(symbol)) return null;

        using var client = BybitClientFactory.CreatePublicRest(options);

        var result = await client.V5Api.ExchangeData
            .GetLinearInverseSymbolsAsync(Category.Linear, symbol, ct: ct)
            .ConfigureAwait(false);

        if (!result.Success)
        {
            logger.LogError("Bybit не отдал справочник по {Symbol}: {Error}", symbol, result.Error);
            return null;
        }

        var info = result.Data.List.FirstOrDefault(s =>
            string.Equals(s.Name, symbol, StringComparison.OrdinalIgnoreCase));

        if (info == null)
        {
            logger.LogError("Инструмента {Symbol} нет среди инструментов категории linear.", symbol);
            return null;
        }

        if (info.ContractType != ContractTypeV5.LinearPerpetual)
        {
            logger.LogError(
                "{Symbol} — это {Type}, а не USDT-перпетуал. Срочные контракты с поставкой бот не ведёт.",
                symbol, info.ContractType);
            return null;
        }

        if (info.Status != SymbolStatus.Trading)
        {
            logger.LogError("{Symbol} не торгуется (статус {Status}).", symbol, info.Status);
            return null;
        }

        var tickSize = info.PriceFilter?.TickSize ?? 0m;
        var quantityStep = info.LotSizeFilter?.QuantityStep ?? 0m;

        if (tickSize <= 0m || quantityStep <= 0m)
        {
            logger.LogError(
                "{Symbol}: биржа вернула шаг цены {Tick} и шаг объёма {Step} — торговать таким контрактом нельзя.",
                symbol, tickSize, quantityStep);
            return null;
        }

        return new InstrumentSpec(
            info.Name,
            tickSize,
            quantityStep,
            info.LotSizeFilter?.MinOrderQuantity ?? quantityStep,
            info.LotSizeFilter?.MaxOrderQuantity ?? 0m,
            info.BaseAsset ?? info.Name,
            info.QuoteAsset ?? "USDT");
    }
}
