FROM mcr.microsoft.com/dotnet/runtime:8.0 AS base
WORKDIR /app

FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
ARG BUILD_CONFIGURATION=Release
WORKDIR /src
COPY ["src/AzureDiscovery.Worker/AzureDiscovery.Worker.csproj", "src/AzureDiscovery.Worker/"]
COPY ["src/AzureDiscovery.Core/AzureDiscovery.Core.csproj", "src/AzureDiscovery.Core/"]
COPY ["src/AzureDiscovery.Infrastructure/AzureDiscovery.Infrastructure.csproj", "src/AzureDiscovery.Infrastructure/"]
RUN dotnet restore "src/AzureDiscovery.Worker/AzureDiscovery.Worker.csproj"
COPY . .
WORKDIR "/src/src/AzureDiscovery.Worker"
RUN dotnet build "AzureDiscovery.Worker.csproj" -c $BUILD_CONFIGURATION -o /app/build

FROM build AS publish
ARG BUILD_CONFIGURATION=Release
RUN dotnet publish "AzureDiscovery.Worker.csproj" -c $BUILD_CONFIGURATION -o /app/publish /p:UseAppHost=false

FROM base AS final
WORKDIR /app
COPY --from=publish /app/publish .
ENTRYPOINT ["dotnet", "AzureDiscovery.Worker.dll"]
