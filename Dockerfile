# CopilotScope — kolektor OTLP + dashboard w jednym kontenerze.
# Build:  docker build -t copilotscope .
# Run:    docker run -p 4318:4318 -e CopilotScope__Ingest__ApiKey=<sekret> copilotscope

# The two versions differ on purpose and must stay in step with the projects:
# every csproj targets net8.0, so the runtime image has to carry the 8.0 shared
# framework — a net8.0 app will not start on aspnet:10.0, because the default
# roll-forward policy does not cross a major version. The SDK is 9.0 to match
# CONTRIBUTING.md ("build with the 9.0 SDK; everything targets net8.0").
# Do not let a major-version bump of either image land without retargeting the
# projects first; Dependabot is configured to ignore those (.github/dependabot.yml).
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY nuget.config .
COPY src/CopilotScope.Collector/ src/CopilotScope.Collector/
RUN dotnet publish src/CopilotScope.Collector -c Release -o /app/publish

FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS runtime
WORKDIR /app
COPY --from=build /app/publish .

ENV ASPNETCORE_ENVIRONMENT=Production
ENV ASPNETCORE_URLS=http://0.0.0.0:4318
EXPOSE 4318

# Healthcheck na wbudowany endpoint
HEALTHCHECK --interval=30s --timeout=5s --start-period=10s \
  CMD wget -qO- http://localhost:4318/api/health || exit 1

ENTRYPOINT ["dotnet", "CopilotScope.Collector.dll"]
