// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using Google.Protobuf;
using Microsoft.Extensions.Logging;
using Nethermind.Libp2p.Core;
using Nethermind.Libp2p.Core.Exceptions;
using Nethermind.Libp2p.Core.Metrics;

namespace Nethermind.Libp2p.Protocols;

public class RequestResponseProtocol<TRequest, TResponse> : ISessionProtocol<TRequest, TResponse>
    where TRequest : class, IMessage<TRequest>, new()
    where TResponse : class, IMessage<TResponse>, new()
{
    private readonly string _protocolId;
    private readonly Func<TRequest, ISessionContext, CancellationToken, Task<TResponse>> _handler;
    private readonly Func<TRequest, bool>? _expectsResponse;
    private readonly ILogger<RequestResponseProtocol<TRequest, TResponse>>? _logger;
    private readonly int _maxMessageSize;
    private readonly TimeProvider _timeProvider;

    public static TimeSpan DefaultReadTimeout { get; } = TimeSpan.FromSeconds(30);

    internal static void ValidateReadOptions(TimeSpan readTimeout, int maxMessageSize)
    {
        if (readTimeout <= TimeSpan.Zero || readTimeout > TimeSpan.FromMilliseconds(uint.MaxValue - 1))
            throw new ArgumentOutOfRangeException(nameof(readTimeout));
        ArgumentOutOfRangeException.ThrowIfNegative(maxMessageSize);
    }

    /// <summary>Deadline for a complete length prefix and protobuf body.</summary>
    public TimeSpan ReadTimeout { get; }

    private readonly MessageParser<TRequest> _requestParser;
    private readonly MessageParser<TResponse> _responseParser;

    public RequestResponseProtocol(
        string protocolId,
        Func<TRequest, ISessionContext, Task<TResponse>> handler,
        ILoggerFactory? loggerFactory = null,
        Func<TRequest, bool>? expectsResponse = null,
        int maxMessageSize = int.MaxValue)
        : this(AdaptHandler(handler), protocolId, loggerFactory, expectsResponse, maxMessageSize)
    {
    }

    private static Func<TRequest, ISessionContext, CancellationToken, Task<TResponse>> AdaptHandler(
        Func<TRequest, ISessionContext, Task<TResponse>> handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        return (request, context, _) => handler(request, context);
    }

    public RequestResponseProtocol(
        Func<TRequest, ISessionContext, CancellationToken, Task<TResponse>> cancellationHandler,
        string protocolId,
        ILoggerFactory? loggerFactory = null,
        Func<TRequest, bool>? expectsResponse = null,
        int maxMessageSize = int.MaxValue)
        : this(DefaultReadTimeout, cancellationHandler, protocolId, loggerFactory, expectsResponse, maxMessageSize)
    {
    }

    public RequestResponseProtocol(
        TimeSpan readTimeout,
        string protocolId,
        Func<TRequest, ISessionContext, Task<TResponse>> handler,
        ILoggerFactory? loggerFactory = null,
        Func<TRequest, bool>? expectsResponse = null,
        int maxMessageSize = int.MaxValue,
        TimeProvider? timeProvider = null)
        : this(readTimeout, AdaptHandler(handler), protocolId, loggerFactory, expectsResponse, maxMessageSize, timeProvider)
    {
    }

    public RequestResponseProtocol(
        TimeSpan readTimeout,
        Func<TRequest, ISessionContext, CancellationToken, Task<TResponse>> cancellationHandler,
        string protocolId,
        ILoggerFactory? loggerFactory = null,
        Func<TRequest, bool>? expectsResponse = null,
        int maxMessageSize = int.MaxValue,
        TimeProvider? timeProvider = null)
    {
        ValidateReadOptions(readTimeout, maxMessageSize);

        ReadTimeout = readTimeout;
        _timeProvider = timeProvider ?? TimeProvider.System;

        _protocolId = protocolId ?? throw new ArgumentNullException(nameof(protocolId));
        _handler = cancellationHandler ?? throw new ArgumentNullException(nameof(cancellationHandler));
        _expectsResponse = expectsResponse;
        _logger = loggerFactory?.CreateLogger<RequestResponseProtocol<TRequest, TResponse>>();
        _maxMessageSize = maxMessageSize;
        _requestParser = new MessageParser<TRequest>(() => new TRequest());
        _responseParser = new MessageParser<TResponse>(() => new TResponse());
    }

    public string Id => _protocolId;

    public async Task ListenAsync(IChannel channel, ISessionContext context)
    {
        CancellationToken token = channel.CancellationToken;
        CancellationToken readToken = default;
        try
        {
            _logger?.LogDebug("Starting ListenAsync for protocol {ProtocolId} from peer {RemotePeerId}",
                Id, context.State.RemotePeerId);

            TRequest request;
            using (CancellationTokenSource deadline = new(ReadTimeout, _timeProvider))
            using (CancellationTokenSource read = CancellationTokenSource.CreateLinkedTokenSource(token, deadline.Token))
            {
                readToken = read.Token;
                try
                {
                    request = await channel.ReadPrefixedProtobufAsync(_requestParser, _maxMessageSize, readToken);
                }
                catch (Exception ex) when (ex is OperationCanceledException or ChannelClosedException &&
                    deadline.IsCancellationRequested && !token.IsCancellationRequested)
                {
                    RecordReadTimeout("inbound");
                    throw;
                }
            }
            _logger?.LogTrace("Received request of type {RequestType}", typeof(TRequest).Name);

            _logger?.LogDebug("Successfully deserialized the response");

            TResponse response = await _handler(request, context, token);

            if (_expectsResponse?.Invoke(request) != false)
            {
                token.ThrowIfCancellationRequested();
                _logger?.LogDebug("Handler processed request successfully, response type: {ResponseType}", typeof(TResponse).Name);
                _logger?.LogTrace("Sending response of type {ResponseType}", typeof(TResponse).Name);

                await channel.WriteSizeAndDataAsync(response.ToByteArray()).OrThrow();

                _logger?.LogDebug("Response sent successfully for protocol {ProtocolId}", Id);
            }

            IOResult eofResult = await channel.WriteEofAsync();
            if (eofResult is not IOResult.Ok and not IOResult.Ended)
                throw new ChannelClosedException(eofResult);
            await channel.CloseAsync();
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            LogFailure(ex, "ListenAsync", token.IsCancellationRequested || readToken.IsCancellationRequested);
            throw;
        }
    }

    public async Task<TResponse> DialAsync(IChannel channel, ISessionContext context, TRequest request)
    {
        CancellationToken token = channel.CancellationToken;
        CancellationToken readToken = default;
        try
        {
            _logger?.LogDebug("Starting DialAsync for protocol {ProtocolId} to peer {RemotePeerId}",
                Id, context.State.RemotePeerId);

            await channel.WriteSizeAndDataAsync(request.ToByteArray()).OrThrow();

            // One-way messages complete after transmission; there is no wire response.
            if (_expectsResponse?.Invoke(request) == false)
                return new TResponse();

            _logger?.LogDebug("Request sent, waiting for response");

            TResponse response;
            using (CancellationTokenSource deadline = new(ReadTimeout, _timeProvider))
            using (CancellationTokenSource read = CancellationTokenSource.CreateLinkedTokenSource(token, deadline.Token))
            {
                readToken = read.Token;
                try
                {
                    response = await channel.ReadPrefixedProtobufAsync(_responseParser, _maxMessageSize, readToken);
                }
                catch (Exception ex) when (ex is OperationCanceledException or ChannelClosedException &&
                    deadline.IsCancellationRequested && !token.IsCancellationRequested)
                {
                    RecordReadTimeout("outbound");
                    throw;
                }
            }

            _logger?.LogTrace("Received request of type {RequestType}", typeof(TResponse).Name);
            _logger?.LogDebug("Successfully deserialized the response");

            return response;
        }
        catch (Exception ex)
        {
            LogFailure(ex, "DialAsync", token.IsCancellationRequested || readToken.IsCancellationRequested);
            throw;
        }
    }

    private void RecordReadTimeout(string direction)
    {
        try
        {
            Libp2pMetrics.RequestResponseReadTimeouts.Add(1, new("protocol", Id), new("direction", direction));
        }
        catch (Exception telemetryFailure)
        {
            try
            {
                _logger?.LogDebug(telemetryFailure,
                    "Failed to record read timeout for protocol {ProtocolId} ({Direction})", Id, direction);
            }
            catch (Exception)
            {
                // Neither a telemetry listener nor its diagnostic may replace the original read failure.
            }
        }
    }

    private void LogFailure(Exception failure, string direction, bool cancelled)
        => _logger?.Log(IsRoutineFailure(failure, cancelled) ? LogLevel.Debug : LogLevel.Warning,
            failure, "Stream failure in {Direction} for protocol {ProtocolId}", direction, Id);

    private static bool IsRoutineFailure(Exception failure, bool cancelled)
        => failure is ChannelClosedException or ChannelAbortedException or SessionExistsException ||
           failure is OperationCanceledException && cancelled ||
           failure is AggregateException aggregate && aggregate.InnerExceptions.Count > 0 &&
           aggregate.InnerExceptions.All(inner => IsRoutineFailure(inner, cancelled));

    public override string ToString() => Id;
}
