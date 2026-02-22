// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

namespace Nethermind.Libp2p.Protocols.Yamux;

/// <summary>
/// Settings for Yamux flow-control window behaviour.
/// When <see cref="UseDynamicWindow"/> is true, the receive window is extended based on
/// observed incoming data consumption (throughput) to better utilise high-bandwidth or
/// high-latency links.
/// </summary>
public class YamuxWindowSettings
{
    /// <summary>
    /// Initial receive window size per stream (bytes). Default 256 KB per libp2p yamux spec.
    /// Must be positive; validation occurs when the protocol uses these settings.
    /// </summary>
    public int InitialWindowSize { get; set; } = YamuxProtocol.ProtocolInitialWindowSize;

    /// <summary>
    /// Maximum receive window size per stream when using dynamic window. Ignored if <see cref="UseDynamicWindow"/> is false.
    /// Must be at least <see cref="InitialWindowSize"/>; validation occurs when the protocol uses these settings.
    /// </summary>
    public int MaxWindowSize { get; set; } = 16 * 1024 * 1024;

    /// <summary>
    /// When true, window extension amount is adjusted from observed consumption rate (throughput)
    /// so that large data exchanges can utilise the link better. When false, window is extended
    /// by a fixed amount each time it drops below half of <see cref="InitialWindowSize"/>.
    /// </summary>
    public bool UseDynamicWindow { get; set; } = true;
}
