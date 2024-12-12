// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.Logging;
using Multiformats.Address;
using Multiformats.Address.Protocols;
using Nethermind.Libp2p.Core.Discovery;
using Nethermind.Libp2p.Core.Exceptions;
using Nethermind.Libp2p.Core.Extensions;
using System.Collections.Concurrent;
using System.Collections.ObjectModel;

namespace Nethermind.Libp2p.Core;

public class LocalPeer : IPeer
{
    protected readonly ILogger? _logger;
    protected readonly PeerStore _peerStore;
    protected readonly IProtocolStackSettings _protocolStackSettings;

    Dictionary<object, TaskCompletionSource<Multiaddress>> listenerReadyTcs = [];
    private ObservableCollection<Session> sessions { get; } = [];


    public LocalPeer(Identity identity, PeerStore peerStore, IProtocolStackSettings protocolStackSettings, ILoggerFactory? loggerFactory = null)
    {
        Identity = identity;
        _peerStore = peerStore;
        _protocolStackSettings = protocolStackSettings;
        _logger = loggerFactory?.CreateLogger($"peer-{identity.PeerId}");
    }

    public override string ToString()
    {
        return $"peer({Identity.PeerId}): addresses {string.Join(",", ListenAddresses)} sessions {string.Join("|", sessions.Select(x => $"{x.State.RemotePeerId}"))}";
    }

    public Identity Identity { get; }

    public ObservableCollection<Multiaddress> ListenAddresses { get; } = [];


    protected virtual Task ConnectedTo(ISession peer, bool isDialer) => Task.CompletedTask;

    public class Session(LocalPeer peer) : ISession
    {
        public string Id { get; } = Interlocked.Increment(ref Ids.IdCounter).ToString();
        public State State { get; } = new();
        public Multiaddress RemoteAddress => State.RemoteAddress ?? throw new Libp2pException("Session contains uninitialized remote address.");

        private readonly BlockingCollection<UpgradeOptions> SubDialRequests = [];

        public async Task DialAsync<TProtocol>(CancellationToken token = default) where TProtocol : ISessionProtocol
        {
            TaskCompletionSource<object?> tcs = new();
            SubDialRequests.Add(new UpgradeOptions() { CompletionSource = tcs!, SelectedProtocol = peer.GetProtocolInstance<TProtocol>() }, token);
            await tcs.Task;
            MarkAsConnected();
        }

        public async Task DialAsync(ISessionProtocol protocol, CancellationToken token = default)
        {
            TaskCompletionSource<object?> tcs = new();
            SubDialRequests.Add(new UpgradeOptions() { CompletionSource = tcs, SelectedProtocol = protocol }, token);
            await tcs.Task;
            MarkAsConnected();
        }

        public async Task<TResponse> DialAsync<TProtocol, TRequest, TResponse>(TRequest request, CancellationToken token = default) where TProtocol : ISessionProtocol<TRequest, TResponse>
        {
            TaskCompletionSource<object?> tcs = new();
            SubDialRequests.Add(new UpgradeOptions() { CompletionSource = tcs, SelectedProtocol = peer.GetProtocolInstance<TProtocol>(), Argument = request }, token);
            await tcs.Task;
            MarkAsConnected();
            return (TResponse)tcs.Task.Result;
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
        internal void MarkAsConnected() => ConnectedTcs?.TrySetResult();


        internal IEnumerable<UpgradeOptions> GetRequestQueue() => SubDialRequests.GetConsumingEnumerable(ConnectionToken);

    }

    protected virtual ProtocolRef SelectProtocol(Multiaddress addr)
    {
        if (_protocolStackSettings.TopProtocols is null or [])
        {
            throw new Libp2pSetupException($"Protocols are not set in {nameof(_protocolStackSettings)}");
        }

        return _protocolStackSettings.TopProtocols.First(p => (bool)p.Protocol.GetType().GetMethod(nameof(ITransportProtocol.IsAddressMatch))!.Invoke(null, [addr])!);
    }
    protected virtual Multiaddress[] GetDefaultAddresses()
    {
        if (_protocolStackSettings.TopProtocols is null or [])
        {
            throw new Libp2pSetupException($"Protocols are not set in {nameof(_protocolStackSettings)}");
        }

        return _protocolStackSettings.TopProtocols.SelectMany(p => (Multiaddress[])p.Protocol.GetType().GetMethod(nameof(ITransportProtocol.GetDefaultAddresses))!.Invoke(null, [Identity.PeerId])!).ToArray();
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


    public event Connected? OnConnected;

    public virtual async Task StartListenAsync(Multiaddress[]? addrs = default, CancellationToken token = default)
    {
        addrs ??= GetDefaultAddresses();

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

            listenTasks.Add(tcs.Task.WaitAsync(TimeSpan.FromMilliseconds(5000)).ContinueWith(t =>
            {
                if (t.IsFaulted)
                {
                    _logger?.LogDebug($"Failed to start listener for an address");
                    return null;
                }

                return t.Result;
            }));
        }

        await Task.WhenAll(listenTasks);

        foreach (Task startTask in listenTasks)
        {
            Multiaddress? addr = (startTask as Task<Multiaddress>)?.Result;

            if (addr is not null)
            {
                ListenAddresses.Add(addr);
            }
        }
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
        PeerId? remotePeerId = session.State.RemotePeerId ??
            throw new Libp2pSetupException($"{nameof(session.State.RemoteAddress)} should be initialiazed before session creation");

        lock (sessions)
        {
            if (sessions.Any(s => !ReferenceEquals(session, s) && s.State.RemoteAddress.GetPeerId() == remotePeerId))
            {
                _ = session.DisconnectAsync();
                throw new SessionExistsException(remotePeerId);
            }
            _logger?.LogDebug($"New session with {remotePeerId}");
            sessions.Add(session);
        }

        Task initializeSession = ConnectedTo(session, !isListener);
        initializeSession.ContinueWith(t =>
        {
            if (t.IsFaulted)
            {
                _ = session.DisconnectAsync();
                _logger?.LogError(t.Exception.InnerException, $"Disconnecting due to exception");
                return;
            }
            session.ConnectedTcs.TrySetResult();
            OnConnected?.Invoke(session);
        });
        return new NewSessionContext(this, session, proto, isListener, null);
    }

    internal IEnumerable<IProtocol> GetProtocolsFor(ProtocolRef protocol)
    {
        if (_protocolStackSettings.Protocols is null)
        {
            throw new Libp2pSetupException($"Protocols are not set in {nameof(_protocolStackSettings)}");
        }

        if (!_protocolStackSettings.Protocols.ContainsKey(protocol))
        {
            throw new Libp2pSetupException($"{protocol} is not added");
        }

        return _protocolStackSettings.Protocols[protocol].Select(p => p.Protocol);
    }


    // TODO: Remove lloking in the entire stack, look only on level for the given parent protocol
    internal IProtocol? GetProtocolInstance<TProtocol>()
    {
        return _protocolStackSettings.Protocols?.Keys.FirstOrDefault(p => p.Protocol.GetType() == typeof(TProtocol))?.Protocol;
    }

    public async Task<ISession> DialAsync(Multiaddress[] addrs, CancellationToken token)
    {
        PeerId? remotePeerId = addrs.FirstOrDefault()?.GetPeerId();
        ISession? existingSession = sessions.FirstOrDefault(s => s.State.RemotePeerId == remotePeerId);

        if (existingSession is not null)
        {
            return existingSession;
        }

        Dictionary<Multiaddress, CancellationTokenSource> cancellations = [];
        foreach (Multiaddress addr in addrs)
        {
            cancellations[addr] = CancellationTokenSource.CreateLinkedTokenSource(token);
        }

        Task timeoutTask = Task.Delay(15_000, token);
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

    public Task<ISession> DialAsync(PeerId peerId, CancellationToken token = default)
    {
        ISession? existingSession = sessions.FirstOrDefault(s => s.State.RemotePeerId == peerId);

        if (existingSession is not null)
        {
            return Task.FromResult(existingSession);
        }

        PeerStore.PeerInfo existingPeerInfo = _peerStore.GetPeerInfo(peerId);

        if (existingPeerInfo?.Addrs is null)
        {
            throw new Libp2pException("Peer not found");
        }

        return DialAsync([.. existingPeerInfo.Addrs], token);
    }

    internal IChannel Upgrade(Session session, ProtocolRef parentProtocol, IProtocol? upgradeProtocol, UpgradeOptions? options, bool isListener)
    {
        if (_protocolStackSettings.Protocols is null)
        {
            throw new Libp2pSetupException($"Protocols are not set in {nameof(_protocolStackSettings)}");
        }

        if (!_protocolStackSettings.Protocols.ContainsKey(parentProtocol))
        {
            throw new Libp2pSetupException($"{parentProtocol} is not added");
        }

        ProtocolRef top = upgradeProtocol is not null ? _protocolStackSettings.Protocols[parentProtocol].SingleOrDefault(x => x.Protocol == options.SelectedProtocol) ?? new ProtocolRef(upgradeProtocol) :
                          _protocolStackSettings.Protocols[parentProtocol].Single();

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
                if (isListener && top.Protocol is ISessionListenerProtocol listenerProtocol)
                {
                    SessionContext ctx = new(this, session, top, isListener, options);
                    upgradeTask = listenerProtocol.ListenAsync(downChannel.Reverse, ctx);
                    break;
                }

                Type? genericInterface = top.Protocol.GetType().GetInterfaces()
                    .FirstOrDefault(i =>
                        i.IsGenericType &&
                        i.GetGenericTypeDefinition() == typeof(ISessionProtocol<,>));

                if (genericInterface != null)
                {
                    Type[] genericArguments = genericInterface.GetGenericArguments();
                    Type requestType = genericArguments[0];

                    if (options?.Argument is not null && !options.Argument.GetType().IsAssignableTo(requestType))
                    {
                        throw new ArgumentException($"Invalid request. Argument is of {options.Argument.GetType()} type which is not assignable to {requestType.FullName}");
                    }

                    System.Reflection.MethodInfo? dialAsyncMethod = genericInterface.GetMethod("DialAsync");
                    if (dialAsyncMethod != null)
                    {
                        SessionContext ctx = new(this, session, top, isListener, options);
                        upgradeTask = (Task)dialAsyncMethod.Invoke(top.Protocol, [downChannel.Reverse, ctx, options?.Argument])!;
                        break;
                    }
                }
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
            _ = downChannel.CloseAsync();
            _logger?.LogInformation($"Finished {parentProtocol} to {top}, listen={isListener}");
        });

        return downChannel;
    }

    private static void MapToTaskCompletionSource(Task t, TaskCompletionSource<object?> tcs)
    {
        if (t.IsCompletedSuccessfully)
        {
            tcs.SetResult(t.GetType().GenericTypeArguments.Any() ? t.GetType().GetProperty("Result")!.GetValue(t) : null);
            return;
        }
        if (t.IsCanceled)
        {
            tcs.SetCanceled();
            return;
        }
        tcs.SetException(t.Exception!);
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
        if (_protocolStackSettings.Protocols is null)
        {
            throw new Libp2pSetupException($"Protocols are not set in {nameof(_protocolStackSettings)}");
        }

        if (upgradeProtocol is not null && !_protocolStackSettings.Protocols[protocol].Any(p => p.Protocol == upgradeProtocol))
        {
            _protocolStackSettings.Protocols.Add(new ProtocolRef(upgradeProtocol, false), []);
        }

        ProtocolRef top = upgradeProtocol is not null ?
            _protocolStackSettings.Protocols[protocol].FirstOrDefault(p => p.Protocol == upgradeProtocol, _protocolStackSettings.Protocols.Keys.First(k => k.Protocol == upgradeProtocol)) :
            _protocolStackSettings.Protocols[protocol].Single();

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
                if (isListener && top.Protocol is ISessionListenerProtocol listenerProtocol)
                {
                    SessionContext ctx = new(this, session, top, isListener, options);
                    upgradeTask = listenerProtocol.ListenAsync(parentChannel, ctx);
                    break;
                }

                Type? genericInterface = top.Protocol.GetType().GetInterfaces()
                    .FirstOrDefault(i =>
                        i.IsGenericType &&
                        i.GetGenericTypeDefinition() == typeof(ISessionProtocol<,>));

                if (genericInterface != null)
                {
                    Type[] genericArguments = genericInterface.GetGenericArguments();
                    Type requestType = genericArguments[0];
                    Type responseType = genericArguments[1];

                    if (options?.Argument is not null && !options.Argument.GetType().IsAssignableTo(requestType))
                    {
                        throw new ArgumentException($"Invalid request. Argument is of {options.Argument.GetType()} type which is not assignable to {requestType.FullName}");
                    }

                    // Dynamically invoke DialAsync
                    System.Reflection.MethodInfo? dialAsyncMethod = genericInterface.GetMethod("DialAsync");
                    if (dialAsyncMethod != null)
                    {
                        SessionContext ctx = new(this, session, top, isListener, options);
                        upgradeTask = (Task)dialAsyncMethod.Invoke(top.Protocol, [parentChannel, ctx, options?.Argument])!;
                        break;
                    }
                }
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
            _ = parentChannel.CloseAsync();
            _logger?.LogInformation($"Finished#2 {protocol} to {top}, listen={isListener}");
        });
    }

    public Task DisconnectAsync() => Task.WhenAll(sessions.ToArray().Select(s => s.DisconnectAsync()));
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

    public IChannel Upgrade(IProtocol specificProtocol, UpgradeOptions? upgradeOptions = null)
    {
        return localPeer.Upgrade(session, protocol, specificProtocol, upgradeOptions ?? this.upgradeOptions, isListener);
    }

    public Task Upgrade(IChannel parentChannel, UpgradeOptions? upgradeOptions = null)
    {
        return localPeer.Upgrade(session, parentChannel, protocol, null, upgradeOptions ?? this.upgradeOptions, isListener);
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