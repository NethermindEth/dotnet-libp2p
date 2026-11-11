// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using SIPSorcery.Net;

namespace Nethermind.Libp2p.Protocols.WebRtc.Internals;

/// <summary>
/// Drives <see cref="SctpWebRtcDataChannel.PollBufferedAmount"/> for a set of channels on one SCTP
/// association.
/// <para>
/// SCTP gives no send-buffer drain notification, so <see cref="WebRtcStream"/>'s parked writer is
/// woken by polling, and a channel with nobody polling it never resumes: the writer stops at
/// <see cref="WebRtcStream.MaxBufferedAmount"/> and stays there until the caller's own timeout.
/// That is not a hypothetical -- a dialled stream was given a channel without a poller and a 1 MiB
/// transfer stalled after 32 KiB. Owning this separately is what stops the driver being forgotten
/// the next time one of these channels is created somewhere new.
/// </para>
/// </summary>
internal sealed class SctpBufferedAmountPoller : IDisposable
{
    /// <summary>
    /// How often the send buffer is sampled. Short enough not to show up as throughput loss, long
    /// enough that the O(send queue) scan behind <see cref="SctpAssociation.SendBufferedAmount"/>
    /// stays noise.
    /// </summary>
    internal static readonly TimeSpan Interval = TimeSpan.FromMilliseconds(20);

    private readonly SctpAssociation _association;
    private readonly Func<IEnumerable<SctpWebRtcDataChannel>> _channels;
    private readonly Timer _timer;
    private int _polling;

    /// <param name="channels">
    /// Read afresh on every tick, so channels may come and go without re-registering anything.
    /// </param>
    public SctpBufferedAmountPoller(SctpAssociation association, Func<IEnumerable<SctpWebRtcDataChannel>> channels)
    {
        _association = association;
        _channels = channels;

        // Started last, so no tick can run before the fields above are set.
        _timer = new Timer(_ => Poll(), null, Interval, Interval);
    }

    public void Dispose() => _timer.Dispose();

    private void Poll()
    {
        // Ticks can overlap if a poll runs long; one at a time is enough, and a skipped tick costs
        // at most one extra interval of latency for a parked writer.
        if (Interlocked.Exchange(ref _polling, 1) == 1)
        {
            return;
        }

        try
        {
            // SendBufferedAmount is association-wide and sums the whole send queue on each read, so
            // it is read once per tick rather than once per channel.
            long buffered = (long)_association.SendBufferedAmount;
            foreach (SctpWebRtcDataChannel channel in _channels())
            {
                try
                {
                    channel.PollBufferedAmount(buffered);
                }
                catch (Exception)
                {
                    // Per channel: one throwing subscriber must not strand the writers on every
                    // channel after it in the iteration.
                }
            }
        }
        catch (Exception)
        {
            // Timer callbacks run on the pool with nobody to observe them. Losing one sample costs
            // a parked writer one more interval; letting it escape would take the process down.
        }
        finally
        {
            Volatile.Write(ref _polling, 0);
        }
    }
}
