// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.DependencyInjection;
using Nethermind.Libp2p.Stack;

namespace Nethermind.Libp2p.Core;

public static class PeerFactoryBuilderBase
{
    private static readonly HashSet<IProtocol> protocols = [];

    internal static TProtocol CreateProtocolInstance<TProtocol>(IServiceProvider serviceProvider, TProtocol? instance = default) where TProtocol : IProtocol
    {
        if (instance is not null)
        {
            protocols.Add(instance);
        }

        IProtocol? existing = instance ?? protocols.OfType<TProtocol>().FirstOrDefault();
        if (existing is null)
        {
            existing = ActivatorUtilities.GetServiceOrCreateInstance<TProtocol>(serviceProvider);
            protocols.Add(existing);
        }
        return (TProtocol)existing;
    }
}

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
    private readonly List<ProtocolRef> _appLayerProtocols = new();
    public IEnumerable<IProtocol> AppLayerProtocols => _appLayerProtocols.Select(x => x.Protocol);

    internal readonly IServiceProvider ServiceProvider;

    protected PeerFactoryBuilderBase(IServiceProvider? serviceProvider = default)
    {
        ServiceProvider = serviceProvider ?? new ServiceCollection().BuildServiceProvider();
    }

    public IPeerFactoryBuilder AddAppLayerProtocol<TProtocol>(TProtocol? instance = default, bool isExposed = true) where TProtocol : IProtocol
    {
        _appLayerProtocols.Add(new ProtocolRef(PeerFactoryBuilderBase.CreateProtocolInstance(ServiceProvider!, instance), isExposed));
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
        return new ProtocolRef(PeerFactoryBuilderBase.CreateProtocolInstance<TProtocol>(ServiceProvider));
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
