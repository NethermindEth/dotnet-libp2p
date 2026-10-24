// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.DependencyInjection;
using System.Diagnostics.CodeAnalysis;

namespace Nethermind.Libp2p.Core;

public class ProtocolRef(IProtocol protocol, bool isExposed = true, InboundStreamLimit? inboundLimit = null)
{
    public ProtocolRef(IProtocol protocol, bool isExposed) : this(protocol, isExposed, null) { }

    static int RefIdCounter = 0;

    public string RefId { get; } = Interlocked.Increment(ref RefIdCounter).ToString();
    public IProtocol Protocol => protocol;
    public bool IsExposed => isExposed;

    /// <summary>Whether listeners may negotiate this registration. Outbound dialing remains available.</summary>
    public bool AcceptInbound { get; init; } = true;

    /// <summary>Caps inbound streams for this registration, or null to leave them uncapped.</summary>
    public InboundStreamLimit? InboundLimit => inboundLimit;

    public string Id => Protocol.Id;

    public override string ToString()
    {
        return $"ref#{RefId}({Protocol.Id})";
    }

    public static implicit operator ProtocolRef[](ProtocolRef pr) => [pr];
}


public abstract class PeerFactoryBuilderBase<TBuilder, [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] TPeerFactory> : IPeerFactoryBuilder, IPeerServiceBuilder, IProtocolFactoryBuilder, IDisposable, IAsyncDisposable
    where TBuilder : PeerFactoryBuilderBase<TBuilder, TPeerFactory>, IPeerFactoryBuilder
    where TPeerFactory : IPeerFactory
{
    private readonly HashSet<IProtocol> protocolInstances = [];
    private readonly List<object> _activated = [];

    private TProtocol CreateProtocolInstance<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] TProtocol>(IServiceProvider serviceProvider, TProtocol? instance = default) where TProtocol : IProtocol
    {
        if (instance is not null)
        {
            protocolInstances.Add(instance);
        }

        IProtocol? existing = instance ?? protocolInstances.OfType<TProtocol>().FirstOrDefault();
        if (existing is null)
        {
            existing = serviceProvider.GetService<TProtocol>();
            if (existing is null)
            {
                existing = ActivatorUtilities.CreateInstance<TProtocol>(serviceProvider);
                if (existing is IDisposable or IAsyncDisposable) _activated.Add(existing);
            }
            protocolInstances.Add(existing);
        }
        return (TProtocol)existing;
    }


    private readonly List<Func<ProtocolRef>> _appLayerProtocols = [];
    private readonly Dictionary<Type, PeerServiceRegistration> _peerServices = [];
    private readonly ServiceCollection _services = new();
    private readonly IServiceProvider? _suppliedProvider;
    private BuilderServiceProvider? _builtProvider;
    private IPeerFactory? _factory;
    private bool _disposed;

    public IServiceProvider ServiceProvider { protected set; get; }

    /// <summary>
    /// Internal service collection used for protocol registration.
    /// Exposed via ILibp2pPeerFactoryBuilder for advanced protocol integration.
    /// </summary>
    protected internal IServiceCollection InternalServices { get; private set; }

    protected PeerFactoryBuilderBase(IServiceProvider? serviceProvider = default)
    {
        _suppliedProvider = serviceProvider;
        InternalServices = _services;
        if (serviceProvider?.GetService<IProtocolStackSettings>() is null)
            _services.AddSingleton<IProtocolStackSettings, ProtocolStackSettings>();
        ServiceProvider = serviceProvider ?? new ConfigurationServices();
    }

    public void AddPeerService<TService>(Func<ILocalPeer, IServiceProvider, TService> factory, bool ownsInstance = true) where TService : class
    {
        EnsureConfigurable();
        ArgumentNullException.ThrowIfNull(factory);
        _peerServices.Add(typeof(TService), new(peer => factory(peer, ServiceProvider), ownsInstance));
    }

    public void AddProtocolFactory(Func<IServiceProvider, ProtocolRef> factory)
    {
        EnsureConfigurable();
        ArgumentNullException.ThrowIfNull(factory);
        _appLayerProtocols.Add(() => factory(ServiceProvider));
    }

    protected void EnsureConfigurable()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_factory is not null) throw new InvalidOperationException("The builder has already been built.");
    }

    public IPeerFactoryBuilder AddProtocol<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] TProtocol>(TProtocol? instance, bool isExposed) where TProtocol : IProtocol
        => AddProtocol(instance, isExposed, null);

    public IPeerFactoryBuilder AddProtocol<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] TProtocol>(TProtocol? instance = default, bool isExposed = true, InboundStreamLimit? inboundLimit = null) where TProtocol : IProtocol
    {
        EnsureConfigurable();
        _appLayerProtocols.Add(() => new ProtocolRef(CreateProtocolInstance(ServiceProvider, instance), isExposed, inboundLimit));
        return (TBuilder)this;
    }

    protected abstract ProtocolRef[] BuildStack(IEnumerable<ProtocolRef> additionalProtocols);

    private readonly Dictionary<ProtocolRef, ProtocolRef[]> protocols = [];

    protected ProtocolRef[] Connect(ProtocolRef[] protocols, params ProtocolRef[][] upgradeToStacks)
    {
        ProtocolRef[] previous = protocols;
        foreach (ProtocolRef[] upgradeTo in upgradeToStacks)
        {
            foreach (ProtocolRef protocolRef in previous)
            {
                this.protocols[protocolRef] = upgradeTo;

                foreach (ProtocolRef upgradeToRef in upgradeTo)
                {
                    this.protocols.TryAdd(upgradeToRef, []);
                }
            }
            previous = upgradeTo;
        }

        return previous;
    }

    protected ProtocolRef Get<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] TProtocol>() where TProtocol : IProtocol
    {
        return new ProtocolRef(CreateProtocolInstance<TProtocol>(ServiceProvider));
    }

    public IPeerFactory Build()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_factory is not null) return _factory;
        IServiceProvider configurationProvider = _suppliedProvider ?? ServiceProvider;
        _builtProvider = new BuilderServiceProvider(_services, configurationProvider);
        ServiceProvider = _builtProvider;
        try
        {
            IProtocolStackSettings protocolStackSettings = ServiceProvider.GetRequiredService<IProtocolStackSettings>();
            PeerServiceRegistry.Configure(protocolStackSettings, _peerServices);
            protocolStackSettings.TopProtocols = BuildStack(_appLayerProtocols.Select(create => create()).ToArray());
            protocolStackSettings.Protocols = protocols;
            TPeerFactory result = ActivatorUtilities.GetServiceOrCreateInstance<TPeerFactory>(ServiceProvider);
            _services.MakeReadOnly();
            return _factory = result;
        }
        catch
        {
            try { DisposeActivatedAsync().AsTask().GetAwaiter().GetResult(); }
            finally
            {
                _builtProvider.Dispose();
                _builtProvider = null;
                ServiceProvider = configurationProvider;
                protocolInstances.Clear();
                protocols.Clear();
            }
            throw;
        }
    }

    private sealed class ConfigurationServices : IServiceProvider
    {
        public object? GetService(Type type) => null;
    }

    public void Dispose() => DisposeAsync().AsTask().GetAwaiter().GetResult();

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        try { await DisposeActivatedAsync(); }
        finally { if (_builtProvider is not null) await _builtProvider.DisposeAsync(); }
    }

    private async ValueTask DisposeActivatedAsync()
    {
        List<Exception> failures = [];
        foreach (object instance in _activated.AsEnumerable().Reverse())
        {
            try
            {
                if (instance is IAsyncDisposable asyncDisposable) await asyncDisposable.DisposeAsync();
                else ((IDisposable)instance).Dispose();
            }
            catch (Exception ex) { failures.Add(ex); }
        }
        _activated.Clear();
        if (failures.Count > 0) throw new AggregateException(failures);
    }

}
