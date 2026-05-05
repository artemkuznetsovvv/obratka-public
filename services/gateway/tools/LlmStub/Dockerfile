# Build
FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
WORKDIR /src
COPY ["tools/LlmStub/ProcessingGateway.LlmStub.csproj", "tools/LlmStub/"]
COPY ["src/ProcessingGateway/ProcessingGateway.csproj", "src/ProcessingGateway/"]
RUN dotnet restore "tools/LlmStub/ProcessingGateway.LlmStub.csproj"
COPY . .
WORKDIR /src/tools/LlmStub
RUN dotnet publish -c Release -o /app/publish /p:UseAppHost=false

# Runtime — без HTTP сервера, чисто worker.
FROM mcr.microsoft.com/dotnet/runtime:9.0 AS final
WORKDIR /app
COPY --from=build /app/publish .
ENV DOTNET_ENVIRONMENT=Production
ENTRYPOINT ["dotnet", "ProcessingGateway.LlmStub.dll"]
