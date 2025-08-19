FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build-env
WORKDIR /app

COPY . ./
RUN ls && cd ./src/samples/transport-interop && dotnet publish -c Release -o /out

FROM mcr.microsoft.com/dotnet/runtime:9.0-noble
WORKDIR /app

RUN apt update -y && \
    apt install curl -y && \
    curl -sSL -O  https://packages.microsoft.com/config/ubuntu/22.04/packages-microsoft-prod.deb && \
    dpkg -i packages-microsoft-prod.deb && \
    apt update -y && \
    apt install libmsquic=2.3.5 -y && \
    ln -s /usr/lib/x86_64-linux-gnu/libmsquic.so.2 /bin

COPY --from=build-env /out .
ENTRYPOINT ["dotnet", "TransportInterop.dll"]


