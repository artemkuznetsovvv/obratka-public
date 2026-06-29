# Build
FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
WORKDIR /src
COPY ["src/ProcessingGateway/ProcessingGateway.csproj", "src/ProcessingGateway/"]
RUN dotnet restore "src/ProcessingGateway/ProcessingGateway.csproj"
COPY . .
WORKDIR /src/src/ProcessingGateway
RUN dotnet publish -c Release -o /app/publish /p:UseAppHost=false

# Runtime — без Playwright, обычный aspnet:9.0
FROM mcr.microsoft.com/dotnet/aspnet:9.0 AS final
WORKDIR /app
COPY --from=build /app/publish .
ENV ASPNETCORE_URLS=http://+:8080
ENV ASPNETCORE_ENVIRONMENT=Production
EXPOSE 8080
ENTRYPOINT ["dotnet", "ProcessingGateway.dll"]
