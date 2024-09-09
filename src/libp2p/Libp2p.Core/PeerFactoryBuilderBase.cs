// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.DependencyInjection;

namespace Nethermind.Libp2p.Core;

public interface ICreateProtocolInstance
{
    IProtocol CreateProtocolInstance<TProtocol>(IServiceProvider serviceProvider, TProtocol? instance = default) where TProtocol : IProtocol;
}

public abstract class PeerFactoryBuilderBase<TBuilder, TPeerFactory> : IPeerFactoryBuilder, ICreateProtocolInstance
    where TBuilder : PeerFactoryBuilderBase<TBuilder, TPeerFactory>, IPeerFactoryBuilder
    where TPeerFactory : PeerFactory
{
    private HashSet<IProtocol> protocols = new();

    public IProtocol CreateProtocolInstance<TProtocol>(IServiceProvider serviceProvider, TProtocol? instance = default) where TProtocol : IProtocol
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
        return existing;
    }

    private readonly List<IProtocol> _appLayerProtocols = new();
    public IEnumerable<IProtocol> AppLayerProtocols { get => _appLayerProtocols; }

    internal readonly IServiceProvider ServiceProvider;

    protected readonly ProtocolStack? _stack;

    protected PeerFactoryBuilderBase(IServiceProvider? serviceProvider = default)
    {
        ServiceProvider = serviceProvider ?? new ServiceCollection().BuildServiceProvider();
    }

    protected ProtocolStack Over<TProtocol>(TProtocol? instance = default) where TProtocol : IProtocol
    {
        ProtocolStack result = new ProtocolStack(this, ServiceProvider, CreateProtocolInstance(ServiceProvider, instance), this);
        result.Root = result;
        return result;
    }

    public IPeerFactoryBuilder AddAppLayerProtocol<TProtocol>(TProtocol? instance = default) where TProtocol : IProtocol
    {
        _appLayerProtocols.Add(CreateProtocolInstance(ServiceProvider!, instance));
        return (TBuilder)this;
    }

    protected class ProtocolStack
    {
        private readonly IPeerFactoryBuilder builder;
        private readonly IServiceProvider serviceProvider;
        private readonly ICreateProtocolInstance createProtocolInstance;

        public ProtocolStack? Root { get; set; }
        public ProtocolStack? Parent { get; private set; }
        public ProtocolStack? PrevSwitch { get; private set; }
        public IProtocol Protocol { get; }
        public HashSet<ProtocolStack> TopProtocols { get; } = new();
        public ChannelFactory UpChannelsFactory { get; }

        public ProtocolStack(IPeerFactoryBuilder builder, IServiceProvider serviceProvider, IProtocol protocol, ICreateProtocolInstance createProtocolInstance)
        {
            this.builder = builder;
            this.serviceProvider = serviceProvider;
            Protocol = protocol;
            this.createProtocolInstance = createProtocolInstance;
            UpChannelsFactory = ActivatorUtilities.GetServiceOrCreateInstance<ChannelFactory>(serviceProvider);
        }

        public ProtocolStack AddAppLayerProtocol<TProtocol>(TProtocol? instance = default) where TProtocol : IProtocol
        {
            builder.AddAppLayerProtocol(instance);
            return this;
        }

        public ProtocolStack Over<TProtocol>(TProtocol? instance = default) where TProtocol : IProtocol
        {
            ProtocolStack nextNode = new(builder, serviceProvider, createProtocolInstance.CreateProtocolInstance(serviceProvider!, instance), createProtocolInstance);
            return Over(nextNode);
        }

        public ProtocolStack Or<TProtocol>(TProtocol? instance = default) where TProtocol : IProtocol
        {
            if (Parent is null)
            {
                throw new NotImplementedException();
            }
            IProtocol protocol = createProtocolInstance.CreateProtocolInstance(serviceProvider!, instance);
            ProtocolStack stack = new(builder, serviceProvider, protocol, createProtocolInstance);
            return Or(stack);
        }

        public ProtocolStack Over(ProtocolStack stack)
        {
            PeerFactoryBuilderBase<TBuilder, TPeerFactory>.ProtocolStack rootProto = stack.Root ?? stack;
            TopProtocols.Add(rootProto);

            if (PrevSwitch != null)
            {
                PrevSwitch.Over(stack);
            }

            rootProto.Root = stack.Root = Root ?? this;
            rootProto.Parent = this;

            return stack;
        }

        public ProtocolStack Or(ProtocolStack stack)
        {
            if (Parent is null)
            {
                throw new NotImplementedException();
            }
            stack.PrevSwitch = this;
            return Parent.Over(stack);
        }

        public override string ToString()
        {
            return $"{Protocol.Id}({TopProtocols.Count}): {string.Join(" or ", TopProtocols.Select(p => p.Protocol.Id))}";
        }
    }

    protected abstract ProtocolStack BuildStack();

    public IPeerFactory Build()
    {
        ProtocolStack transportLayer = BuildStack();
        ProtocolStack? appLayer = default;

        foreach (IProtocol appLayerProtocol in _appLayerProtocols)
        {
            appLayer = appLayer is null ? transportLayer.Over(appLayerProtocol) : appLayer.Or(appLayerProtocol);
        }

        ProtocolStack? root = transportLayer.Root;

        if (root?.Protocol is null || root.UpChannelsFactory is null)
        {
            throw new ApplicationException("Root protocol is not properly defined");
        }

        static void SetupChannelFactories(ProtocolStack root)
        {
            root.UpChannelsFactory.Setup(new Dictionary<IProtocol, IChannelFactory>(root.TopProtocols
                     .Select(p => new KeyValuePair<IProtocol, IChannelFactory>(p.Protocol, p.UpChannelsFactory))));
            foreach (ProtocolStack topProto in root.TopProtocols)
            {
                if (!root.TopProtocols.Any())
                {
                    return;
                }
                SetupChannelFactories(topProto);
            }
        }

        SetupChannelFactories(root);

        TPeerFactory result = ActivatorUtilities.GetServiceOrCreateInstance<TPeerFactory>(ServiceProvider);
        result.Setup(root?.Protocol!, root!.UpChannelsFactory);
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
