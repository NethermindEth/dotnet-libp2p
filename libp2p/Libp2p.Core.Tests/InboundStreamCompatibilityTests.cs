// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using NUnit.Framework;

namespace Nethermind.Libp2p.Core.Tests;

public class InboundStreamCompatibilityTests
{
    [TestCase(typeof(IPeerFactoryBuilder))]
    [TestCase(typeof(PeerFactoryBuilderBase<,>))]
    public void ExistingBuilderSignatureIsRetained(Type builderType)
    {
        Assert.That(builderType.GetMethods().Any(m => m.Name == "AddProtocol" && m.GetParameters().Length == 2), Is.True,
            "Existing callers and builder implementations require the original two-parameter member.");
    }

    [Test]
    public void ExistingProtocolRegistrationConstructorIsRetained()
    {
        Assert.That(typeof(ProtocolRef).GetConstructor([typeof(IProtocol), typeof(bool)]), Is.Not.Null,
            "Compiled callers require the original two-parameter constructor.");
    }

    [Test]
    public void LegacyBuilderSupportsAllExistingCallForms()
    {
        LegacyBuilder legacy = new();
        IPeerFactoryBuilder builder = legacy;
        TestProtocol protocol = new();
        Assert.That(builder.AddProtocol<TestProtocol>(), Is.SameAs(builder));
        Assert.That(builder.AddProtocol(protocol, false), Is.SameAs(builder));
        Assert.That(legacy.LastProtocol, Is.SameAs(protocol));
        Assert.That(legacy.LastExposed, Is.False);
        builder.AddProtocol<TestProtocol>(isExposed: false);
        Assert.That(legacy.LastExposed, Is.False);
        builder.AddProtocol<TestProtocol>(null);
        builder.AddProtocol<TestProtocol>(inboundLimit: null);
        Assert.That(legacy.Calls, Is.EqualTo(5));
        Assert.Throws<NotSupportedException>(() => builder.AddProtocol<TestProtocol>(inboundLimit: new(1)));
        Assert.That(legacy.Calls, Is.EqualTo(5), "An unsupported limit must not silently register an unlimited protocol.");
    }

    private sealed class LegacyBuilder : IPeerFactoryBuilder
    {
        public int Calls { get; private set; }
        public IProtocol? LastProtocol { get; private set; }
        public bool LastExposed { get; private set; }
        public IServiceProvider ServiceProvider => throw new NotSupportedException();
        public IPeerFactory Build() => throw new NotSupportedException();
        public IPeerFactoryBuilder AddProtocol<TProtocol>(TProtocol? instance = default, bool isExposed = true) where TProtocol : IProtocol
        {
            Calls++;
            LastProtocol = instance;
            LastExposed = isExposed;
            return this;
        }
    }

    private sealed class TestProtocol : IProtocol
    {
        public string Id => "/test/legacy-builder";
    }
}
