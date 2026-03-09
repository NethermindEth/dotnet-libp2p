# Build stage
FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
WORKDIR /src

# Copy the project file and restore dependencies (leveraged for caching)
COPY PerfBenchmarks.csproj ./
RUN dotnet restore

# Copy the rest of the source code
COPY . ./

# Build and publish as self-contained executable
RUN dotnet publish -c Release -o /app/publish --no-restore \
    /p:PublishSingleFile=false

# Runtime stage (slim image with only the .NET runtime)
FROM mcr.microsoft.com/dotnet/runtime:9.0

# Install libmsquic for QUIC transport support
RUN apt-get update && \
    apt-get install -y --no-install-recommends wget && \
    wget https://packages.microsoft.com/config/debian/12/packages-microsoft-prod.deb -O packages-microsoft-prod.deb && \
    dpkg -i packages-microsoft-prod.deb && \
    rm packages-microsoft-prod.deb && \
    apt-get update && \
    apt-get install -y --no-install-recommends libmsquic && \
    apt-get purge -y wget && \
    apt-get autoremove -y && \
    rm -rf /var/lib/apt/lists/*

# Create a non-root user (matches common host UID/GID for volume mounts)
ARG UID=1000
ARG GID=1000
RUN groupadd -g ${GID} appgroup && \
    useradd -m -u ${UID} -g ${GID} -s /bin/sh appuser

WORKDIR /app
COPY --from=build --chown=${UID}:${GID} /app/publish ./

# Switch to non-root user
USER appuser

# Entry point: run with dotnet
ENTRYPOINT ["dotnet", "PerfBenchmarks.dll"]
