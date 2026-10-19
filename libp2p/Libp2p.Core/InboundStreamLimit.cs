// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

namespace Nethermind.Libp2p.Core;

/// <summary>
/// Caps how many inbound streams may be in flight for the protocols it is attached to. One instance
/// shared across several registrations gives them one shared allowance, which is what a protocol
/// offered under several versions needs: the peer picks a version, so the versions are alternatives
/// rather than additions.
/// </summary>
/// <remarks>
/// <para>
/// A frame or message size limit bounds one stream, and a muxer's stream cap bounds one session, but
/// neither bounds the product across peers. This does, so the cost of being reachable stops scaling
/// with how many peers choose to talk at once.
/// </para>
/// <para>
/// This is a narrow slice of what go-libp2p's resource manager calls a service scope and its
/// service-peer counterpart: inbound stream counts only, with no memory or file descriptor
/// accounting and no scope hierarchy. There is no libp2p specification for any of it. For a sense of
/// scale, go's generic per-protocol default admits 512 inbound streams and 64 per peer on a small
/// machine, both growing with memory; go-libp2p-pubsub separately allows exactly one inbound stream
/// per peer, though it drops the older stream rather than refusing the newer one.
/// </para>
/// </remarks>
public sealed class InboundStreamLimit
{
    private readonly Lock _lock = new();
    private readonly Dictionary<PeerId, int> _perPeer = [];
    private int _inFlight;

    /// <param name="maxConcurrent">Streams allowed at once across every peer.</param>
    /// <param name="maxPerPeer">Streams allowed at once from one peer, or 0 for no per-peer cap.</param>
    public InboundStreamLimit(int maxConcurrent, int maxPerPeer = 0)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxConcurrent);
        ArgumentOutOfRangeException.ThrowIfNegative(maxPerPeer);
        MaxConcurrent = maxConcurrent;
        MaxPerPeer = maxPerPeer;
    }

    public int MaxConcurrent { get; }

    public int MaxPerPeer { get; }

    /// <summary>Streams currently counted against this limit.</summary>
    public int InFlight { get { lock (_lock) { return _inFlight; } } }

    /// <summary>
    /// Takes an allowance for <paramref name="peer"/>, or reports that there is none to take. A
    /// stream whose peer is not yet identified counts against the total only, since there is no
    /// identity to attribute it to.
    /// </summary>
    internal bool TryAcquire(PeerId? peer)
    {
        lock (_lock)
        {
            if (_inFlight >= MaxConcurrent)
            {
                return false;
            }

            if (peer is not null && MaxPerPeer != 0)
            {
                _perPeer.TryGetValue(peer, out int forPeer);
                if (forPeer >= MaxPerPeer)
                {
                    return false;
                }

                _perPeer[peer] = forPeer + 1;
            }

            _inFlight++;
            return true;
        }
    }

    /// <summary>Gives back an allowance taken by <see cref="TryAcquire"/>.</summary>
    internal void Release(PeerId? peer)
    {
        lock (_lock)
        {
            _inFlight--;

            if (peer is null || MaxPerPeer == 0)
            {
                return;
            }

            if (_perPeer.TryGetValue(peer, out int forPeer) && --forPeer == 0)
            {
                _perPeer.Remove(peer);
            }
            else if (forPeer > 0)
            {
                _perPeer[peer] = forPeer;
            }
        }
    }
}
