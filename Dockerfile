FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
ARG VERSION=0.0.0-local
ARG REVISION=unknown
WORKDIR /source
COPY global.json Directory.Build.props NuGet.config ./
COPY src/Auth0Mock/Auth0Mock.csproj src/Auth0Mock/packages.lock.json src/Auth0Mock/
RUN dotnet restore src/Auth0Mock/Auth0Mock.csproj --locked-mode
COPY src/Auth0Mock/ src/Auth0Mock/
RUN dotnet publish src/Auth0Mock/Auth0Mock.csproj --configuration Release --no-restore \
    --output /app --property:UseAppHost=false --property:Version=$VERSION \
    --property:SourceRevisionId=$REVISION

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
ARG VERSION=0.0.0-local
ARG REVISION=unknown
LABEL org.opencontainers.image.title="Auth0Mock" \
    org.opencontainers.image.description="Development-only Auth0-compatible email identity mock" \
    org.opencontainers.image.licenses="MIT" \
    org.opencontainers.image.source="https://github.com/Jouda5/Auth0Mock" \
    org.opencontainers.image.version=$VERSION \
    org.opencontainers.image.revision=$REVISION
WORKDIR /app
COPY --from=build /app/ ./
COPY LICENSE ./
ENV ASPNETCORE_ENVIRONMENT=Development \
    ASPNETCORE_HTTP_PORTS=8080 \
    ASPNETCORE_HTTPS_PORTS=7443 \
    AUTH0MOCK_HEALTH_URL=http://127.0.0.1:8080/health \
    DOTNET_EnableDiagnostics=0
EXPOSE 7443
USER $APP_UID
HEALTHCHECK --interval=10s --timeout=5s --start-period=10s --retries=3 \
    CMD ["dotnet", "Auth0Mock.dll", "--health-check"]
ENTRYPOINT ["dotnet", "Auth0Mock.dll"]
