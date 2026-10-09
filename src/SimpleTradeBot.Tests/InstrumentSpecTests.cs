using Xunit;
using SimpleTradeBot.Core.Domain;

namespace SimpleTradeBot.Tests;

public class InstrumentSpecTests
{
    [Fact]
    public void Знаки_после_запятой_выводятся_из_шагов()
    {
        var spec = TestFactory.Instrument(tickSize: 0.01m, quantityStep: 0.001m);

        Assert.Equal(2, spec.PriceDecimals);
        Assert.Equal(3, spec.QuantityDecimals);
    }

    [Fact]
    public void Целый_шаг_даёт_ноль_знаков()
    {
        var spec = TestFactory.Instrument(tickSize: 1m, quantityStep: 100m, minQuantity: 100m);

        Assert.Equal(0, spec.PriceDecimals);
        Assert.Equal(0, spec.QuantityDecimals);
    }

    [Fact]
    public void Покупка_округляется_вниз_продажа_вверх()
    {
        var spec = TestFactory.Instrument(tickSize: 0.5m);

        Assert.Equal(100m, spec.SnapPrice(100.4m, Side.Buy));
        Assert.Equal(100.5m, spec.SnapPrice(100.4m, Side.Sell));
    }

    [Fact]
    public void Цена_кратная_тику_не_меняется()
    {
        var spec = TestFactory.Instrument(tickSize: 0.5m);

        Assert.Equal(100.5m, spec.SnapPrice(100.5m, Side.Buy));
        Assert.Equal(100.5m, spec.SnapPrice(100.5m, Side.Sell));
    }

    [Fact]
    public void Снап_не_оставляет_мусора_в_младших_разрядах()
    {
        // Ради этого в снапе стоит Math.Round: иначе 0.1 × 3 даёт 0.30000000000000004,
        // и биржа отклоняет цену как некратную тику.
        var spec = TestFactory.Instrument(tickSize: 0.1m);

        var price = spec.SnapPrice(95_123.45m, Side.Buy);

        Assert.Equal(95_123.4m, price);
        Assert.Equal(0m, price % 0.1m);
    }

    [Fact]
    public void Объём_округляется_вниз_до_шага()
    {
        var spec = TestFactory.Instrument(quantityStep: 0.001m, minQuantity: 0.001m);

        Assert.Equal(0.123m, spec.NormalizeQuantity(0.1239m));
    }

    [Fact]
    public void Объём_меньше_минимального_лота_запрещён()
    {
        var spec = TestFactory.Instrument(quantityStep: 0.01m, minQuantity: 0.01m);

        Assert.Equal(0m, spec.NormalizeQuantity(0.009m));
        Assert.Equal(0m, spec.NormalizeQuantity(0m));
        Assert.Equal(0m, spec.NormalizeQuantity(-1m));
    }

    [Fact]
    public void Объём_больше_максимального_урезается()
    {
        var spec = new InstrumentSpec("TEST", 0.1m, 0.001m, 0.001m, 1.5m, "T", "USDT");

        Assert.Equal(1.5m, spec.NormalizeQuantity(10m));
    }
}
