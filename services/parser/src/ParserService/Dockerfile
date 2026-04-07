FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
WORKDIR /src
COPY src/ParserService/ParserService.csproj src/ParserService/
RUN dotnet restore src/ParserService/ParserService.csproj
COPY src/ParserService/ src/ParserService/
RUN dotnet publish src/ParserService/ParserService.csproj -c Release -o /app/publish

FROM mcr.microsoft.com/dotnet/aspnet:9.0 AS runtime
WORKDIR /app

# Playwright dependencies (uncomment when plugins need browser)
# RUN apt-get update && apt-get install -y wget && \
#     dotnet tool install --global Microsoft.Playwright.CLI && \
#     playwright install chromium --with-deps

RUN mkdir -p /data

COPY --from=build /app/publish .

ENV ASPNETCORE_URLS=http://+:8080
ENV ConnectionStrings__TasksDb="Data Source=/data/tasks.db"

EXPOSE 8080
ENTRYPOINT ["dotnet", "ParserService.dll"]
