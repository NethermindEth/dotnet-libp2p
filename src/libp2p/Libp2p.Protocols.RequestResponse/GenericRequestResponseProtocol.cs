// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using System.Buffers;
using Google.Protobuf;
using Microsoft.Extensions.Logging;
using Nethermind.Libp2p.Core;
using System;
using System.Threading.Tasks;
using System.IO;

namespace Nethermind.Libp2p.Protocols;

public class GenericRequestResponseProtocol<TRequest, TResponse> : ISessionProtocol<TRequest, TResponse>
    where TRequest : class, IMessage<TRequest>, new()
    where TResponse : class, IMessage<TResponse>, new()
{
    private readonly string _protocolId;
    private readonly Func<TRequest, ISessionContext, Task<TResponse>> _handler;
    private readonly ILogger<GenericRequestResponseProtocol<TRequest, TResponse>>? _logger;

    public GenericRequestResponseProtocol(
        string protocolId,
        Func<TRequest, ISessionContext, Task<TResponse>> handler,
        ILoggerFactory? loggerFactory = null)
    {
        _protocolId = protocolId ?? throw new ArgumentNullException(nameof(protocolId));
        _handler = handler ?? throw new ArgumentNullException(nameof(handler));
        _logger = loggerFactory?.CreateLogger<GenericRequestResponseProtocol<TRequest, TResponse>>();
    }

    public string Id => _protocolId;

    public async Task ListenAsync(IChannel channel, ISessionContext context)
    {
        try
        {
            _logger?.LogDebug("Starting ListenAsync for protocol {ProtocolId} from peer {RemotePeerId}",
                Id, context.State.RemotePeerId);

            int requestSize = await channel.ReadVarintAsync();
            _logger?.LogTrace("Received request size: {RequestSize} bytes", requestSize);

            if (requestSize <= 0)
            {
                _logger?.LogWarning("Invalid request size: {RequestSize}", requestSize);
                throw new InvalidDataException($"Invalid request size: {requestSize}");
            }

            ReadOnlySequence<byte> requestData = await channel.ReadAsync(requestSize, ReadBlockingMode.WaitAll).OrThrow();
            _logger?.LogTrace("Received request data: {RequestSize} bytes", requestData.Length);

            TRequest request = new TRequest().Descriptor.Parser.ParseFrom(requestData.ToArray()) as TRequest
                ?? throw new InvalidDataException("Failed to deserialize request");

            _logger?.LogDebug("Successfully deserialized the response");

            TResponse response = await _handler(request, context);
            _logger?.LogDebug("Handler processed request successfully, response type: {ResponseType}", typeof(TResponse).Name);

            byte[] responseBytes = response.ToByteArray();
            _logger?.LogTrace("Serialized response: {ResponseSize} bytes", responseBytes.Length);

            await channel.WriteVarintAsync(responseBytes.Length);
            await channel.WriteAsync(new ReadOnlySequence<byte>(responseBytes));

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

            byte[] requestBytes = request.ToByteArray();
            _logger?.LogTrace("Serialized request: {RequestSize} bytes", requestBytes.Length);

            await channel.WriteVarintAsync(requestBytes.Length);
            await channel.WriteAsync(new ReadOnlySequence<byte>(requestBytes));

            _logger?.LogDebug("Request sent, waiting for response");

            int responseSize = await channel.ReadVarintAsync();
            _logger?.LogTrace("Received response size: {ResponseSize} bytes", responseSize);

            if (responseSize <= 0)
            {
                _logger?.LogWarning("Invalid response size: {ResponseSize}", responseSize);
                throw new InvalidDataException($"Invalid response size: {responseSize}");
            }

            ReadOnlySequence<byte> responseData = await channel.ReadAsync(responseSize, ReadBlockingMode.WaitAll).OrThrow();
            _logger?.LogTrace("Received response data: {ResponseSize} bytes", responseData.Length);

            TResponse response = new TResponse().Descriptor.Parser.ParseFrom(responseData.ToArray()) as TResponse
                ?? throw new InvalidDataException("Failed to deserialize response");

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

