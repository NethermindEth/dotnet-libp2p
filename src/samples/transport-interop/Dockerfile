FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build-env
WORKDIR /app

# Bring in solution files and sources so project references resolve during restore.
COPY ./global.json ./Directory.Build.props ./
COPY ./src ./src

WORKDIR /app/src/samples/transport-interop
RUN dotnet restore
RUN dotnet publish -c Release -o /app/out

FROM mcr.microsoft.com/dotnet/runtime:10.0
WORKDIR /app

RUN apt-get update -y && \
    apt-get install -y --no-install-recommends curl && \
    curl -sSL -O https://packages.microsoft.com/config/ubuntu/22.04/packages-microsoft-prod.deb && \
    dpkg -i packages-microsoft-prod.deb && \
    apt-get update -y && \
    apt-get install -y --no-install-recommends libmsquic && \
    ln -s /usr/lib/x86_64-linux-gnu/libmsquic.so.2 /bin && \
    rm -rf /var/lib/apt/lists/* packages-microsoft-prod.deb

COPY --from=build-env /app/out .
ENTRYPOINT ["dotnet", "TransportInterop.dll"]