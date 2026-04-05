FROM mcr.microsoft.com/dotnet/aspnet:9.0 AS base
WORKDIR /app

FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
WORKDIR /src
COPY ["XtremeIdiots.Portal.Server.Agent.App/XtremeIdiots.Portal.Server.Agent.App.csproj", "XtremeIdiots.Portal.Server.Agent.App/"]
RUN dotnet restore "XtremeIdiots.Portal.Server.Agent.App/XtremeIdiots.Portal.Server.Agent.App.csproj"
COPY . .
WORKDIR "/src/XtremeIdiots.Portal.Server.Agent.App"
RUN dotnet build "XtremeIdiots.Portal.Server.Agent.App.csproj" -c Release -o /app/build

FROM build AS publish
RUN dotnet publish "XtremeIdiots.Portal.Server.Agent.App.csproj" -c Release -o /app/publish /p:UseAppHost=false

FROM base AS final
WORKDIR /app
COPY --from=publish /app/publish .
ENTRYPOINT ["dotnet", "XtremeIdiots.Portal.Server.Agent.App.dll"]
