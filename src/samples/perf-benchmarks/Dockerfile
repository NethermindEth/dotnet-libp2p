FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY global.json Directory.Build.props ./
COPY src ./src
RUN dotnet publish src/samples/perf-benchmarks/PerfBenchmarks.csproj -c Release -o /app/publish \
    -p:SaveDiskSpace=true -p:EnableSourceLink=false -p:EnableSourceControlManagerQueries=false -p:TreatWarningsAsErrors=true

FROM mcr.microsoft.com/dotnet/aspnet:10.0
RUN apt-get update && apt-get install -y --no-install-recommends curl && \
    curl -fsSL -o /tmp/packages-microsoft-prod.deb https://packages.microsoft.com/config/ubuntu/24.04/packages-microsoft-prod.deb && \
    dpkg -i /tmp/packages-microsoft-prod.deb && \
    apt-get update && apt-get install -y --no-install-recommends libmsquic && \
    rm -rf /var/lib/apt/lists/* /tmp/packages-microsoft-prod.deb

WORKDIR /app
COPY --from=build --chown=$APP_UID:$APP_UID /app/publish ./
USER $APP_UID
ENTRYPOINT ["dotnet", "PerfBenchmarks.dll"]
