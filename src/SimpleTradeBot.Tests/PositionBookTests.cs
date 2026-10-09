using SimpleTradeBot.Core.Domain;
using Xunit;

namespace SimpleTradeBot.Tests;

public class PositionBookTests
{
    [Fact]
    public void Первая_сделка_открывает_позицию()
    {
        var book = new PositionBook();

        var pnl = book.Apply(Side.Buy, 1_000m, 0.5m, fee: 0.1m);

        Assert.Equal(0m, pnl);
        Assert.Equal(Side.Buy, book.Side);
        Assert.Equal(0.5m, book.Volume);
        Assert.Equal(1_000m, book.AveragePrice);
        Assert.Equal(0.1m, book.Fees);
    }

    [Fact]
    public void Добор_пересчитывает_среднюю_цену_по_объёму()
    {
        var book = new PositionBook();

        book.Apply(Side.Buy, 1_000m, 1m, 0m);
        book.Apply(Side.Buy, 900m, 3m, 0m);

        Assert.Equal(4m, book.Volume);
        Assert.Equal(925m, book.AveragePrice);
    }

    [Fact]
    public void Встречная_сделка_реализует_результат_по_закрытому_объёму()
    {
        var book = new PositionBook();
        book.Apply(Side.Buy, 1_000m, 2m, 0m);

        var pnl = book.Apply(Side.Sell, 1_010m, 1m, 0m);

        Assert.Equal(10m, pnl);
        Assert.Equal(10m, book.RealizedPnl);
        Assert.Equal(1m, book.Volume);
        // Средняя цена остатка не меняется при частичном закрытии.
        Assert.Equal(1_000m, book.AveragePrice);
    }

    [Fact]
    public void Шорт_зарабатывает_на_падении()
    {
        var book = new PositionBook();
        book.Apply(Side.Sell, 1_000m, 1m, 0m);

        var pnl = book.Apply(Side.Buy, 950m, 1m, 0m);

        Assert.Equal(50m, pnl);
        Assert.True(book.IsFlat);
        Assert.Null(book.Side);
    }

    [Fact]
    public void Полное_закрытие_обнуляет_позицию_и_сохраняет_результат()
    {
        var book = new PositionBook();
        book.Apply(Side.Buy, 1_000m, 1m, 0.5m);
        book.Apply(Side.Sell, 1_100m, 1m, 0.5m);

        Assert.True(book.IsFlat);
        Assert.Equal(100m, book.RealizedPnl);
        Assert.Equal(1m, book.Fees);
        Assert.Equal(0m, book.SignedVolume);
    }

    [Fact]
    public void Переворот_открывает_позицию_остатком()
    {
        var book = new PositionBook();
        book.Apply(Side.Buy, 1_000m, 1m, 0m);

        var pnl = book.Apply(Side.Sell, 1_010m, 3m, 0m);

        Assert.Equal(10m, pnl);
        Assert.Equal(Side.Sell, book.Side);
        Assert.Equal(2m, book.Volume);
        Assert.Equal(1_010m, book.AveragePrice);
    }

    [Fact]
    public void Нереализованный_результат_считается_по_текущей_цене()
    {
        var book = new PositionBook();
        book.Apply(Side.Buy, 1_000m, 2m, 0m);

        Assert.Equal(40m, book.UnrealizedPnl(1_020m));
        Assert.Equal(-40m, book.UnrealizedPnl(980m));
        Assert.Equal(0m, book.UnrealizedPnl(0m));
    }

    [Fact]
    public void Ребейт_мейкера_уменьшает_комиссии()
    {
        var book = new PositionBook();

        book.Apply(Side.Buy, 1_000m, 1m, fee: 0.5m);
        book.Apply(Side.Buy, 1_000m, 1m, fee: -0.2m);

        Assert.Equal(0.3m, book.Fees);
    }

    [Fact]
    public void Нетто_со_знаком_отражает_сторону()
    {
        var book = new PositionBook();

        book.Apply(Side.Sell, 1_000m, 1.5m, 0m);

        Assert.Equal(-1.5m, book.SignedVolume);
    }

    [Fact]
    public void MarkFlat_закрывает_позицию_но_оставляет_результат()
    {
        var book = new PositionBook();
        book.Apply(Side.Buy, 1_000m, 1m, 0m);
        book.Apply(Side.Sell, 1_100m, 0.5m, 0m);

        book.MarkFlat();

        Assert.True(book.IsFlat);
        Assert.Equal(50m, book.RealizedPnl);
    }

    [Fact]
    public void Нулевой_объём_игнорируется()
    {
        var book = new PositionBook();

        Assert.Equal(0m, book.Apply(Side.Buy, 1_000m, 0m, 1m));
        Assert.True(book.IsFlat);
        Assert.Equal(0, book.Fills);
        Assert.Equal(0m, book.Fees);
    }
}
