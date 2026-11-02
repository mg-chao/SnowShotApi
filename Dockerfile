FROM mcr.microsoft.com/dotnet/sdk:10.0@sha256:72dd743782f2ae7e5476fd64f6a460045e3998dc862218b80e6944cba79a01b0 AS build
WORKDIR /source
COPY global.json Directory.Build.props Directory.Packages.props SnowShotApi.sln ./
COPY src/SnowShot.Contracts/SnowShot.Contracts.csproj src/SnowShot.Contracts/packages.lock.json src/SnowShot.Contracts/
COPY src/SnowShot.Domain/SnowShot.Domain.csproj src/SnowShot.Domain/packages.lock.json src/SnowShot.Domain/
COPY src/SnowShot.Application/SnowShot.Application.csproj src/SnowShot.Application/packages.lock.json src/SnowShot.Application/
COPY src/SnowShot.ApiAdapter/SnowShot.ApiAdapter.csproj src/SnowShot.ApiAdapter/packages.lock.json src/SnowShot.ApiAdapter/
COPY src/SnowShot.Infrastructure/SnowShot.Infrastructure.csproj src/SnowShot.Infrastructure/packages.lock.json src/SnowShot.Infrastructure/
COPY src/SnowShot.DatabaseMigrator/SnowShot.DatabaseMigrator.csproj src/SnowShot.DatabaseMigrator/packages.lock.json src/SnowShot.DatabaseMigrator/
COPY src/SnowShotApi/SnowShotApi.csproj src/SnowShotApi/packages.lock.json src/SnowShotApi/
COPY tests/SnowShotApi.Tests/SnowShotApi.Tests.csproj tests/SnowShotApi.Tests/packages.lock.json tests/SnowShotApi.Tests/
RUN dotnet restore SnowShotApi.sln --locked-mode
COPY src/ src/
RUN dotnet publish src/SnowShotApi/SnowShotApi.csproj -c Release --no-restore -o /app
RUN dotnet publish src/SnowShot.DatabaseMigrator/SnowShot.DatabaseMigrator.csproj -c Release --no-restore -o /migrator

FROM mcr.microsoft.com/dotnet/aspnet:10.0-azurelinux3.0@sha256:5f261110c876eb9db148e99482117090767a0b67e98d813cfa1a4aab1f85b230 AS runtime-base
WORKDIR /app
USER $APP_UID

FROM runtime-base AS migrator
COPY --from=build /migrator .
ENTRYPOINT ["dotnet", "SnowShot.DatabaseMigrator.dll"]

FROM runtime-base AS api
COPY --from=build /app .
ENV ASPNETCORE_HTTP_PORTS=8080
EXPOSE 8080
ENTRYPOINT ["dotnet", "SnowShotApi.dll"]
