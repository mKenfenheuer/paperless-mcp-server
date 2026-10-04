# syntax=docker/dockerfile:1

# Build on the native platform of the builder and cross-compile for the target architecture.
FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:10.0 AS build
ARG TARGETARCH
ARG VERSION=0.0.0-dev
WORKDIR /src

COPY src/PaperlessMcpServer/PaperlessMcpServer.csproj src/PaperlessMcpServer/
RUN dotnet restore src/PaperlessMcpServer/PaperlessMcpServer.csproj -a $TARGETARCH

COPY src/ src/
RUN dotnet publish src/PaperlessMcpServer/PaperlessMcpServer.csproj \
    -c Release -a $TARGETARCH --no-restore -o /app \
    -p:Version=$VERSION -p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0-alpine
WORKDIR /app
COPY --from=build /app .

ENV ASPNETCORE_HTTP_PORTS=8080 \
    DOTNET_NOLOGO=1
EXPOSE 8080
USER $APP_UID

HEALTHCHECK --interval=30s --timeout=5s --start-period=10s --retries=3 \
    CMD wget -qO- http://127.0.0.1:8080/healthz || exit 1

ENTRYPOINT ["dotnet", "PaperlessMcpServer.dll"]
