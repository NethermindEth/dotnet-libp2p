// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

namespace Nethermind.Libp2p.Protocols.WebRtc.Internals;

/// <summary>
/// The slice of an RTCDataChannel that <see cref="WebRtcStream"/> needs. Deliberately free of
/// SIPSorcery types: the whole close handshake can then be driven by an in-memory fake, which is the
/// only way this transport gets covered in CI, where no live WebRTC stack is available.
/// </summary>
internal interface IWebRtcDataChannel
{
    /// <summary>
    /// Bytes handed to the channel that it has not yet put on the wire, used as the backpressure
    /// signal: see <see cref="WebRtcStream.MaxBufferedAmount"/>.
    /// </summary>
    long BufferedAmount { get; }

    /// <summary>One inbound datachannel message, which may hold any number of whole or partial frames.</summary>
    event Action<byte[]>? MessageReceived;

    /// <summary><see cref="BufferedAmount"/> has fallen far enough for sending to resume.</summary>
    event Action? BufferedAmountLow;

    event Action? Closed;

    ValueTask SendAsync(ReadOnlyMemory<byte> frame, CancellationToken token = default);

    void Close();
}
