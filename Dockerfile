# ───────────────────────────── сборка ─────────────────────────────
FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
WORKDIR /src

# Сначала только файлы проектов: пока они не менялись, слой с restore берётся из кэша,
# и пересборка после правки кода не тянет пакеты заново.
COPY nuget.config ./
COPY src/Directory.Build.props ./
COPY src/SimpleTradeBot.Core/SimpleTradeBot.Core.csproj SimpleTradeBot.Core/
COPY src/SimpleTradeBot.Bybit/SimpleTradeBot.Bybit.csproj SimpleTradeBot.Bybit/
COPY src/SimpleTradeBot.Web/SimpleTradeBot.Web.csproj SimpleTradeBot.Web/
RUN dotnet restore SimpleTradeBot.Web/SimpleTradeBot.Web.csproj

COPY src/ ./
RUN dotnet publish SimpleTradeBot.Web/SimpleTradeBot.Web.csproj \
    --configuration Release --no-restore --output /app

# ───────────────────────────── запуск ─────────────────────────────
FROM mcr.microsoft.com/dotnet/aspnet:9.0 AS runtime
WORKDIR /app
COPY --from=build /app ./

ENV ASPNETCORE_URLS=http://+:8080 \
    DOTNET_gcServer=0

EXPOSE 8080

# Непривилегированный пользователь из базового образа: боту некуда и незачем писать на диск,
# всё состояние живёт в памяти процесса.
USER $APP_UID

ENTRYPOINT ["dotnet", "SimpleTradeBot.Web.dll"]
