// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.DependencyInjection;

namespace Nethermind.Libp2p.Core;

public class ProtocolRef(IProtocol protocol, bool isExposed = true)
{
    static int RefIdCounter = 0;

    public string RefId { get; } = Interlocked.Increment(ref RefIdCounter).ToString();
    public IProtocol Protocol => protocol;
    public bool IsExposed => isExposed;

    public string Id => Protocol.Id;

    public override string ToString()
    {
        return $"ref#{RefId}({Protocol.Id})";
    }
}


public abstract class PeerFactoryBuilderBase<TBuilder, TPeerFactory> : IPeerFactoryBuilder
    where TBuilder : PeerFactoryBuilderBase<TBuilder, TPeerFactory>, IPeerFactoryBuilder
    where TPeerFactory : IPeerFactory
{
    private readonly HashSet<IProtocol> protocolInstances = [];

    private TProtocol CreateProtocolInstance<TProtocol>(IServiceProvider serviceProvider, TProtocol? instance = default) where TProtocol : IProtocol
    {
        if (instance is not null)
        {
            protocolInstances.Add(instance);
        }

        IProtocol? existing = instance ?? protocolInstances.OfType<TProtocol>().FirstOrDefault();
        if (existing is null)
        {
            existing = ActivatorUtilities.GetServiceOrCreateInstance<TProtocol>(serviceProvider);
            protocolInstances.Add(existing);
        }
        return (TProtocol)existing;
    }


    private readonly List<ProtocolRef> _appLayerProtocols = [];
    public IEnumerable<IProtocol> AppLayerProtocols => _appLayerProtocols.Select(x => x.Protocol);

    internal readonly IServiceProvider ServiceProvider;

    protected PeerFactoryBuilderBase(IServiceProvider? serviceProvider = default)
    {
        ServiceProvider = serviceProvider ?? new ServiceCollection().BuildServiceProvider();
    }

    public IPeerFactoryBuilder AddAppLayerProtocol<TProtocol>(TProtocol? instance = default, bool isExposed = true) where TProtocol : IProtocol
    {
        _appLayerProtocols.Add(new ProtocolRef(CreateProtocolInstance(ServiceProvider!, instance), isExposed));
        return (TBuilder)this;
    }

    protected abstract ProtocolRef[] BuildStack(ProtocolRef[] additionalProtocols);

    private Dictionary<ProtocolRef, ProtocolRef[]> protocols = [];

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

    protected ProtocolRef Get<TProtocol>() where TProtocol : IProtocol
    {
        return new ProtocolRef(CreateProtocolInstance<TProtocol>(ServiceProvider));
    }

    public IPeerFactory Build()
    {
        IProtocolStackSettings protocolStackSettings = ActivatorUtilities.GetServiceOrCreateInstance<IProtocolStackSettings>(ServiceProvider);
        protocolStackSettings.TopProtocols = BuildStack(_appLayerProtocols.ToArray());
        protocolStackSettings.Protocols = protocols;

        TPeerFactory result = ActivatorUtilities.GetServiceOrCreateInstance<TPeerFactory>(ServiceProvider);

        return result;
    }
}
