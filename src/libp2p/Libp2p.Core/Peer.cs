// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.Logging;
using Multiformats.Address;
using Multiformats.Address.Protocols;
using Nethermind.Libp2p.Core.Exceptions;
using Nethermind.Libp2p.Core.Extensions;
using Nethermind.Libp2p.Stack;
using System.Collections.Concurrent;
using System.Collections.ObjectModel;

namespace Nethermind.Libp2p.Core;

public class LocalPeer(Identity identity, IProtocolStackSettings protocolStackSettings, ILoggerFactory? loggerFactory = null) : IPeer
{
    private readonly ILogger? _logger = loggerFactory?.CreateLogger<LocalPeer>();

    protected IProtocolStackSettings protocolStackSettings = protocolStackSettings;

    public Identity Identity { get; } = identity;

    public ObservableCollection<Multiaddress> ListenAddresses { get; } = [];

    private ObservableCollection<Session> sessions { get; } = [];

    protected virtual Task ConnectedTo(ISession peer, bool isDialer) => Task.CompletedTask;

    public class Session(LocalPeer peer) : ISession
    {
        public string Id { get; } = Interlocked.Increment(ref Ids.IdCounter).ToString();
        public State State { get; } = new();
        public Multiaddress RemoteAddress => State.RemoteAddress ?? throw new Libp2pException("Session contains uninitialized remote address.");

        private readonly BlockingCollection<UpgradeOptions> SubDialRequests = [];

        public async Task DialAsync<TProtocol>(CancellationToken token = default) where TProtocol : ISessionProtocol
        {
            TaskCompletionSource tcs = new();
            SubDialRequests.Add(new UpgradeOptions() { CompletionSource = tcs, SelectedProtocol = peer.GetProtocolInstance<TProtocol>() }, token);
            await tcs.Task;
        }

        public async Task DialAsync(ISessionProtocol protocol, CancellationToken token = default)
        {
            TaskCompletionSource tcs = new();
            SubDialRequests.Add(new UpgradeOptions() { CompletionSource = tcs, SelectedProtocol = protocol }, token);
            await tcs.Task;
            MarkAsConnected();
        }

        private CancellationTokenSource connectionTokenSource = new();

        public Task DisconnectAsync()
        {
            connectionTokenSource.Cancel();
            peer.sessions.Remove(this);
            return Task.CompletedTask;
        }

        public CancellationToken ConnectionToken => connectionTokenSource.Token;


        public TaskCompletionSource ConnectedTcs = new();
        public Task Connected => ConnectedTcs.Task;
        internal void MarkAsConnected() => ConnectedTcs.SetResult();


        internal IEnumerable<UpgradeOptions> GetRequestQueue() => SubDialRequests.GetConsumingEnumerable(ConnectionToken);

    }

    protected virtual ProtocolRef SelectProtocol(Multiaddress addr)
    {
        if (protocolStackSettings.TopProtocols is null)
        {
            throw new Libp2pSetupException($"Protocols are not set in {nameof(protocolStackSettings)}");
        }

        if (protocolStackSettings.TopProtocols.Length is not 1)
        {
            throw new Libp2pSetupException("Top protocol should be single one by default");

        }

        return protocolStackSettings.TopProtocols.Single();
    }

    protected virtual IEnumerable<Multiaddress> PrepareAddresses(Multiaddress[] addrs)
    {
        foreach (Multiaddress addr in addrs)
        {
            if (!addr.Has<P2P>())
            {
                yield return addr.Add<P2P>(Identity.PeerId.ToString());
            }
            else
            {
                yield return addr;
            }
        }
    }

    Dictionary<object, TaskCompletionSource<Multiaddress>> listenerReadyTcs = new();

    public event Connected? OnConnected;

    public virtual async Task StartListenAsync(Multiaddress[] addrs, CancellationToken token = default)
    {
        List<Task> listenTasks = new(addrs.Length);

        foreach (Multiaddress addr in PrepareAddresses(addrs))
        {
            ProtocolRef listenerProtocol = SelectProtocol(addr);

            if (listenerProtocol.Protocol is not ITransportProtocol transportProtocol)
            {
                throw new Libp2pSetupException($"{nameof(ITransportProtocol)} should be implemented by {listenerProtocol.GetType()}");
            }

            ITransportContext ctx = new TransportContext(this, listenerProtocol, true);
            TaskCompletionSource<Multiaddress> tcs = new();
            listenerReadyTcs[ctx] = tcs;

            _ = transportProtocol.ListenAsync(ctx, addr, token).ContinueWith(t =>
            {
                if (t.IsFaulted)
                {
                    tcs.SetException(t.Exception);
                }
                ListenAddresses.Remove(tcs.Task.Result);
            });

            listenTasks.Add(tcs.Task.WaitAsync(TimeSpan.FromMilliseconds(5000)));
            ListenAddresses.Add(tcs.Task.Result);
        }

        await Task.WhenAll(listenTasks);
    }

    public void ListenerReady(object sender, Multiaddress addr)
    {
        if (listenerReadyTcs.Remove(sender, out TaskCompletionSource<Multiaddress>? tcs))
        {
            tcs.SetResult(addr);
        }
    }

    public INewConnectionContext CreateConnection(ProtocolRef proto, Session? session, bool isListener)
    {
        session ??= new(this);
        return new NewConnectionContext(this, session, proto, isListener, null);
    }

    public INewSessionContext UpgradeToSession(Session session, ProtocolRef proto, bool isListener)
    {
        if (session.State.RemoteAddress?.GetPeerId() is null)
        {
            throw new Libp2pSetupException($"{nameof(session.State.RemoteAddress)} should be initialiazed before session creation");
        }

        lock (sessions)
        {
            if (sessions.Any(s => !ReferenceEquals(session, s) && s.State.RemoteAddress.GetPeerId() == session.State.RemoteAddress?.GetPeerId()))
            {
                _ = session.DisconnectAsync();
                throw new Libp2pException("Session is already established");
            }

            sessions.Add(session);
        }

        Task initializeSession = ConnectedTo(session, !isListener);
        initializeSession.ContinueWith(t =>
        {
            if (t.IsFaulted)
            {
                _ = session.DisconnectAsync();
                return;
            }
            session.ConnectedTcs.SetResult();
            OnConnected?.Invoke(session);
        });
        return new NewSessionContext(this, session, proto, isListener, null);
    }

    internal IEnumerable<IProtocol> GetProtocolsFor(ProtocolRef protocol)
    {
        if (protocolStackSettings.Protocols is null)
        {
            throw new Libp2pSetupException($"Protocols are not set in {nameof(protocolStackSettings)}");
        }

        if (!protocolStackSettings.Protocols.ContainsKey(protocol))
        {
            throw new Libp2pSetupException($"{protocol} is not added");
        }

        return protocolStackSettings.Protocols[protocol].Select(p => p.Protocol);
    }

    internal IProtocol? GetProtocolInstance<TProtocol>()
    {
        return protocolStackSettings.Protocols?.Keys.FirstOrDefault(p => p.Protocol.GetType() == typeof(TProtocol))?.Protocol;
    }

    public async Task<ISession> DialAsync(Multiaddress[] addrs, CancellationToken token)
    {
        Dictionary<Multiaddress, CancellationTokenSource> cancellations = new();
        foreach (Multiaddress addr in addrs)
        {
            cancellations[addr] = CancellationTokenSource.CreateLinkedTokenSource(token);
        }

        Task timeoutTask = Task.Delay(15000_000, token);
        Task wait = await TaskHelper.FirstSuccess([timeoutTask, .. addrs.Select(addr => DialAsync(addr, cancellations[addr].Token))]);

        if (wait == timeoutTask)
        {
            throw new TimeoutException();
        }

        ISession firstConnected = (wait as Task<ISession>)!.Result;

        foreach (KeyValuePair<Multiaddress, CancellationTokenSource> c in cancellations)
        {
            if (c.Key != firstConnected.RemoteAddress)
            {
                c.Value.Cancel(false);
            }
        }

        return firstConnected;
    }

    public async Task<ISession> DialAsync(Multiaddress addr, CancellationToken token = default)
    {
        ProtocolRef dialerProtocol = SelectProtocol(addr);

        if (dialerProtocol.Protocol is not ITransportProtocol transportProtocol)
        {
            throw new Libp2pSetupException($"{nameof(ITransportProtocol)} should be implemented by {dialerProtocol.GetType()}");
        }

        Session session = new(this);
        ITransportContext ctx = new DialerTransportContext(this, session, dialerProtocol);

        Task dialingTask = transportProtocol.DialAsync(ctx, addr, token);

        Task dialingResult = await Task.WhenAny(dialingTask, session.Connected);

        if (dialingResult == dialingTask)
        {
            if (dialingResult.IsFaulted)
            {
                throw dialingResult.Exception;
            }
            throw new Libp2pException("Not able to dial the peer");
        }
        await session.Connected;
        return session;
    }

    internal IChannel Upgrade(Session session, ProtocolRef parentProtocol, IProtocol? upgradeProtocol, UpgradeOptions? options, bool isListener)
    {
        if (protocolStackSettings.Protocols is null)
        {
            throw new Libp2pSetupException($"Protocols are not set in {nameof(protocolStackSettings)}");
        }

        if (!protocolStackSettings.Protocols.ContainsKey(parentProtocol))
        {
            throw new Libp2pSetupException($"{parentProtocol} is not added");
        }

        ProtocolRef top = upgradeProtocol is not null ? new ProtocolRef(upgradeProtocol) :
                          options?.SelectedProtocol is not null ? protocolStackSettings.Protocols[parentProtocol].SingleOrDefault(x => x.Protocol == options.SelectedProtocol) ?? new ProtocolRef(options.SelectedProtocol) :
                          protocolStackSettings.Protocols[parentProtocol].Single();

        Channel downChannel = new();

        isListener = options?.ModeOverride switch { UpgradeModeOverride.Dial => false, UpgradeModeOverride.Listen => true, _ => isListener };

        Task upgradeTask;

        _logger?.LogInformation($"Upgrade {parentProtocol} to {top}, listen={isListener}");

        switch (top.Protocol)
        {
            case IConnectionProtocol tProto:
                {
                    ConnectionContext ctx = new(this, session, top, isListener, options);
                    upgradeTask = isListener ? tProto.ListenAsync(downChannel.Reverse, ctx) : tProto.DialAsync(downChannel.Reverse, ctx);

                    break;
                }
            case ISessionProtocol sProto:
                {
                    SessionContext ctx = new(this, session, top, isListener, options);
                    upgradeTask = isListener ? sProto.ListenAsync(downChannel.Reverse, ctx) : sProto.DialAsync(downChannel.Reverse, ctx);
                    break;
                }
            default:
                throw new Libp2pSetupException($"Protocol {top.Protocol} does not implement proper protocol interface");
        }

        if (options?.SelectedProtocol == top.Protocol && options?.CompletionSource is not null)
        {
            _ = upgradeTask.ContinueWith(async t =>
            {
                MapToTaskCompletionSource(t, options.CompletionSource);
                await downChannel.CloseAsync();
            });
        }

        upgradeTask.ContinueWith(t =>
        {
            if (t.IsFaulted)
            {
                _logger?.LogError($"Upgrade task failed for {top} with {t.Exception}");
            }
        });

        return downChannel;
    }

    private static void MapToTaskCompletionSource(Task t, TaskCompletionSource tcs)
    {
        if (t.IsCompletedSuccessfully)
        {
            tcs.SetResult();
            return;
        }
        if (t.IsCanceled)
        {
            tcs.SetCanceled();
            return;
        }
        tcs.SetException(t.Exception!);
    }

    internal async Task Upgrade(Session session, IChannel parentChannel, ProtocolRef protocol, IProtocol? upgradeProtocol, UpgradeOptions? options, bool isListener)
    {
        if (protocolStackSettings.Protocols is null)
        {
            throw new Libp2pSetupException($"Protocols are not set in {nameof(protocolStackSettings)}");
        }

        if (upgradeProtocol is not null && !protocolStackSettings.Protocols[protocol].Any(p => p.Protocol == upgradeProtocol))
        {
            protocolStackSettings.Protocols.Add(new ProtocolRef(upgradeProtocol, false), []);
        }

        ProtocolRef top = upgradeProtocol is not null ?
            protocolStackSettings.Protocols[protocol].FirstOrDefault(p => p.Protocol == upgradeProtocol, protocolStackSettings.Protocols.Keys.First(k => k.Protocol == upgradeProtocol)) :
            protocolStackSettings.Protocols[protocol].Single();

        isListener = options?.ModeOverride switch { UpgradeModeOverride.Dial => false, UpgradeModeOverride.Listen => true, _ => isListener };

        _logger?.LogInformation($"Upgrade and bind {protocol} to {top}, listen={isListener}");

        Task upgradeTask;
        switch (top.Protocol)
        {
            case IConnectionProtocol tProto:
                {
                    ConnectionContext ctx = new(this, session, top, isListener, options);
                    upgradeTask = isListener ? tProto.ListenAsync(parentChannel, ctx) : tProto.DialAsync(parentChannel, ctx);
                    break;
                }
            case ISessionProtocol sProto:
                {
                    SessionContext ctx = new(this, session, top, isListener, options);
                    upgradeTask = isListener ? sProto.ListenAsync(parentChannel, ctx) : sProto.DialAsync(parentChannel, ctx);
                    break;
                }
            default:
                throw new Libp2pSetupException($"Protocol {top.Protocol} does not implement proper protocol interface");
        }

        if (options?.SelectedProtocol == top.Protocol && options?.CompletionSource is not null)
        {
            _ = upgradeTask.ContinueWith(async t =>
            {
                MapToTaskCompletionSource(t, options.CompletionSource);
                await parentChannel.CloseAsync();
            });
        }

        await upgradeTask.ContinueWith(t =>
        {
            if (t.IsFaulted)
            {
                _logger?.LogError($"Upgrade task failed with {t.Exception}");
            }
        });
    }

    public Task DisconnectAsync() => Task.WhenAll(sessions.Select(s => s.DisconnectAsync()));
}

public class NewSessionContext(LocalPeer localPeer, LocalPeer.Session session, ProtocolRef protocol, bool isListener, UpgradeOptions? upgradeOptions) : ContextBase(localPeer, session, protocol, isListener, upgradeOptions), INewSessionContext
{
    public IEnumerable<UpgradeOptions> DialRequests => session.GetRequestQueue();

    public CancellationToken Token => session.ConnectionToken;

    public void Dispose()
    {

    }
}

public class SessionContext(LocalPeer localPeer, LocalPeer.Session session, ProtocolRef protocol, bool isListener, UpgradeOptions? upgradeOptions) : ContextBase(localPeer, session, protocol, isListener, upgradeOptions), ISessionContext
{
    public UpgradeOptions? UpgradeOptions => upgradeOptions;

    public async Task DialAsync<TProtocol>() where TProtocol : ISessionProtocol
    {
        await session.DialAsync<TProtocol>();
    }

    public async Task DialAsync(ISessionProtocol protocol)
    {
        await session.DialAsync(protocol);
    }

    public Task DisconnectAsync()
    {
        return session.DisconnectAsync();
    }
}

public class NewConnectionContext(LocalPeer localPeer, LocalPeer.Session session, ProtocolRef protocol, bool isListener, UpgradeOptions? upgradeOptions) : ContextBase(localPeer, session, protocol, isListener, upgradeOptions), INewConnectionContext
{
    public CancellationToken Token => session.ConnectionToken;

    public void Dispose()
    {

    }
}

public class ConnectionContext(LocalPeer localPeer, LocalPeer.Session session, ProtocolRef protocol, bool isListener, UpgradeOptions? upgradeOptions) : ContextBase(localPeer, session, protocol, isListener, upgradeOptions), IConnectionContext
{
    public UpgradeOptions? UpgradeOptions => upgradeOptions;

    public Task DisconnectAsync()
    {
        return session.DisconnectAsync();
    }
}

public class ContextBase(LocalPeer localPeer, LocalPeer.Session session, ProtocolRef protocol, bool isListener, UpgradeOptions? upgradeOptions) : IChannelFactory
{
    protected bool isListener = isListener;
    public IPeer Peer => localPeer;
    public State State => session.State;

    public IEnumerable<IProtocol> SubProtocols => localPeer.GetProtocolsFor(protocol);

    public string Id { get; } = session.Id;

    protected LocalPeer localPeer = localPeer;
    protected LocalPeer.Session session = session;
    protected ProtocolRef protocol = protocol;
    protected UpgradeOptions? upgradeOptions = upgradeOptions;

    public IChannel Upgrade(UpgradeOptions? upgradeOptions = null)
    {
        return localPeer.Upgrade(session, protocol, null, upgradeOptions ?? this.upgradeOptions, isListener);
    }

    public Task Upgrade(IChannel parentChannel, UpgradeOptions? upgradeOptions = null)
    {
        return localPeer.Upgrade(session, parentChannel, protocol, null, upgradeOptions ?? this.upgradeOptions, isListener);
    }

    public IChannel Upgrade(IProtocol specificProtocol, UpgradeOptions? upgradeOptions = null)
    {
        return localPeer.Upgrade(session, protocol, specificProtocol, upgradeOptions ?? this.upgradeOptions, isListener);
    }

    public Task Upgrade(IChannel parentChannel, IProtocol specificProtocol, UpgradeOptions? upgradeOptions = null)
    {
        return localPeer.Upgrade(session, parentChannel, protocol, specificProtocol, upgradeOptions ?? this.upgradeOptions, isListener);
    }

    public INewConnectionContext CreateConnection()
    {
        return localPeer.CreateConnection(protocol, null, isListener);
    }

    public INewSessionContext UpgradeToSession()
    {
        return localPeer.UpgradeToSession(session, protocol, isListener);
    }

    public void ListenerReady(Multiaddress addr)
    {
        localPeer.ListenerReady(this, addr);
    }
}
