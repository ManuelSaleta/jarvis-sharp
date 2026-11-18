# =============================================================================
# Jarvis.Sharp - Multi-Stage Container Build
# =============================================================================

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Copy solution and project definitions for optimal layer caching
COPY Jarvis.Sharp.slnx .
COPY src/Jarvis.Sharp/Jarvis.Sharp.csproj src/Jarvis.Sharp/
COPY tests/Jarvis.Sharp.Tests/Jarvis.Sharp.Tests.csproj tests/Jarvis.Sharp.Tests/

RUN dotnet restore Jarvis.Sharp.slnx

# Copy source tree and execute tests during build
COPY src/ src/
COPY tests/ tests/

RUN dotnet test tests/Jarvis.Sharp.Tests/Jarvis.Sharp.Tests.csproj -c Release --no-restore
RUN dotnet publish src/Jarvis.Sharp/Jarvis.Sharp.csproj -c Release -o /app/publish --no-restore
RUN curl -fsSL https://antigravity.google/cli/install.sh | bash -s -- --dir /usr/local/bin
RUN mkdir -p /state /home/app/.gemini /workspace

# =============================================================================
# Minimal Runtime Stage (Ubuntu Noble Chiseled - Non-Root)
# =============================================================================
FROM mcr.microsoft.com/dotnet/runtime:10.0-noble-chiseled AS final
WORKDIR /app

COPY --from=build /app/publish .
COPY --from=build /usr/local/bin/agy /usr/local/bin/agy
COPY --from=build --chown=$APP_UID:$APP_UID /state /state
COPY --from=build --chown=$APP_UID:$APP_UID /home/app /home/app
COPY --from=build --chown=$APP_UID:$APP_UID /workspace /workspace

# Use non-root user built into chiseled images
USER $APP_UID

ENV HOME=/home/app \
    AGY_BINARY=/usr/local/bin/agy \
    AGY_WORKSPACE=/workspace \
    DOTNET_EnableDiagnostics=0 \
    DOTNET_CLI_TELEMETRY_OPTOUT=1

ENTRYPOINT ["dotnet", "jarvis-sharp.dll"]
