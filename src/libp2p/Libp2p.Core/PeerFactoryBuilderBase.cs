// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.DependencyInjection;
using Nethermind.Libp2p.Stack;

namespace Nethermind.Libp2p.Core;

public static class PeerFactoryBuilderBase
{
    private static HashSet<IProtocol> protocols = new();

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

public class ProtocolRef(IProtocol protocol)
{
    static int IdCounter = 0;
    public string RefId { get; } = Interlocked.Increment(ref IdCounter).ToString();
    public IProtocol Protocol => protocol;

    public override string ToString()
    {
        return $"ref#{RefId}({Protocol.Id})";
    }
}

public abstract class PeerFactoryBuilderBase<TBuilder, TPeerFactory> : IPeerFactoryBuilder
    where TBuilder : PeerFactoryBuilderBase<TBuilder, TPeerFactory>, IPeerFactoryBuilder
    where TPeerFactory : IPeerFactory
{
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
        return new ProtocolStack(this, ServiceProvider, new ProtocolRef(PeerFactoryBuilderBase.CreateProtocolInstance(ServiceProvider, instance)));
    }

    public IPeerFactoryBuilder AddAppLayerProtocol<TProtocol>(TProtocol? instance = default) where TProtocol : IProtocol
    {
        _appLayerProtocols.Add(PeerFactoryBuilderBase.CreateProtocolInstance(ServiceProvider!, instance));
        return (TBuilder)this;
    }

    protected class ProtocolStack
    {
        private readonly IPeerFactoryBuilder builder;
        private readonly IServiceProvider serviceProvider;

        public ProtocolStack? Root { get; private set; }
        public ProtocolStack Parent { get; private set; }
        public ProtocolStack? PrevSwitch { get; private set; }
        public ProtocolRef Protocol { get; }
        public HashSet<ProtocolStack> TopProtocols { get; } = new();

        public ProtocolStack(IPeerFactoryBuilder builder, IServiceProvider serviceProvider, ProtocolRef protocol)
        {
            this.builder = builder;
            this.serviceProvider = serviceProvider;
            Protocol = protocol;
        }

        public ProtocolStack AddAppLayerProtocol<TProtocol>(TProtocol? instance = default) where TProtocol : IProtocol
        {
            builder.AddAppLayerProtocol(instance);
            return this;
        }

        public ProtocolStack Over<TProtocol>(TProtocol? instance = default) where TProtocol : IProtocol
        {
            ProtocolStack nextNode = new(builder, serviceProvider, new ProtocolRef(PeerFactoryBuilderBase.CreateProtocolInstance(serviceProvider!, instance)));
            return Over(nextNode);
        }

        public ProtocolStack Or<TProtocol>(TProtocol? instance = default) where TProtocol : IProtocol
        {
            if (Parent is null)
            {
                Parent = new ProtocolStack(builder, serviceProvider, new ProtocolRef(RootStub.Instance));
                Parent.Over(this);
            }
            ProtocolRef protocol = new ProtocolRef(PeerFactoryBuilderBase.CreateProtocolInstance(serviceProvider!, instance));
            ProtocolStack stack = new(builder, serviceProvider, protocol);
            return Or(stack);
        }

        public ProtocolStack Over(ProtocolStack stack)
        {
            ProtocolStack rootProto = stack.Root ?? stack;
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
                Parent = new ProtocolStack(builder, serviceProvider, new ProtocolRef(RootStub.Instance));
                Parent.Over(this);
            }
            stack.PrevSwitch = this;
            return Parent.Over(stack);
        }

        public override string ToString()
        {
            return $"{Protocol}({TopProtocols.Count}): {string.Join(" or ", TopProtocols.Select(p => p.Protocol))}";
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

        if (root?.Protocol is null)
        {
            throw new ApplicationException("Root protocol is not properly defined");
        }

        Dictionary<ProtocolRef, ProtocolRef[]> protocols = new();

        void SetupChannelFactories(ProtocolStack root)
        {
            protocols.TryAdd(root.Protocol, root.TopProtocols.Select(p => p.Protocol).ToArray());
            //root.UpChannelsFactory.Setup(new Dictionary<IProtocol, IChannelFactory>(root.TopProtocols
            //         .Select(p => new KeyValuePair<IProtocol, IChannelFactory>(p.Protocol, p.UpChannelsFactory))));
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

        IProtocolStackSettings protocolStackSettings = ActivatorUtilities.GetServiceOrCreateInstance<IProtocolStackSettings>(ServiceProvider);
        protocolStackSettings.Protocols = protocols;
        protocolStackSettings.TopProtocols = root.Protocol.Protocol is RootStub ? root.TopProtocols.Select(s => s.Protocol).ToArray() : [root?.Protocol];

        TPeerFactory result = ActivatorUtilities.GetServiceOrCreateInstance<TPeerFactory>(ServiceProvider);

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

    class RootStub : IProtocol
    {
        public static RootStub Instance { get; } = new();

        private RootStub() { }

        public string Id => "protocol hierachy root";
    }
}
