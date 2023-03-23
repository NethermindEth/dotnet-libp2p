// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.DependencyInjection;

namespace Nethermind.Libp2p.Core;

public abstract class PeerFactoryBuilderBase<TBuilder, TPeerFactory>
    where TBuilder : PeerFactoryBuilderBase<TBuilder, TPeerFactory>, IPeerFactoryBuilder
    where TPeerFactory : PeerFactory
{
    private readonly List<Func<IProtocol>> _appLayerProtocols = new();

    private readonly Stack<Layer> protocolStack = new();
    protected readonly IServiceProvider ServiceProvider;

    private IProtocol? inititalProtocol = null;
    private IChannelFactory? inititalProtocolFactory = null;

    private bool isSelecting;
    private IChannelFactory? preselectProtocolFactory = null;

    protected PeerFactoryBuilderBase(IServiceProvider? serviceProvider = default)
    {
        ServiceProvider = serviceProvider ?? new ServiceCollection().BuildServiceProvider();
    }

    public IPeerFactoryBuilder AddAppLayerProtocol<TProtocol>(TProtocol? instance = default) where TProtocol : IProtocol
    {
        _appLayerProtocols.Add(instance is null
            ? () => ActivatorUtilities.CreateInstance<TProtocol>(ServiceProvider)
            : () => instance);
        return (TBuilder)this;
    }

    protected TBuilder Over<TProtocol>() where TProtocol : IProtocol
    {
        TProtocol newProtocol = ActivatorUtilities.CreateInstance<TProtocol>(ServiceProvider);
        if (isSelecting)
        {
            if (protocolStack.Peek().Protocols.Count == 0)
            {
                protocolStack.Peek().Protocols.Add(newProtocol);
            }
            else
            {
                isSelecting = false;
                protocolStack.Push(new Layer());
                protocolStack.Peek().Protocols.Add(newProtocol);
            }
        }
        else
        {
            protocolStack.Push(new Layer());
            protocolStack.Peek().Protocols.Add(newProtocol);
        }

        return (TBuilder)this;
    }

    protected TBuilder Select<TProtocol>() where TProtocol : IProtocol
    {
        Over<TProtocol>();
        isSelecting = true;
        protocolStack.Peek().IsSelector = true;
        protocolStack.Push(new Layer());
        return (TBuilder)this;
    }

    protected TBuilder Or<TProtocol>() where TProtocol : IProtocol
    {
        TProtocol newProtocol = ActivatorUtilities.CreateInstance<TProtocol>(ServiceProvider);
        protocolStack.Peek().Protocols.Add(newProtocol);
        return (TBuilder)this;
    }

    private TBuilder Or(Func<IProtocol> protoFactory)
    {
        IProtocol newProtocol = protoFactory();
        protocolStack.Peek().Protocols.Add(newProtocol);
        return (TBuilder)this;
    }

    protected abstract TBuilder BuildTransportLayer();

    public IPeerFactory Build()
    {
        TBuilder appLayer = BuildTransportLayer();
        foreach (Func<IProtocol> protoFactory in _appLayerProtocols)
        {
            appLayer = appLayer.Or(protoFactory);
        }

        Layer? prevLayer = null;
        ChannelFactory? prevFactory = null;
        Layer? topLayer = null;
        ChannelFactory? preselectProtocolFactory = null;

        while (protocolStack.TryPop(out topLayer))
        {
            if (prevLayer is not null)
            {
                ChannelFactory factory = ActivatorUtilities.CreateInstance<ChannelFactory>(ServiceProvider)
                    .Connect(topLayer.Protocols.First(), prevFactory, prevLayer.Protocols.ToArray());
                prevFactory = factory;
            }

            if (preselectProtocolFactory is null && prevLayer?.IsSelector == true)
            {
                preselectProtocolFactory = prevFactory;
            }

            prevLayer = topLayer;
        }

        TPeerFactory result = ActivatorUtilities.CreateInstance<TPeerFactory>(ServiceProvider);
        ;
        result.Connect(prevFactory, preselectProtocolFactory, prevLayer.Protocols.First());
        return result;
    }

    private class Layer
    {
        public List<IProtocol> Protocols { get; } = new();
        public bool IsSelector { get; set; }

        public override string ToString()
        {
            return (IsSelector ? "(selector)" : "") + string.Join(",", Protocols.Select(p => p.Id));
        }
    }
}
