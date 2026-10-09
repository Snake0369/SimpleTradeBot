using Bybit.Net;
using Bybit.Net.Clients;
using Bybit.Net.Objects;
using SimpleTradeBot.Core.Configuration;

namespace SimpleTradeBot.Bybit;

/// <summary>
/// Сборка клиентов Bybit по настройкам.
/// </summary>
/// <remarks>
/// <para>Рыночные данные и торговля берутся из РАЗНЫХ площадок, и это не недосмотр.
/// Демо-торговля Bybit (<c>api-demo.bybit.com</c>) обслуживает только счёт и заявки, публичных
/// рыночных эндпоинтов у неё нет вовсе: котировки демо-счёта — это котировки настоящей биржи.
/// Поэтому фид и справочник инструментов всегда смотрят на боевую площадку, а заявки уходят
/// туда, куда указано в настройках.</para>
/// <para>Testnet — исключение: у него свой, довольно рваный рынок, и смотреть на боевые цены,
/// торгуя на testnet, было бы бессмысленно.</para>
/// </remarks>
public static class BybitClientFactory
{
    /// <summary>Площадка для публичных данных: цены, справочник инструментов.</summary>
    public static BybitEnvironment PublicEnvironment(BybitOptions options)
        => options.Environment == BybitEnvironmentKind.Testnet
            ? BybitEnvironment.Testnet
            : BybitEnvironment.Live;

    /// <summary>Площадка для заявок и счёта.</summary>
    public static BybitEnvironment TradingEnvironment(BybitOptions options) => options.Environment switch
    {
        BybitEnvironmentKind.Testnet => BybitEnvironment.Testnet,
        BybitEnvironmentKind.Live => BybitEnvironment.Live,
        _ => BybitEnvironment.DemoTrading
    };

    /// <summary>Человекочитаемое имя площадки для интерфейса.</summary>
    public static string EnvironmentLabel(BybitOptions options) => options.Environment switch
    {
        BybitEnvironmentKind.Testnet => "Testnet",
        BybitEnvironmentKind.Live => "Live",
        _ => "Demo Trading"
    };

    /// <summary>REST-клиент без ключей — только публичные данные.</summary>
    public static BybitRestClient CreatePublicRest(BybitOptions options)
        => new(o => o.Environment = PublicEnvironment(options));

    /// <summary>REST-клиент с ключами — заявки и счёт.</summary>
    public static BybitRestClient CreateTradingRest(BybitOptions options)
        => new(o =>
        {
            o.Environment = TradingEnvironment(options);
            if (options.HasCredentials)
            {
                o.ApiCredentials = new BybitCredentials(options.ApiKey, options.ApiSecret);
            }
        });

    /// <summary>Сокет для публичного потока сделок.</summary>
    public static BybitSocketClient CreatePublicSocket(BybitOptions options)
        => new(o => o.Environment = PublicEnvironment(options));

    /// <summary>Сокет для приватного потока заявок и сделок пользователя.</summary>
    public static BybitSocketClient CreateTradingSocket(BybitOptions options)
        => new(o =>
        {
            o.Environment = TradingEnvironment(options);
            if (options.HasCredentials)
            {
                o.ApiCredentials = new BybitCredentials(options.ApiKey, options.ApiSecret);
            }
        });
}
