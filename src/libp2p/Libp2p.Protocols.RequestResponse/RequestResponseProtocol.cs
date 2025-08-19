// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using Google.Protobuf;
using Microsoft.Extensions.Logging;
using Nethermind.Libp2p.Core;

namespace Nethermind.Libp2p.Protocols;

public class RequestResponseProtocol<TRequest, TResponse> : ISessionProtocol<TRequest, TResponse>
    where TRequest : class, IMessage<TRequest>, new()
    where TResponse : class, IMessage<TResponse>, new()
{
    private readonly string _protocolId;
    private readonly Func<TRequest, ISessionContext, Task<TResponse>> _handler;
    private readonly ILogger<RequestResponseProtocol<TRequest, TResponse>>? _logger;

    private readonly MessageParser<TRequest> _requestParser;
    private readonly MessageParser<TResponse> _responseParser;

    public RequestResponseProtocol(
        string protocolId,
        Func<TRequest, ISessionContext, Task<TResponse>> handler,
        ILoggerFactory? loggerFactory = null)
    {
        _protocolId = protocolId ?? throw new ArgumentNullException(nameof(protocolId));
        _handler = handler ?? throw new ArgumentNullException(nameof(handler));
        _logger = loggerFactory?.CreateLogger<RequestResponseProtocol<TRequest, TResponse>>();
        _requestParser = new MessageParser<TRequest>(() => new TRequest());
        _responseParser = new MessageParser<TResponse>(() => new TResponse());
    }

    public string Id => _protocolId;

    public async Task ListenAsync(IChannel channel, ISessionContext context)
    {
        try
        {
            _logger?.LogDebug("Starting ListenAsync for protocol {ProtocolId} from peer {RemotePeerId}",
                Id, context.State.RemotePeerId);

            TRequest request = await channel.ReadPrefixedProtobufAsync(_requestParser);
            _logger?.LogTrace("Received request of type {RequestType}", typeof(TRequest).Name);

            _logger?.LogDebug("Successfully deserialized the response");

            TResponse response = await _handler(request, context);

            _logger?.LogDebug("Handler processed request successfully, response type: {ResponseType}", typeof(TResponse).Name);
            _logger?.LogTrace("Sending response of type {ResponseType}", typeof(TResponse).Name);

            await channel.WriteSizeAndProtobufAsync(response);

            _logger?.LogDebug("Response sent successfully for protocol {ProtocolId}", Id);

            await channel.CloseAsync();
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Error in ListenAsync for protocol {ProtocolId}: {ErrorMessage}", Id, ex.Message);
            throw;
        }
    }

    public async Task<TResponse> DialAsync(IChannel channel, ISessionContext context, TRequest request)
    {
        try
        {
            _logger?.LogDebug("Starting DialAsync for protocol {ProtocolId} to peer {RemotePeerId}",
                Id, context.State.RemotePeerId);

            await channel.WriteSizeAndProtobufAsync(request);

            _logger?.LogDebug("Request sent, waiting for response");

            TResponse response = await channel.ReadPrefixedProtobufAsync(_responseParser);

            _logger?.LogTrace("Received request of type {RequestType}", typeof(TResponse).Name);
            _logger?.LogDebug("Successfully deserialized the response");

            return response;
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Error in DialAsync for protocol {ProtocolId}: {ErrorMessage}", Id, ex.Message);
            throw;
        }
    }

    public override string ToString() => Id;
}

