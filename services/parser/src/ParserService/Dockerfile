# syntax=docker/dockerfile:1.7

# ===== Build stage =====
FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
WORKDIR /src

COPY ["src/ParserService/ParserService.csproj", "src/ParserService/"]
RUN dotnet restore "src/ParserService/ParserService.csproj"

COPY src/ParserService/ src/ParserService/
WORKDIR /src/src/ParserService
RUN dotnet publish -c Release -o /app/publish /p:UseAppHost=false

# ===== Runtime stage =====
# playwright/dotnet содержит Chrome + все системные зависимости (libnss, libasound и т.д.)
# версия образа должна совпадать с Microsoft.Playwright NuGet (см. ParserService.csproj)
# Образ v1.58 пока поставляется с .NET 8 runtime — доставляем ASP.NET Core 9 отдельно.
FROM mcr.microsoft.com/playwright/dotnet:v1.58.0-noble AS final
WORKDIR /app

RUN apt-get update \
 && apt-get install -y --no-install-recommends curl ca-certificates \
 && curl -sSL https://dot.net/v1/dotnet-install.sh -o /tmp/dotnet-install.sh \
 && chmod +x /tmp/dotnet-install.sh \
 && /tmp/dotnet-install.sh --channel 9.0 --runtime aspnetcore --install-dir /usr/share/dotnet \
 && rm /tmp/dotnet-install.sh \
 && rm -rf /var/lib/apt/lists/*

COPY --from=build /app/publish .

ENV ASPNETCORE_URLS=http://+:8080
ENV ASPNETCORE_ENVIRONMENT=Production
ENV DOTNET_EnableDiagnostics=0

EXPOSE 8080
ENTRYPOINT ["dotnet", "ParserService.dll"]
