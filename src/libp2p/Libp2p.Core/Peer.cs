// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.Logging;
using Multiformats.Address;
using Multiformats.Address.Protocols;
using Nethermind.Libp2p.Core.Context;
using Nethermind.Libp2p.Core.Discovery;
using Nethermind.Libp2p.Core.Exceptions;
using Nethermind.Libp2p.Core.Extensions;
using System.Collections.ObjectModel;

namespace Nethermind.Libp2p.Core;

public partial class LocalPeer : ILocalPeer
{
    protected readonly ILogger? _logger;
    protected readonly PeerStore _peerStore;
    protected readonly IProtocolStackSettings _protocolStackSettings;

    Dictionary<object, TaskCompletionSource<Multiaddress>> listenerReadyTcs = [];
    public ObservableCollection<Session> Sessions { get; } = [];


    public LocalPeer(Identity identity, PeerStore peerStore, IProtocolStackSettings protocolStackSettings, ILoggerFactory? loggerFactory = null)
    {
        Identity = identity;
        _peerStore = peerStore;
        _protocolStackSettings = protocolStackSettings;
        _logger = loggerFactory?.CreateLogger($"peer-{identity.PeerId}");
    }

    public override string ToString()
    {
        return $"peer({Identity.PeerId}): addresses {string.Join(",", ListenAddresses)} sessions {string.Join("|", Sessions.Select(x => $"{x.State.RemotePeerId}"))}";
    }

    public Identity Identity { get; }

    public ObservableCollection<Multiaddress> ListenAddresses { get; } = [];


    protected virtual Task ConnectedTo(ISession peer, bool isDialer) => Task.CompletedTask;

    protected virtual ProtocolRef SelectProtocol(Multiaddress addr)
    {
        if (_protocolStackSettings.TopProtocols is null or [])
        {
            throw new Libp2pSetupException($"Protocols are not set in {nameof(_protocolStackSettings)}");
        }

        return _protocolStackSettings.TopProtocols.First(p => ITransportProtocol.IsAddressMatch(p.Protocol, addr));
    }

    protected virtual Multiaddress[] GetDefaultAddresses()
    {
        if (_protocolStackSettings.TopProtocols is null or [])
        {
            throw new Libp2pSetupException($"Protocols are not set in {nameof(_protocolStackSettings)}");
        }

        return _protocolStackSettings.TopProtocols.SelectMany(p => ITransportProtocol.GetDefaultAddresses(p.Protocol, Identity.PeerId)).ToArray();
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

        lock (Sessions)
        {
            if (Sessions.Any(s => !ReferenceEquals(session, s) && s.State.RemoteAddress.GetPeerId() == remotePeerId))
            {
                _ = session.DisconnectAsync();
                throw new SessionExistsException(remotePeerId);
            }
            _logger?.LogDebug($"New session with {remotePeerId}");
            Sessions.Add(session);
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


    // TODO: Remove locking in the entire stack, look only on level for the given parent protocol
    internal IProtocol? GetProtocolInstance<TProtocol>()
    {
        return _protocolStackSettings.Protocols?.Keys.FirstOrDefault(p => p.Protocol.GetType() == typeof(TProtocol))?.Protocol;
    }

    public async Task<ISession> DialAsync(Multiaddress[] addrs, CancellationToken token)
    {
        PeerId? remotePeerId = addrs.FirstOrDefault()?.GetPeerId();
        ISession? existingSession = Sessions.FirstOrDefault(s => s.State.RemotePeerId == remotePeerId);

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
        ISession? existingSession = Sessions.FirstOrDefault(s => s.State.RemotePeerId == peerId);

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

    internal IChannel Upgrade(Session session, ProtocolRef parentProtocol, IProtocol? upgradeProtocol, UpgradeOptions? options, bool isListener)
    {
        Channel downChannel = new();

        _ = Upgrade(session, downChannel.Reverse, parentProtocol, upgradeProtocol, options, isListener);

        return downChannel;
    }

    internal Task Upgrade(Session session, IChannel downChannel, ProtocolRef parentProtocol, IProtocol? upgradeProtocol, UpgradeOptions? options, bool isListener)
    {
        if (_protocolStackSettings.Protocols is null)
        {
            throw new Libp2pSetupException($"Protocols are not set in {nameof(_protocolStackSettings)}");
        }

        if (upgradeProtocol is not null && !_protocolStackSettings.Protocols[parentProtocol].Any(p => p.Protocol == upgradeProtocol))
        {
            _protocolStackSettings.Protocols.Add(new ProtocolRef(upgradeProtocol, false), []);
        }

        ProtocolRef top = upgradeProtocol is not null ?
            _protocolStackSettings.Protocols[parentProtocol].FirstOrDefault(p => p.Protocol == upgradeProtocol, _protocolStackSettings.Protocols.Keys.First(k => k.Protocol == upgradeProtocol)) :
            _protocolStackSettings.Protocols[parentProtocol].Single();

        isListener = options?.ModeOverride switch { UpgradeModeOverride.Dial => false, UpgradeModeOverride.Listen => true, _ => isListener };

        _logger?.LogInformation($"Upgrade and bind {parentProtocol} to {top}, listen={isListener}");

        Task upgradeTask;
        switch (top.Protocol)
        {
            case IConnectionProtocol tProto:
                {
                    ConnectionContext ctx = new(this, session, top, isListener, options);
                    upgradeTask = isListener ? tProto.ListenAsync(downChannel, ctx) : tProto.DialAsync(downChannel, ctx);
                    break;
                }
            case ISessionProtocol sProto:
                {
                    SessionContext ctx = new(this, session, top, isListener, options);
                    upgradeTask = isListener ? sProto.ListenAsync(downChannel, ctx) : sProto.DialAsync(downChannel, ctx);
                    break;
                }
            default:
                if (isListener && top.Protocol is ISessionListenerProtocol listenerProtocol)
                {
                    SessionContext ctx = new(this, session, top, isListener, options);
                    upgradeTask = listenerProtocol.ListenAsync(downChannel, ctx);
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

                    // Dynamically invoke DialAsync
                    System.Reflection.MethodInfo? dialAsyncMethod = genericInterface.GetMethod("DialAsync");
                    if (dialAsyncMethod != null)
                    {
                        SessionContext ctx = new(this, session, top, isListener, options);
                        upgradeTask = (Task)dialAsyncMethod.Invoke(top.Protocol, [downChannel, ctx, options?.Argument])!;
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

        return upgradeTask.ContinueWith(t =>
        {
            if (t.IsFaulted)
            {
                _logger?.LogError($"Upgrade task failed with {t.Exception}");
            }
            _ = downChannel.CloseAsync();
            _logger?.LogInformation($"Finished#2 {parentProtocol} to {top}, listen={isListener}");
        });
    }

    public Task DisconnectAsync() => Task.WhenAll(Sessions.ToArray().Select(s => s.DisconnectAsync()));
}
