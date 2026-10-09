namespace SimpleTradeBot.Core.Configuration;

/// <summary>Куда подключаться на Bybit.</summary>
public enum BybitEnvironmentKind
{
    /// <summary>Демо-торговля: реальный рынок, виртуальные деньги. Свои ключи, раздел Demo Trading.</summary>
    DemoTrading = 0,

    /// <summary>Testnet: отдельная тестовая биржа со своим, довольно рваным рынком.</summary>
    Testnet = 1,

    /// <summary>Реальные деньги.</summary>
    Live = 2
}

/// <summary>Настройки подключения к Bybit (секция <c>Bybit</c> в appsettings.json).</summary>
public sealed class BybitOptions
{
    public const string SectionName = "Bybit";

    /// <summary>
    /// Площадка. По умолчанию демо-торговля: рынок настоящий, деньги — нет. Переключать на Live
    /// стоит только после того, как стратегия отработала здесь.
    /// </summary>
    public BybitEnvironmentKind Environment { get; set; } = BybitEnvironmentKind.DemoTrading;

    /// <summary>
    /// Ключ API. Держать его в appsettings.json не нужно: заберётся из переменной окружения
    /// <c>Bybit__ApiKey</c> или из <c>dotnet user-secrets</c>, они перекрывают файл.
    /// </summary>
    public string ApiKey { get; set; } = string.Empty;

    /// <summary>Секрет API. Про хранение — см. <see cref="ApiKey"/>.</summary>
    public string ApiSecret { get; set; } = string.Empty;

    /// <summary>
    /// Плечо. Выставляется на инструмент при запуске. 1 — торгуем на своё, самый осторожный
    /// вариант и единственный разумный для сетки с мартингейлом.
    /// </summary>
    public decimal Leverage { get; set; } = 1m;

    /// <summary>Окно склейки сделок в один пул, мс. См. <see cref="Domain.Tick"/>.</summary>
    public int TickPoolMs { get; set; } = 300;

    /// <summary>Есть ли ключи. Без них доступны только публичные данные — торговать нельзя.</summary>
    public bool HasCredentials
        => !string.IsNullOrWhiteSpace(ApiKey) && !string.IsNullOrWhiteSpace(ApiSecret);
}
