FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS base
WORKDIR /app
EXPOSE 8080
EXPOSE 8081

FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
ARG BUILD_CONFIGURATION=Release
WORKDIR /src
COPY ["src/AzureDiscovery.Api/AzureDiscovery.Api.csproj", "src/AzureDiscovery.Api/"]
COPY ["src/AzureDiscovery.Core/AzureDiscovery.Core.csproj", "src/AzureDiscovery.Core/"]
COPY ["src/AzureDiscovery.Infrastructure/AzureDiscovery.Infrastructure.csproj", "src/AzureDiscovery.Infrastructure/"]
RUN dotnet restore "src/AzureDiscovery.Api/AzureDiscovery.Api.csproj"
COPY . .
WORKDIR "/src/src/AzureDiscovery.Api"
RUN dotnet build "AzureDiscovery.Api.csproj" -c $BUILD_CONFIGURATION -o /app/build

FROM build AS publish
ARG BUILD_CONFIGURATION=Release
RUN dotnet publish "AzureDiscovery.Api.csproj" -c $BUILD_CONFIGURATION -o /app/publish /p:UseAppHost=false

FROM base AS final
WORKDIR /app
COPY --from=publish /app/publish .
ENTRYPOINT ["dotnet", "AzureDiscovery.Api.dll"]
