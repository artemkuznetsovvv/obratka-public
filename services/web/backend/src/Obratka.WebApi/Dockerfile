# syntax=docker/dockerfile:1.7

# ---- build stage ----
FROM mcr.microsoft.com/dotnet/sdk:9.0-alpine AS build
WORKDIR /src

# csproj-ы тянем первыми, чтобы restore-слой кешировался между сборками.
COPY Obratka.sln .
COPY src/Obratka.WebApi/Obratka.WebApi.csproj                                src/Obratka.WebApi/
COPY src/Obratka.Modules.Analytics/Obratka.Modules.Analytics.csproj          src/Obratka.Modules.Analytics/
COPY src/Obratka.Modules.Reports/Obratka.Modules.Reports.csproj              src/Obratka.Modules.Reports/
COPY src/Obratka.Modules.Notifications/Obratka.Modules.Notifications.csproj  src/Obratka.Modules.Notifications/
COPY src/Obratka.Shared/Obratka.Shared.csproj                                src/Obratka.Shared/

RUN dotnet restore src/Obratka.WebApi/Obratka.WebApi.csproj

COPY . .
RUN dotnet publish src/Obratka.WebApi/Obratka.WebApi.csproj \
        -c Release -o /app/publish --no-restore /p:UseAppHost=false

# ---- runtime stage ----
FROM mcr.microsoft.com/dotnet/aspnet:9.0-alpine AS runtime
WORKDIR /app

# Образ уже содержит non-root юзера `app` (UID 64198) — переиспользуем его.
# icu-libs    — сами рантайм-библиотеки (libicuuc/libicui18n/libicudata), без них
#               .NET с DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=false фейлит на старте.
# icu-data-full — полные локали (нужно для нормализации/сравнения русских строк).
# tini        — корректный PID1 для проксирования сигналов.
RUN apk add --no-cache tini icu-libs icu-data-full
USER app

COPY --from=build --chown=app:app /app/publish .

ENV ASPNETCORE_URLS=http://+:8080 \
    DOTNET_RUNNING_IN_CONTAINER=true \
    DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=false
EXPOSE 8080
ENTRYPOINT ["/sbin/tini", "--", "dotnet", "Obratka.WebApi.dll"]
