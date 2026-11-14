# Keep the SDK in sync with global.json. No SDK or source code in runtime images.
# The runtime images are pinned to the latest .NET 10 servicing release (not the
# SDK's older 10.0.7 runtime) so builds are reproducible and include security
# fixes. Bump RUNTIME_VERSION with each .NET servicing release.
ARG RUNTIME_VERSION=10.0.15

FROM mcr.microsoft.com/dotnet/sdk:10.0.203 AS build
WORKDIR /source
COPY global.json Directory.Build.props Directory.Packages.props ./
COPY .config/dotnet-tools.json .config/
COPY src/el.Domain/el.Domain.csproj src/el.Domain/packages.lock.json src/el.Domain/
COPY src/el.Application/el.Application.csproj src/el.Application/packages.lock.json src/el.Application/
COPY src/el.Infrastructure/el.Infrastructure.csproj src/el.Infrastructure/packages.lock.json src/el.Infrastructure/
COPY src/el.Api/el.Api.csproj src/el.Api/packages.lock.json src/el.Api/
RUN dotnet restore src/el.Api/el.Api.csproj --locked-mode && dotnet tool restore
COPY src/ src/
RUN dotnet publish src/el.Api/el.Api.csproj --configuration Release --no-restore --output /out/api /p:UseAppHost=false
RUN dotnet ef migrations bundle --project src/el.Infrastructure --startup-project src/el.Api \
    --configuration Release --no-build --output /out/migrations/efbundle

FROM mcr.microsoft.com/dotnet/aspnet:${RUNTIME_VERSION} AS migrations
WORKDIR /app
COPY --from=build --chown=app:app /out/migrations/ ./
USER $APP_UID
ENTRYPOINT ["./efbundle"]

FROM mcr.microsoft.com/dotnet/aspnet:${RUNTIME_VERSION} AS api
WORKDIR /app
COPY --from=build --chown=app:app /out/api/ ./
USER $APP_UID
ENV ASPNETCORE_HTTP_PORTS=8080
EXPOSE 8080
ENTRYPOINT ["dotnet", "el.Api.dll"]
