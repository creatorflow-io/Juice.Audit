# The audit collector image. Build context: the repository root.
#   docker build -f src/Juice.Audit.Collector/Dockerfile -t juice-audit-collector .
#
# Juice packages resolve to released versions from nuget.org. To build against the alpha packages on the GitHub feed
# (what a local build uses), pass JUICE_PACKAGES=alpha and the feed's credentials as build secrets:
#   docker build -f src/Juice.Audit.Collector/Dockerfile --build-arg JUICE_PACKAGES=alpha \
#     --secret id=gh_user,env=GITHUB_PACKAGE_USERNAME --secret id=gh_token,env=GITHUB_PACKAGE_TOKEN -t juice-audit-collector .
FROM mcr.microsoft.com/dotnet/sdk:10.0.402 AS build
ARG JUICE_PACKAGES=released
WORKDIR /src

# Restore first, from the project files alone, so a code change doesn't download the packages again.
COPY Directory.Build.props nuget.config ./
COPY src/Juice.Audit/Juice.Audit.csproj src/Juice.Audit/
COPY src/Juice.Audit.Api/Juice.Audit.Api.csproj src/Juice.Audit.Api/
COPY src/Juice.Audit.Api.Contracts/Juice.Audit.Api.Contracts.csproj src/Juice.Audit.Api.Contracts/
COPY src/Juice.Audit.EF/Juice.Audit.EF.csproj src/Juice.Audit.EF/
COPY src/Juice.Audit.EF.PostgreSQL/Juice.Audit.EF.PostgreSQL.csproj src/Juice.Audit.EF.PostgreSQL/
COPY src/Juice.Audit.EF.SqlServer/Juice.Audit.EF.SqlServer.csproj src/Juice.Audit.EF.SqlServer/
COPY src/Juice.Audit.Messaging/Juice.Audit.Messaging.csproj src/Juice.Audit.Messaging/
COPY src/Juice.Audit.Collector/Juice.Audit.Collector.csproj src/Juice.Audit.Collector/

# Released packages: CI=true drops the "-alpha.*" suffix Directory.Build.props puts on the Juice versions, and nuget.org
# is the only feed (NuGet refuses the GitHub feed's empty credentials). Only net10.0 is built.
ENV BUILD_PROPS="-p:SolutionDir=/src/ -p:AppTargetFramework=net10.0"
RUN --mount=type=secret,id=gh_user,required=false --mount=type=secret,id=gh_token,required=false \
    if [ "$JUICE_PACKAGES" = "alpha" ]; then \
      [ -s /run/secrets/gh_token ] || { echo "JUICE_PACKAGES=alpha needs the gh_user and gh_token secrets" >&2; exit 1; }; \
      export GITHUB_PACKAGE_USERNAME="$(cat /run/secrets/gh_user)" GITHUB_PACKAGE_TOKEN="$(cat /run/secrets/gh_token)"; \
      echo "$BUILD_PROPS" > /tmp/props; \
    else \
      printf '<?xml version="1.0" encoding="utf-8"?>\n<configuration><packageSources><clear /><add key="nuget.org" value="https://api.nuget.org/v3/index.json" protocolVersion="3" /></packageSources></configuration>\n' > nuget.config; \
      echo "$BUILD_PROPS -p:CI=true" > /tmp/props; \
    fi; \
    dotnet restore src/Juice.Audit.Collector/Juice.Audit.Collector.csproj $(cat /tmp/props)

COPY src/ src/
RUN dotnet publish src/Juice.Audit.Collector/Juice.Audit.Collector.csproj -c Release --no-restore \
      $(cat /tmp/props) -o /app

FROM mcr.microsoft.com/dotnet/aspnet:10.0.9-alpine3.24
# icu-libs: culture-aware text in the records; krb5-libs: Npgsql probes for GSSAPI at start and logs an error without it.
RUN apk add --no-cache icu-libs krb5-libs
# gRPC on 8080, HTTP/2 only: without TLS a client can't negotiate HTTP/2, so the port speaks nothing else.
# Health checks and the read API on 8081, HTTP/1.1. ASPNETCORE_HTTP_PORTS (8080 in the base image) is cleared: the
# endpoints replace it.
ENV ASPNETCORE_HTTP_PORTS= \
    DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=false \
    Kestrel__Endpoints__Grpc__Url=http://+:8080 \
    Kestrel__Endpoints__Grpc__Protocols=Http2 \
    Kestrel__Endpoints__Http__Url=http://+:8081 \
    Kestrel__Endpoints__Http__Protocols=Http1
WORKDIR /app
COPY --from=build /app .
USER $APP_UID
EXPOSE 8080 8081
HEALTHCHECK --interval=5s --timeout=3s --start-period=10s --retries=12 \
  CMD wget -q -O /dev/null http://127.0.0.1:8081/health/ready || exit 1
ENTRYPOINT ["dotnet", "Juice.Audit.Collector.dll"]
