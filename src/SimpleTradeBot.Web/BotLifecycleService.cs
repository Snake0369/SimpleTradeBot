using SimpleTradeBot.Core.Configuration;
using SimpleTradeBot.Core.Domain;
using SimpleTradeBot.Core.Execution;
using SimpleTradeBot.Core.Strategy;

namespace SimpleTradeBot.Web;

/// <summary>
/// Автозапуск бота и аккуратное завершение процесса.
/// </summary>
/// <remarks>
/// <para>При остановке процесса заявки снимаются, но позиция НЕ закрывается. Это сделано
/// намеренно: перезапуск контейнера или деплой — не повод закрывать позицию по рынку, а вот
/// оставить в рынке заявки, которые уже никто не сопровождает, нельзя: состояние сетки живёт в
/// памяти и после перезапуска бот о них не узнает.</para>
/// <para>Если позиция при этом осталась открытой, в лог уходит предупреждение: следующий запуск
/// на неё не встанет (<c>Bot:RequireFlatOnStart</c>), и решать, что с ней делать, должен
/// человек.</para>
/// </remarks>
public sealed class BotLifecycleService(
    GridBotEngine engine,
    IExecutionConnector execution,
    BotOptions options,
    ILogger<BotLifecycleService> logger) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (!options.AutoStart)
        {
            logger.LogInformation(
                "Бот не запущен: Bot:AutoStart = false. Запустите его из интерфейса.");
            return;
        }

        var error = await engine.StartAsync(cancellationToken).ConfigureAwait(false);
        if (error != null)
        {
            logger.LogError("Автозапуск не удался: {Error}", error);
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        var snapshot = engine.GetSnapshot();

        if (snapshot.Status is BotStatus.Stopped or BotStatus.Faulted)
        {
            await engine.DisposeAsync().ConfigureAwait(false);
            return;
        }

        logger.LogWarning("Процесс останавливается — снимаем заявки по {Symbol}.", options.Symbol);

        try
        {
            await execution.CancelAllAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Снять заявки при остановке процесса не удалось.");
        }

        if (snapshot.Position.Volume > 0m)
        {
            logger.LogWarning(
                "Позиция {Side} {Volume} по {Symbol} осталась ОТКРЫТОЙ: остановка процесса её не "
                + "закрывает. Закройте её вручную или кнопкой «Остановить» до перезапуска.",
                snapshot.Position.Side, snapshot.Position.Volume, options.Symbol);
        }

        await engine.DisposeAsync().ConfigureAwait(false);
    }
}
