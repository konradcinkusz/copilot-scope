# CopilotScope — OTLP collector in one container.
# Build:  docker build -t copilotscope .
# Run:    docker run -p 4318:4318 -e CopilotScope__Ingest__ApiKey=<secret> copilotscope
#
# The image major version tracks the TFM: every csproj targets net10.0, so both
# the SDK build image and the aspnet runtime image are 10.0. A runtime image on a
# different major than the TFM fails at startup — the default roll-forward policy
# does not cross a major version — so these must stay in step with the projects.
# Dependabot is configured to ignore major bumps of either image
# (.github/dependabot.yml) until the projects are retargeted first, and the CI
# smoke test (build-containers.yml) now catches a TFM/runtime mismatch before publish.
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY nuget.config .
# Restore against the project file first so a source-only change keeps the
# restore layer cached.
COPY src/CopilotScope.Collector/*.csproj src/CopilotScope.Collector/
RUN dotnet restore src/CopilotScope.Collector
COPY src/CopilotScope.Collector/ src/CopilotScope.Collector/
RUN dotnet publish src/CopilotScope.Collector -c Release -o /app/publish --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
LABEL org.opencontainers.image.source=https://github.com/konradcinkusz/copilot-scope
WORKDIR /app
# curl for the container healthcheck — the aspnet image ships neither curl nor wget.
RUN apt-get update \
 && apt-get install -y --no-install-recommends curl \
 && rm -rf /var/lib/apt/lists/*
COPY --from=build /app/publish .

ENV ASPNETCORE_ENVIRONMENT=Production
ENV ASPNETCORE_URLS=http://0.0.0.0:4318
EXPOSE 4318

# Run as the non-root user the aspnet base image provides.
USER $APP_UID

HEALTHCHECK --interval=30s --timeout=5s --start-period=10s \
  CMD curl -fsS http://localhost:4318/api/health || exit 1

ENTRYPOINT ["dotnet", "CopilotScope.Collector.dll"]
