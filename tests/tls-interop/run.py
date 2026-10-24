# SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
# SPDX-License-Identifier: MIT
# cspell:words appendonly lpop rpush

"""Run the retained transport-interop application against pinned Go TLS/Yamux."""

import argparse
import json
import os
from pathlib import Path
import socket
import subprocess
import time


def wait_for(operation, seconds=40):
    deadline = time.monotonic() + seconds
    while time.monotonic() < deadline:
        result = operation()
        if result:
            return result
        time.sleep(0.05)
    raise TimeoutError("Interop readiness deadline expired")


def redis(port, *arguments):
    values = [str(value).encode() for value in arguments]
    data = f"*{len(values)}\r\n".encode()
    for value in values:
        data += f"${len(value)}\r\n".encode() + value + b"\r\n"
    with socket.create_connection(("127.0.0.1", port), timeout=1) as connection:
        connection.sendall(data)
        with connection.makefile("rb") as response:
            line = response.readline().rstrip(b"\r\n")
            if line.startswith(b"$"):
                length = int(line[1:])
                return response.read(length).decode() if length >= 0 else None
            if line.startswith((b"+", b":")):
                return line[1:].decode()
            raise RuntimeError(f"Unexpected Redis reply: {line!r}")


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--dotnet-dll", required=True)
    parser.add_argument("--go-peer", required=True)
    parser.add_argument("--output", required=True)
    args = parser.parse_args()
    output = Path(args.output).resolve()
    output.mkdir(parents=True, exist_ok=True)
    processes = []
    files = []

    def start(name, command, env=None):
        log = output / f"{name}.log"
        stream = log.open("w")
        files.append(stream)
        process = subprocess.Popen(command, stdout=stream, stderr=subprocess.STDOUT, env=env, cwd=output)
        processes.append(process)
        return process, log

    def text(path):
        return path.read_text(errors="replace")

    try:
        with socket.socket() as reserved:
            reserved.bind(("127.0.0.1", 0))
            port = reserved.getsockname()[1]
        server, _ = start("redis", ["redis-server", "--port", str(port), "--bind", "127.0.0.1", "--save", "", "--appendonly", "no"])

        def ready():
            if server.poll() is not None:
                raise RuntimeError("Owned Redis server exited")
            try:
                return redis(port, "PING") == "PONG"
            except OSError:
                return False

        wait_for(ready, 5)
        env = os.environ.copy()
        env.update(transport="tcp", muxer="yamux", security="tls", ip="127.0.0.1",
                   redis_addr=f"127.0.0.1:{port}", test_timeout_seconds="60", TEST_KEY="tls373")
        key = "tls373_listener_multiaddr"
        env["is_dialer"] = "false"
        listener, _ = start("dotnet-listener", ["dotnet", str(Path(args.dotnet_dll).resolve())], env)

        def dotnet_address():
            if listener.poll() is not None:
                raise RuntimeError(".NET listener exited before publishing its address")
            return redis(port, "LPOP", key)

        target = wait_for(dotnet_address)
        go_dialer, go_dial_log = start("go-dialer", [str(Path(args.go_peer).resolve()), target])
        if go_dialer.wait(timeout=45) != 0 or "PASS Go dialer" not in text(go_dial_log):
            raise RuntimeError("Go dialer/.NET listener interoperability failed")
        listener.terminate()
        listener.wait(timeout=5)

        go_listener, go_listen_log = start("go-listener", [str(Path(args.go_peer).resolve())])

        def go_address():
            if go_listener.poll() is not None:
                raise RuntimeError("Go listener exited")
            return next((line[7:] for line in text(go_listen_log).splitlines() if line.startswith("LISTEN ")), None)

        target = wait_for(go_address)
        redis(port, "RPUSH", key, target)
        env["is_dialer"] = "true"
        dialer, dial_log = start("dotnet-dialer", ["dotnet", str(Path(args.dotnet_dll).resolve())], env)
        if dialer.wait(timeout=45) != 0:
            raise RuntimeError(".NET dialer/Go listener interoperability failed")
        result = next(json.loads(line) for line in text(dial_log).splitlines() if line.startswith('{"handshakePlus'))
        wait_for(lambda: "AUTHENTICATED Go listener" in text(go_listen_log), 5)
        summary = {"go_revision": "8ce45df1bd142373cdf0a6f8355a757273258f49", "go_version": "v0.41.1",
                   "go_dialer": "passed", "dotnet_dialer": "passed", "dotnet_result": result}
        (output / "summary.json").write_text(json.dumps(summary, indent=2) + "\n")
        print(json.dumps(summary))
    finally:
        # Stop only direct children this driver started. All services are local
        # to this run; there is no process-name or shared executable matching.
        for process in reversed(processes):
            if process.poll() is None:
                process.terminate()
                try:
                    process.wait(timeout=5)
                except subprocess.TimeoutExpired:
                    process.kill()
                    process.wait(timeout=5)
        for stream in files:
            stream.close()


if __name__ == "__main__":
    main()
