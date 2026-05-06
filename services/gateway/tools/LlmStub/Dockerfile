# Build
FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
WORKDIR /src
COPY ["tools/LlmStub/ProcessingGateway.LlmStub.csproj", "tools/LlmStub/"]
COPY ["src/ProcessingGateway/ProcessingGateway.csproj", "src/ProcessingGateway/"]
RUN dotnet restore "tools/LlmStub/ProcessingGateway.LlmStub.csproj"
COPY . .
WORKDIR /src/tools/LlmStub
RUN dotnet publish -c Release -o /app/publish /p:UseAppHost=false

# Runtime: aspnet:9.0 (а не runtime:9.0) — стуб через ProjectReference на основной
# web-проект тянет `Microsoft.AspNetCore.App` framework, поэтому нужен aspnet-runtime.
FROM mcr.microsoft.com/dotnet/aspnet:9.0 AS final
WORKDIR /app
COPY --from=build /app/publish .
ENV DOTNET_ENVIRONMENT=Production
ENTRYPOINT ["dotnet", "ProcessingGateway.LlmStub.dll"]
