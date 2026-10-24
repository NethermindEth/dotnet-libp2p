// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using System.Buffers;
using Nethermind.Libp2p.Core;

namespace Nethermind.Libp2p.Protocols.Tls;

/// <summary>
/// One async owner drives the TLS engine. I/O tasks never access the
/// engine. Each direction has at most one application record pending, and the
/// network writer drains records in order without preventing network reads.
/// </summary>
internal static class TlsChannel
{
    internal static async Task RunAsync(TlsHandshake handshake, IChannel downChannel,
        Func<IChannel> upgrade, CancellationToken cancellationToken)
    {
        // Crypto and certificate processing run on the thread pool, not a native
        // transport callback or a permanently blocked TLS stream worker.
        await Task.Yield();
        var engine = handshake.Engine;
        CancellationToken downToken = downChannel.CancellationToken;
        using CancellationTokenSource stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        CancellationToken token = stop.Token;
        Task cancelled = Task.Delay(Timeout.Infinite, token);
        // Transport completion can follow a valid close_notify before its paired
        // EOFs are published. Wake the owner; do not cancel delivery of that EOF.
        using CancellationTokenSource downSignal = CancellationTokenSource.CreateLinkedTokenSource(token, downToken);
        Task downClosed = Task.Delay(Timeout.Infinite, downSignal.Token);
        bool downCompletionObserved = false;
        IChannel? upChannel = null;
        CancellationToken upToken = default;
        CancellationTokenRegistration upRegistration = default;
        TaskCompletionSource upClosed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        bool upSignalObserved = false;
        bool upCompletionObserved = false;
        bool upEofPublished = false;
        bool applicationEof = false;
        Task<Chunk>? networkRead = null;
        Task<Chunk>? applicationRead = null;
        Task<IOResult>? networkWrite = null;
        Task<IOResult>? applicationWrite = null;
        byte[] record = new byte[5];
        int received = 0;
        bool header = true;
        bool writeEnded = false;
        bool networkWriteIsClose = false;
        bool clean = false;
        try
        {
            token.ThrowIfCancellationRequested();
            handshake.Start();
            while (true)
            {
                token.ThrowIfCancellationRequested();
                bool progress = false;
                if (upChannel is not null && upToken.IsCancellationRequested && !upCompletionObserved)
                {
                    upSignalObserved = true;
                    if (!upEofPublished) throw new IOException("Upgraded TLS channel was closed.");
                    // A completed channel's remaining read distinguishes graceful
                    // application EOF from forced abort. Do not cancel the final
                    // close alert just because both application halves reached EOF.
                    applicationRead ??= ReadAsync(upChannel, engine.ApplicationDataLimit, token);
                    if (applicationRead.IsCompleted)
                    {
                        Chunk completed = await applicationRead;
                        if (completed.Result == IOResult.Ended)
                        {
                            applicationRead = null;
                            applicationEof = true;
                            upCompletionObserved = true;
                        }
                        else Check(completed.Result, token);
                    }
                }
                if (networkWrite is { IsCompleted: true })
                {
                    IOResult result = await networkWrite;
                    if (result != IOResult.Ok && !(handshake.PeerClosed && networkWriteIsClose && downToken.IsCancellationRequested))
                        Check(result, token);
                    networkWrite = null;
                    progress = true;
                }
                if (applicationWrite is { IsCompleted: true })
                {
                    Check(await applicationWrite, token);
                    applicationWrite = null;
                    progress = true;
                }

                // Admit writes only after prior encrypted output has drained.
                // Prefer an already accepted application chunk to the next peer record.
                if (!engine.IsClosed && applicationRead is { IsCompleted: true }
                    && networkWrite is null && engine.GetAvailableOutputBytes() == 0)
                {
                    Chunk chunk = await applicationRead;
                    applicationRead = null;
                    if (chunk.Result == IOResult.Ended)
                    {
                        applicationEof = true;
                    }
                    else
                    {
                        Check(chunk.Result, token);
                        engine.WriteApplicationData(chunk.Data, 0, chunk.Data.Length);
                    }
                    progress = true;
                }

                if (!engine.IsClosed && !handshake.PeerClosed && networkRead is { IsCompleted: true }
                    && applicationWrite is null && engine.GetAvailableInputBytes() == 0
                    && (!handshake.Complete || engine.GetAvailableOutputBytes() == 0))
                {
                    Chunk chunk = await networkRead;
                    networkRead = null;
                    if (chunk.Result == IOResult.Ended)
                    {
                        if (received != 0 || !header) throw new EndOfStreamException("Truncated TLS record.");
                        // Requires close_notify; a bare transport EOF is not clean TLS EOF.
                        engine.CloseInput();
                    }
                    else
                    {
                        Check(chunk.Result, token);
                        chunk.Data.CopyTo(record, received);
                        received += chunk.Data.Length;
                        if (received == record.Length)
                        {
                            if (header)
                            {
                                // Validate the header against the current cipher/record limit
                                // before allocating the declared body. Never read the next record.
                                int length = engine.PreviewInputRecord(record).RecordSize;
                                byte[] frame = new byte[length];
                                record.CopyTo(frame, 0);
                                record = frame;
                                header = false;
                            }
                            if (received == record.Length)
                            {
                                engine.OfferInput(record);
                                record = new byte[5];
                                received = 0;
                                header = true;
                            }
                        }
                    }
                    progress = true;
                }

                if (upChannel is null && handshake.Complete && networkWrite is null
                    && engine.GetAvailableOutputBytes() == 0)
                {
                    upChannel = upgrade();
                    upToken = upChannel.CancellationToken;
                    upRegistration = upToken.Register(() => upClosed.TrySetResult());
                    progress = true;
                }

                if (downToken.IsCancellationRequested)
                {
                    downCompletionObserved = true;
                    if (!handshake.PeerClosed && networkRead is not { IsCompleted: true })
                        throw new IOException("TLS transport closed without close_notify.");
                }

                if (upChannel is not null && applicationWrite is null && engine.GetAvailableInputBytes() > 0)
                {
                    // A full input record was offered only when the prior plaintext
                    // write had finished. This owned array remains immutable after WriteAsync.
                    byte[] plaintext = new byte[engine.GetAvailableInputBytes()];
                    int length = engine.ReadInput(plaintext, 0, plaintext.Length);
                    if (length != plaintext.Length) throw new IOException("Incomplete TLS plaintext read.");
                    applicationWrite = upChannel.WriteAsync(new ReadOnlySequence<byte>(plaintext), token).AsTask();
                    progress = true;
                }

                if (applicationEof && !writeEnded && networkWrite is null && engine.GetAvailableOutputBytes() == 0)
                {
                    writeEnded = true;
                    handshake.SendCloseNotify();
                    progress = true;
                }

                if (networkWrite is null && engine.GetAvailableOutputBytes() > 0)
                {
                    byte[] encrypted = new byte[engine.PreviewOutputRecord()];
                    int length = engine.ReadOutput(encrypted, 0, encrypted.Length);
                    if (length != encrypted.Length) throw new IOException("Incomplete TLS output record.");
                    // Channel write completion can precede the reader finishing with
                    // the array. Do not pool, reuse, or mutate it after handing it off.
                    networkWrite = downChannel.WriteAsync(new ReadOnlySequence<byte>(encrypted), token).AsTask();
                    networkWriteIsClose = engine.IsClosed || writeEnded;
                    progress = true;
                }

                if (engine.IsClosed) throw new IOException("TLS engine closed without an orderly shutdown.");
                if (handshake.PeerClosed)
                {
                    if (!handshake.Complete) throw new IOException("TLS closed before mutual authentication completed.");
                    if (!upEofPublished && applicationWrite is null && engine.GetAvailableInputBytes() == 0)
                    {
                        upEofPublished = true;
                        Check(await upChannel!.WriteEofAsync(cancellationToken), cancellationToken);
                        progress = true;
                    }
                    if (downToken.IsCancellationRequested && !writeEnded)
                        throw new IOException("TLS transport closed before the application write side ended.");
                    if (writeEnded && networkWrite is null && applicationWrite is null
                        && engine.GetAvailableOutputBytes() == 0 && engine.GetAvailableInputBytes() == 0)
                    {
                        // Publishing EOF can complete the transport channel. Only
                        // explicit caller cancellation applies to this final operation.
                        Check(await downChannel.WriteEofAsync(cancellationToken), cancellationToken);
                        clean = true;
                        return;
                    }
                }
                else if (networkRead is null && applicationWrite is null
                    && engine.GetAvailableInputBytes() == 0
                    && (!handshake.Complete || engine.GetAvailableOutputBytes() == 0))
                {
                    // A peer may finish writing HRR plus CCS before reading our
                    // CCS and second ClientHello. Handshake input must progress
                    // while that bounded response flight is queued. After Finished,
                    // retain the application record/output backpressure boundary.
                    networkRead = ReadAsync(downChannel, record.Length - received, token);
                    progress = true;
                }
                if (upChannel is not null && !writeEnded && !applicationEof && applicationRead is null
                    && networkWrite is null && engine.GetAvailableOutputBytes() == 0)
                {
                    applicationRead = ReadAsync(upChannel, engine.ApplicationDataLimit, token);
                    progress = true;
                }

                if (progress) continue;
                List<Task> pending = [cancelled];
                // Latch signals only after the owner handles them. A completion
                // racing this wait-set construction must still wake the owner.
                if (!downCompletionObserved) pending.Add(downClosed);
                if (upChannel is not null && !upSignalObserved) pending.Add(upClosed.Task);
                // Keep every consumable read in the wait set, even if it completed
                // after the checks above. Filtering by IsCompleted here loses that
                // wakeup and can strand a request until unrelated traffic arrives.
                // Exclude reads held behind backpressure to avoid a busy loop.
                if (networkRead is not null && !handshake.PeerClosed && applicationWrite is null
                    && engine.GetAvailableInputBytes() == 0
                    && (!handshake.Complete || engine.GetAvailableOutputBytes() == 0))
                    pending.Add(networkRead);
                if (applicationRead is not null &&
                    ((networkWrite is null && engine.GetAvailableOutputBytes() == 0)
                    || (upToken.IsCancellationRequested && upEofPublished && !upCompletionObserved)))
                    pending.Add(applicationRead);
                if (networkWrite is not null) pending.Add(networkWrite);
                if (applicationWrite is not null) pending.Add(applicationWrite);
                await Task.WhenAny(pending);
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException
            && upChannel is not null && upChannel.GetAwaiter().IsCompleted && !cancellationToken.IsCancellationRequested)
        {
            // The owner of the upgraded channel disposed/aborted the connection.
        }
        finally
        {
            // Cancellation alone cannot release a channel write that was partly
            // consumed. Abort closes that debt before awaiting every owned task.
            stop.Cancel();
            if (clean)
            {
                if (upChannel is not null) await upChannel.CloseReadAsync();
                await downChannel.CloseReadAsync();
            }
            else
            {
                await downChannel.AbortAsync();
                if (upChannel is not null) await upChannel.AbortAsync();
            }
            Task?[] pending = [networkRead, applicationRead, networkWrite, applicationWrite, cancelled, downClosed];
            foreach (Task? task in pending)
            {
                if (task is null) continue;
                try { await task; }
                catch (Exception) { /* Preserve the original failure; observe shutdown failures. */ }
            }
            upRegistration.Dispose();
            if (upChannel is not null) await upChannel.CloseAsync();
            await downChannel.CloseAsync();
        }
    }

    private static async Task<Chunk> ReadAsync(IChannel channel, int length, CancellationToken token)
    {
        ReadResult result = await channel.ReadAsync(length, ReadBlockingMode.WaitAny, token);
        if (result.Result == IOResult.Ok && (result.Data.IsEmpty || result.Data.Length > length))
            throw new IOException("Invalid channel read length.");
        return new Chunk(result.Result, result.Data.ToArray());
    }

    private static void Check(IOResult result, CancellationToken token)
    {
        if (result == IOResult.Ok) return;
        token.ThrowIfCancellationRequested();
        throw new IOException($"TLS channel I/O failed: {result}.");
    }

    private readonly record struct Chunk(IOResult Result, byte[] Data);
}
