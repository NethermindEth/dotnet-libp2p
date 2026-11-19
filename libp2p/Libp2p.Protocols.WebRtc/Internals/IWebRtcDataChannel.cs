// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

namespace Nethermind.Libp2p.Protocols.WebRtc.Internals;

/// <summary>
/// The slice of an RTCDataChannel that <see cref="WebRtcChannel"/> needs. Deliberately free of
/// SIPSorcery types: the whole close handshake can then be driven by an in-memory fake, which is the
/// only way this transport gets covered in CI, where no live WebRTC stack is available.
/// </summary>
internal interface IWebRtcDataChannel
{
    /// <summary>
    /// The SCTP stream this channel runs on. Carried on the seam because it is the only name a
    /// libp2p stream has on this transport -- there is no label and no stream header -- so it is
    /// what a log line, and the multi-stream integration test, identify a stream by.
    /// </summary>
    ushort StreamId { get; }

    /// <summary>
    /// Bytes handed to the channel that it has not yet put on the wire, used as the backpressure
    /// signal: see <see cref="WebRtcChannel.MaxBufferedAmount"/>.
    /// </summary>
    long BufferedAmount { get; }

    /// <summary>One inbound datachannel message, which may hold any number of whole or partial frames.</summary>
    event Action<byte[]>? MessageReceived;

    /// <summary><see cref="BufferedAmount"/> has fallen far enough for sending to resume.</summary>
    event Action? BufferedAmountLow;

    event Action? Closed;

    /// <summary>
    /// Whether the channel has already closed. A stream can be accepted and queued before anything
    /// wraps it, so the <see cref="Closed"/> event may have fired before there was a subscriber:
    /// terminal state has to be readable as well as announced.
    /// </summary>
    bool IsClosed { get; }

    ValueTask SendAsync(ReadOnlyMemory<byte> frame, CancellationToken token = default);

    void Close();
}
