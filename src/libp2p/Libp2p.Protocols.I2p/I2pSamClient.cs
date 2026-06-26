// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using System.Collections.Concurrent;
using System.Net.Sockets;
using System.Net;
using System.Text;

namespace Nethermind.Libp2p.Protocols.I2p;

public sealed record I2pSamStream(NetworkStream Stream, string? RemoteDestination);

public sealed class I2pSamClient(I2pOptions options) : IAsyncDisposable
{
    private const int MaxSamResponseLineBytes = 8192;
    private static readonly Encoding SamEncoding = Encoding.ASCII;
    private readonly I2pOptions _options = options;
    private readonly SemaphoreSlim _sessionLock = new(1, 1);
    private readonly ConcurrentDictionary<NetworkStream, byte> _pendingControlStreams = new();
    private NetworkStream? _primaryControl;
    private string? _publicDestination;
    private NetworkStream? _plainStreamControl;
    private string? _plainStreamDestination;
    private bool _streamSessionAdded;
    private bool _datagramSessionAdded;
    private int _disposed;

    public async Task<string> CreateSessionAsync(CancellationToken token)
    {
        ThrowIfDisposed();
        using CancellationTokenSource sessionCts = CreateTimeoutTokenSource(token);
        await _sessionLock.WaitAsync(sessionCts.Token).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            if (_options.UsePrimarySessionForStreams)
            {
                await EnsurePrimarySessionAsync(sessionCts.Token).ConfigureAwait(false);
                await EnsureStreamSubsessionAsync(sessionCts.Token).ConfigureAwait(false);
                return _publicDestination!;
            }

            await EnsurePlainStreamSessionAsync(sessionCts.Token).ConfigureAwait(false);
            return _plainStreamDestination!;
        }
        finally
        {
            _sessionLock.Release();
        }
    }

    public async Task<I2pSamStream> AcceptStreamAsync(CancellationToken token)
    {
        ThrowIfDisposed();
        await CreateSessionAsync(token).ConfigureAwait(false);
        ThrowIfDisposed();
        NetworkStream stream = await OpenControlStreamAsync(token).ConfigureAwait(false);
        try
        {
            await NegotiateHelloAsync(stream, token, requireSam33: _options.UsePrimarySessionForStreams).ConfigureAwait(false);
            string command = $"STREAM ACCEPT ID={_options.StreamSessionId} SILENT=false";
            I2pSamResponse response = await SendCommandAsync(stream, command, token).ConfigureAwait(false);
            response.ThrowIfNotOk(command);
            string destinationLine = await ReadLineAsync(stream, token).ConfigureAwait(false);
            PromoteControlStream(stream);
            return new I2pSamStream(stream, ParseAcceptedDestination(destinationLine));
        }
        catch
        {
            await DisposeControlStreamAsync(stream).ConfigureAwait(false);
            throw;
        }
    }

    public async Task<NetworkStream> ConnectStreamAsync(string destination, CancellationToken token)
    {
        ThrowIfDisposed();
        ValidateCommandToken(destination, nameof(destination));
        await CreateSessionAsync(token).ConfigureAwait(false);
        ThrowIfDisposed();
        NetworkStream stream = await OpenControlStreamAsync(token).ConfigureAwait(false);
        try
        {
            await NegotiateHelloAsync(stream, token, requireSam33: _options.UsePrimarySessionForStreams).ConfigureAwait(false);
            string command = $"STREAM CONNECT ID={_options.StreamSessionId} DESTINATION={destination}";
            I2pSamResponse response = await SendCommandAsync(stream, command, token).ConfigureAwait(false);
            response.ThrowIfNotOk(command);
            PromoteControlStream(stream);
            return stream;
        }
        catch
        {
            await DisposeControlStreamAsync(stream).ConfigureAwait(false);
            throw;
        }
    }

    public async Task<I2pDatagramSession> CreateDatagramSessionAsync(CancellationToken token)
    {
        ThrowIfDisposed();
        IPEndPoint samUdpEndpoint = ResolveEndpoint(_options.SamUdpHost, _options.SamUdpPort);
        UdpClient? udpClient = null;
        using CancellationTokenSource sessionCts = CreateTimeoutTokenSource(token);
        try
        {
            udpClient = new(BindDatagramEndpoint());
            await _sessionLock.WaitAsync(sessionCts.Token).ConfigureAwait(false);
            try
            {
                ThrowIfDisposed();
                await EnsurePrimarySessionAsync(sessionCts.Token).ConfigureAwait(false);
                await EnsureDatagramSubsessionAsync((IPEndPoint)udpClient.Client.LocalEndPoint!, sessionCts.Token).ConfigureAwait(false);
            }
            finally
            {
                _sessionLock.Release();
            }

            return new I2pDatagramSession(
                _options.DatagramSessionId,
                _publicDestination!,
                udpClient,
                samUdpEndpoint,
                _options.MaxDatagramPayloadSize,
                ReleaseDatagramSubsessionAsync);
        }
        catch
        {
            udpClient?.Dispose();
            throw;
        }
    }

    private async Task<NetworkStream> OpenControlStreamAsync(CancellationToken token)
    {
        TcpClient client = new();
        try
        {
            using CancellationTokenSource connectCts = CancellationTokenSource.CreateLinkedTokenSource(token);
            connectCts.CancelAfter(_options.ConnectTimeoutMilliseconds);
            await client.ConnectAsync(_options.SamHost, _options.SamPort, connectCts.Token).ConfigureAwait(false);
            NetworkStream stream = new(client.Client, ownsSocket: true);
            _pendingControlStreams.TryAdd(stream, 0);
            if (Volatile.Read(ref _disposed) != 0)
            {
                await DisposeControlStreamAsync(stream).ConfigureAwait(false);
                throw new ObjectDisposedException(nameof(I2pSamClient));
            }

            return stream;
        }
        catch
        {
            client.Dispose();
            throw;
        }
    }

    private async Task NegotiateHelloAsync(NetworkStream stream, CancellationToken token, bool requireSam33 = false)
    {
        string command = requireSam33 ? "HELLO VERSION MIN=3.3 MAX=3.3" : "HELLO VERSION MIN=3.1 MAX=3.3";
        I2pSamResponse response = await SendCommandAsync(stream, command, token).ConfigureAwait(false);
        response.ThrowIfNotOk(command);
        if (requireSam33
            && response.Values.TryGetValue("VERSION", out string? version)
            && !string.Equals(version, "3.3", StringComparison.Ordinal))
        {
            throw new I2pException($"SAM bridge negotiated version {version}; SAM 3.3 is required for primary sessions.");
        }
    }

    private async Task<string> GetPrivateDestinationAsync(CancellationToken token)
    {
        if (string.IsNullOrWhiteSpace(_options.DestinationKeyFile))
        {
            return _options.Destination;
        }

        if (File.Exists(_options.DestinationKeyFile))
        {
            return (await File.ReadAllTextAsync(_options.DestinationKeyFile, token).ConfigureAwait(false)).Trim();
        }

        NetworkStream stream = await OpenControlStreamAsync(token).ConfigureAwait(false);
        try
        {
            await NegotiateHelloAsync(stream, token).ConfigureAwait(false);
            string command = string.IsNullOrWhiteSpace(_options.DestinationSignatureType)
                ? "DEST GENERATE"
                : $"DEST GENERATE SIGNATURE_TYPE={_options.DestinationSignatureType}";
            I2pSamResponse response = await SendCommandAsync(stream, command, token).ConfigureAwait(false);
            response.ThrowIfNotOk(command);
            string destination = response.Values.GetValueOrDefault("PRIV")
                ?? throw new I2pException("SAM DEST GENERATE did not return PRIV.");
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(_options.DestinationKeyFile))!);
            await WritePrivateDestinationAsync(_options.DestinationKeyFile, destination, token).ConfigureAwait(false);
            return destination;
        }
        finally
        {
            await DisposeControlStreamAsync(stream).ConfigureAwait(false);
        }
    }

    private async Task EnsurePrimarySessionAsync(CancellationToken token)
    {
        if (_primaryControl is not null && _publicDestination is not null)
        {
            return;
        }

        NetworkStream control = await OpenControlStreamAsync(token).ConfigureAwait(false);
        try
        {
            await NegotiateHelloAsync(control, token, requireSam33: true).ConfigureAwait(false);
            string command = BuildPrimarySessionCreateCommand(await GetPrivateDestinationAsync(token).ConfigureAwait(false));
            I2pSamResponse response = await SendCommandAsync(control, command, token).ConfigureAwait(false);
            response.ThrowIfNotOk("SESSION CREATE");
            _primaryControl = control;
            _publicDestination = await LookupOwnDestinationAsync(control, token).ConfigureAwait(false);
            PromoteControlStream(control);
        }
        catch
        {
            await DisposeControlStreamAsync(control).ConfigureAwait(false);
            throw;
        }
    }

    private async Task EnsurePlainStreamSessionAsync(CancellationToken token)
    {
        if (_plainStreamControl is not null && _plainStreamDestination is not null)
        {
            return;
        }

        NetworkStream control = await OpenControlStreamAsync(token).ConfigureAwait(false);
        try
        {
            await NegotiateHelloAsync(control, token).ConfigureAwait(false);
            string command = BuildStreamSessionCreateCommand(await GetPrivateDestinationAsync(token).ConfigureAwait(false));
            I2pSamResponse response = await SendCommandAsync(control, command, token).ConfigureAwait(false);
            response.ThrowIfNotOk("SESSION CREATE");
            _plainStreamControl = control;
            _plainStreamDestination = await LookupOwnDestinationAsync(control, token).ConfigureAwait(false);
            PromoteControlStream(control);
        }
        catch
        {
            await DisposeControlStreamAsync(control).ConfigureAwait(false);
            throw;
        }
    }

    private async Task EnsureStreamSubsessionAsync(CancellationToken token)
    {
        if (_streamSessionAdded)
        {
            return;
        }

        string command = BuildStreamSessionAddCommand();
        I2pSamResponse response = await SendCommandAsync(_primaryControl!, command, token).ConfigureAwait(false);
        response.ThrowIfNotOk(command);
        _streamSessionAdded = true;
    }

    private async Task EnsureDatagramSubsessionAsync(IPEndPoint forwardEndpoint, CancellationToken token)
    {
        if (_datagramSessionAdded)
        {
            throw new I2pException("A SAM DATAGRAM subsession is already active.");
        }

        string command = BuildDatagramSessionAddCommand(forwardEndpoint);
        I2pSamResponse response = await SendCommandAsync(_primaryControl!, command, token).ConfigureAwait(false);
        response.ThrowIfNotOk(command);
        _datagramSessionAdded = true;
    }

    private string BuildPrimarySessionCreateCommand(string destination)
    {
        ValidateCommandToken(_options.SessionId, nameof(_options.SessionId));
        ValidateCommandToken(_options.PrimarySessionStyle, nameof(_options.PrimarySessionStyle));
        ValidateCommandToken(destination, nameof(destination));
        ValidateCommandToken(_options.SamUdpHost, nameof(_options.SamUdpHost));
        StringBuilder builder = new($"SESSION CREATE STYLE={_options.PrimarySessionStyle} ID={_options.SessionId} DESTINATION={destination}");
        builder.Append(" sam.udp.host=").Append(_options.SamUdpHost);
        builder.Append(" sam.udp.port=").Append(_options.SamUdpPort);
        if (string.Equals(destination, "TRANSIENT", StringComparison.OrdinalIgnoreCase)
            && !string.IsNullOrWhiteSpace(_options.DestinationSignatureType)
            && !_options.SessionOptions.ContainsKey("SIGNATURE_TYPE"))
        {
            ValidateCommandToken(_options.DestinationSignatureType, nameof(_options.DestinationSignatureType));
            builder.Append(" SIGNATURE_TYPE=").Append(_options.DestinationSignatureType);
        }

        AppendSessionOptions(builder);
        return builder.ToString();
    }

    private string BuildStreamSessionAddCommand()
    {
        ValidateCommandToken(_options.StreamSessionId, nameof(_options.StreamSessionId));
        return $"SESSION ADD STYLE=STREAM ID={_options.StreamSessionId}";
    }

    private string BuildStreamSessionCreateCommand(string destination)
    {
        ValidateCommandToken(_options.StreamSessionId, nameof(_options.StreamSessionId));
        ValidateCommandToken(destination, nameof(destination));
        StringBuilder builder = new($"SESSION CREATE STYLE=STREAM ID={_options.StreamSessionId} DESTINATION={destination}");
        if (string.Equals(destination, "TRANSIENT", StringComparison.OrdinalIgnoreCase)
            && !string.IsNullOrWhiteSpace(_options.DestinationSignatureType)
            && !_options.SessionOptions.ContainsKey("SIGNATURE_TYPE"))
        {
            ValidateCommandToken(_options.DestinationSignatureType, nameof(_options.DestinationSignatureType));
            builder.Append(" SIGNATURE_TYPE=").Append(_options.DestinationSignatureType);
        }

        AppendSessionOptions(builder);
        return builder.ToString();
    }

    private string BuildDatagramSessionAddCommand(IPEndPoint forwardEndpoint)
    {
        ValidateCommandToken(_options.DatagramSessionId, nameof(_options.DatagramSessionId));
        ValidateCommandToken(_options.SamUdpHost, nameof(_options.SamUdpHost));
        StringBuilder builder = new($"SESSION ADD STYLE=DATAGRAM ID={_options.DatagramSessionId}");
        if (!string.IsNullOrWhiteSpace(_options.DatagramHost))
        {
            ValidateCommandToken(_options.DatagramHost, nameof(_options.DatagramHost));
            builder.Append(" HOST=").Append(_options.DatagramHost);
        }

        builder.Append(" PORT=").Append(forwardEndpoint.Port);
        builder.Append(" sam.udp.host=").Append(_options.SamUdpHost);
        builder.Append(" sam.udp.port=").Append(_options.SamUdpPort);

        return builder.ToString();
    }

    private void AppendSessionOptions(StringBuilder builder)
    {
        foreach ((string key, string value) in _options.SessionOptions)
        {
            ValidateOptionKey(key);
            ValidateCommandToken(value, key);
            builder.Append(' ').Append(key).Append('=').Append(value);
        }
    }

    private IPEndPoint BindDatagramEndpoint()
    {
        return ResolveEndpoint(_options.DatagramHost ?? IPAddress.Any.ToString(), _options.DatagramPort);
    }

    private static IPEndPoint ResolveEndpoint(string host, int port)
    {
        IPAddress address = IPAddress.TryParse(host, out IPAddress? parsed)
            ? parsed
            : Dns.GetHostAddresses(host).First();
        return new IPEndPoint(address, port);
    }

    private async ValueTask ReleaseDatagramSubsessionAsync()
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            return;
        }

        using CancellationTokenSource releaseCts = new(_options.ConnectTimeoutMilliseconds);
        bool hasLock = false;
        try
        {
            await _sessionLock.WaitAsync(releaseCts.Token).ConfigureAwait(false);
            hasLock = true;
        }
        catch (ObjectDisposedException) when (Volatile.Read(ref _disposed) != 0)
        {
            return;
        }
        catch (OperationCanceledException)
        {
            _datagramSessionAdded = false;
            return;
        }

        try
        {
            if (!_datagramSessionAdded || _primaryControl is null)
            {
                return;
            }

            string command = $"SESSION REMOVE ID={_options.DatagramSessionId}";
            try
            {
                I2pSamResponse response = await SendCommandAsync(_primaryControl, command, releaseCts.Token).ConfigureAwait(false);
                response.ThrowIfNotOk(command);
            }
            catch (Exception ex) when (IsExpectedDatagramReleaseFailure(ex))
            {
            }

            _datagramSessionAdded = false;
        }
        finally
        {
            if (hasLock)
            {
                _sessionLock.Release();
            }
        }
    }

    private static async Task WritePrivateDestinationAsync(string path, string destination, CancellationToken token)
    {
        if (OperatingSystem.IsWindows())
        {
            await File.WriteAllTextAsync(path, destination, token).ConfigureAwait(false);
            return;
        }

        FileStreamOptions options = new()
        {
            Mode = FileMode.CreateNew,
            Access = FileAccess.Write,
            Share = FileShare.None,
            UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite
        };
        await using FileStream stream = new(path, options);
        byte[] bytes = SamEncoding.GetBytes(destination);
        await stream.WriteAsync(bytes, token).ConfigureAwait(false);
    }

    private static bool IsExpectedDatagramReleaseFailure(Exception exception)
        => exception is OperationCanceledException or IOException or ObjectDisposedException or SocketException or I2pException;

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
    }

    private void PromoteControlStream(NetworkStream stream)
    {
        _pendingControlStreams.TryRemove(stream, out _);
    }

    private async ValueTask DisposeControlStreamAsync(NetworkStream stream)
    {
        _pendingControlStreams.TryRemove(stream, out _);
        await stream.DisposeAsync().ConfigureAwait(false);
    }

    private async ValueTask DisposePendingControlStreamsAsync()
    {
        foreach (NetworkStream stream in _pendingControlStreams.Keys)
        {
            await DisposeControlStreamAsync(stream).ConfigureAwait(false);
        }
    }

    private CancellationTokenSource CreateTimeoutTokenSource(CancellationToken token)
    {
        CancellationTokenSource cts = CancellationTokenSource.CreateLinkedTokenSource(token);
        cts.CancelAfter(_options.ConnectTimeoutMilliseconds);
        return cts;
    }

    private static void ValidateCommandToken(string value, string paramName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, paramName);
        if (value.Any(char.IsWhiteSpace) || value.Any(char.IsControl))
        {
            throw new ArgumentException("SAM command token cannot contain whitespace or control characters.", paramName);
        }
    }

    private static void ValidateOptionKey(string key)
    {
        ValidateCommandToken(key, nameof(key));
        if (key.Contains('='))
        {
            throw new ArgumentException("SAM option key cannot contain '='.", nameof(key));
        }
    }

    private static async Task<string> LookupOwnDestinationAsync(NetworkStream stream, CancellationToken token)
    {
        const string command = "NAMING LOOKUP NAME=ME";
        I2pSamResponse response = await SendCommandAsync(stream, command, token).ConfigureAwait(false);
        response.ThrowIfNotOk(command);
        return response.Values.GetValueOrDefault("VALUE")
            ?? throw new I2pException("SAM NAMING LOOKUP NAME=ME did not return VALUE.");
    }

    private static string? ParseAcceptedDestination(string line)
    {
        if (line.StartsWith("STREAM STATUS ", StringComparison.Ordinal))
        {
            I2pSamResponse response = I2pSamResponse.Parse(line);
            response.ThrowIfNotOk("STREAM ACCEPT");
            return response.Values.GetValueOrDefault("DESTINATION");
        }

        string trimmed = line.Trim();
        if (trimmed.Length == 0)
        {
            return null;
        }

        int firstSpace = trimmed.IndexOf(' ');
        return firstSpace < 0 ? trimmed : trimmed[..firstSpace];
    }

    private static async Task<I2pSamResponse> SendCommandAsync(NetworkStream stream, string command, CancellationToken token)
    {
        await WriteLineAsync(stream, command, token).ConfigureAwait(false);
        string line = await ReadLineAsync(stream, token).ConfigureAwait(false);
        return I2pSamResponse.Parse(line);
    }

    private static async Task WriteLineAsync(NetworkStream stream, string line, CancellationToken token)
    {
        byte[] bytes = SamEncoding.GetBytes(line + "\n");
        await stream.WriteAsync(bytes, token).ConfigureAwait(false);
    }

    private static async Task<string> ReadLineAsync(NetworkStream stream, CancellationToken token)
    {
        using MemoryStream buffer = new();
        byte[] one = new byte[1];
        int lineBytes = 0;
        while (true)
        {
            int read = await stream.ReadAsync(one, token).ConfigureAwait(false);
            if (read == 0)
            {
                throw new I2pException("SAM connection closed while reading a response.");
            }

            if (one[0] == '\n')
            {
                break;
            }

            lineBytes++;
            if (lineBytes > MaxSamResponseLineBytes)
            {
                throw new I2pException($"SAM response exceeded {MaxSamResponseLineBytes} bytes without a line terminator.");
            }

            if (one[0] != '\r')
            {
                buffer.WriteByte(one[0]);
            }
        }

        return SamEncoding.GetString(buffer.ToArray());
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        await DisposePendingControlStreamsAsync().ConfigureAwait(false);

        NetworkStream? primarySnapshot = _primaryControl;
        NetworkStream? streamSnapshot = _plainStreamControl;
        if (primarySnapshot is not null)
        {
            await primarySnapshot.DisposeAsync().ConfigureAwait(false);
        }

        if (streamSnapshot is not null && !ReferenceEquals(streamSnapshot, primarySnapshot))
        {
            await streamSnapshot.DisposeAsync().ConfigureAwait(false);
        }

        NetworkStream? primaryControl;
        NetworkStream? streamControl;
        using CancellationTokenSource disposeCts = new(_options.ConnectTimeoutMilliseconds);
        try
        {
            await _sessionLock.WaitAsync(disposeCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        try
        {
            primaryControl = _primaryControl;
            streamControl = _plainStreamControl;
            _primaryControl = null;
            _publicDestination = null;
            _plainStreamControl = null;
            _plainStreamDestination = null;
            _streamSessionAdded = false;
            _datagramSessionAdded = false;
        }
        finally
        {
            _sessionLock.Release();
            _sessionLock.Dispose();
        }

        if (primaryControl is not null)
        {
            await primaryControl.DisposeAsync().ConfigureAwait(false);
        }

        if (streamControl is not null && !ReferenceEquals(streamControl, primaryControl))
        {
            await streamControl.DisposeAsync().ConfigureAwait(false);
        }
    }
}
