// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.DependencyInjection;

namespace Nethermind.Libp2p.Core;

public abstract class PeerFactoryBuilderBase<TBuilder, TPeerFactory>
    where TBuilder : PeerFactoryBuilderBase<TBuilder, TPeerFactory>, IPeerFactoryBuilder
    where TPeerFactory : PeerFactory
{
    private readonly List<IProtocol> _appLayerProtocols = new();

    private readonly Stack<Layer> _protocolStack = new();
    // ReSharper disable once MemberCanBePrivate.Global
    protected readonly IServiceProvider ServiceProvider;

    private bool _isSelecting;

    protected PeerFactoryBuilderBase(IServiceProvider? serviceProvider = default)
    {
        ServiceProvider = serviceProvider ?? new ServiceCollection().BuildServiceProvider();
    }

    public IPeerFactoryBuilder AddAppLayerProtocol<TProtocol>(TProtocol? instance = default) where TProtocol : IProtocol
    {
        _appLayerProtocols.Add(instance is null
            ? ActivatorUtilities.CreateInstance<TProtocol>(ServiceProvider)
            : instance);
        return (TBuilder)this;
    }

    protected TBuilder Over<TProtocol>() where TProtocol : IProtocol
    {
        TProtocol newProtocol = ActivatorUtilities.CreateInstance<TProtocol>(ServiceProvider);
        if (_isSelecting)
        {
            if (_protocolStack.Peek().Protocols.Count == 0)
            {
                _protocolStack.Peek().Protocols.Add(newProtocol);
            }
            else
            {
                _isSelecting = false;
                _protocolStack.Push(new Layer());
                _protocolStack.Peek().Protocols.Add(newProtocol);
            }
        }
        else
        {
            _protocolStack.Push(new Layer());
            _protocolStack.Peek().Protocols.Add(newProtocol);
        }

        return (TBuilder)this;
    }

    protected TBuilder Select<TProtocol>() where TProtocol : IProtocol
    {
        Over<TProtocol>();
        _isSelecting = true;
        _protocolStack.Peek().IsSelector = true;
        _protocolStack.Push(new Layer());
        return (TBuilder)this;
    }

    protected TBuilder Or<TProtocol>() where TProtocol : IProtocol
    {
        TProtocol newProtocol = ActivatorUtilities.CreateInstance<TProtocol>(ServiceProvider);
        _protocolStack.Peek().Protocols.Add(newProtocol);
        return (TBuilder)this;
    }

    private TBuilder Or(IProtocol protocol)
    {
        _protocolStack.Peek().Protocols.Add(protocol);
        return (TBuilder)this;
    }

    protected abstract IPeerFactoryBuilder BuildTransportLayer();

    public IPeerFactory Build()
    {
        TBuilder appLayer = (TBuilder)BuildTransportLayer();
        foreach (IProtocol appLyaerProtocol in _appLayerProtocols)
        {
            appLayer = appLayer.Or(appLyaerProtocol);
        }

        Layer? prevLayer = null;
        ChannelFactory? prevFactory = null;
        ChannelFactory? preselectProtocolFactory = null;

        while (_protocolStack.TryPop(out Layer? topLayer))
        {
            if (prevLayer is not null)
            {
                ChannelFactory factory = ActivatorUtilities.CreateInstance<ChannelFactory>(ServiceProvider);
                factory.Connect(topLayer.Protocols.First(), prevFactory, prevLayer.Protocols.ToArray());
                prevFactory = factory;
            }

            if (preselectProtocolFactory is null && prevLayer?.IsSelector == true)
            {
                preselectProtocolFactory = prevFactory;
            }

            prevLayer = topLayer;
        }

        TPeerFactory result = ActivatorUtilities.CreateInstance<TPeerFactory>(ServiceProvider);
        result.Connect(prevFactory, preselectProtocolFactory, prevLayer.Protocols.First(), _appLayerProtocols);
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
