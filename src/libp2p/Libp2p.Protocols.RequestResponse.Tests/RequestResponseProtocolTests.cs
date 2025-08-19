// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using NUnit.Framework;
using Google.Protobuf;
using Google.Protobuf.Reflection;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Nethermind.Libp2p.Core;
using System.Text;
using Nethermind.Libp2p.Core.TestsBase;

namespace Nethermind.Libp2p.Protocols.Tests;

public class TestRequest : IMessage<TestRequest>
{
    public string Message { get; set; }
    public int Value { get; set; }

    public static MessageParser<TestRequest> Parser { get; } =
        new MessageParser<TestRequest>(() => new TestRequest());
    public static MessageDescriptor StaticDescriptor { get; } = null;
    MessageDescriptor IMessage.Descriptor => StaticDescriptor;

    public TestRequest Clone() => new TestRequest { Message = Message, Value = Value };
    public bool Equals(TestRequest other) => other != null && Message == other.Message && Value == other.Value;
    public void MergeFrom(TestRequest message) { Message = message.Message; Value = message.Value; }
    public void MergeFrom(CodedInputStream input) { Message = input.ReadString(); Value = input.ReadInt32(); }
    public void WriteTo(CodedOutputStream output) { output.WriteString(Message); output.WriteInt32(Value); }
    public int CalculateSize() => CodedOutputStream.ComputeStringSize(Message) + CodedOutputStream.ComputeInt32Size(Value);
}

// Mock interface initialisation for IMessage->TestResponse
public class TestResponse : IMessage<TestResponse>
{
    public string Echo { get; set; }
    public int ProcessedValue { get; set; }

    public static MessageParser<TestResponse> Parser { get; } =
        new MessageParser<TestResponse>(() => new TestResponse());
    public static MessageDescriptor StaticDescriptor { get; } = null;
    MessageDescriptor IMessage.Descriptor => StaticDescriptor;

    public TestResponse Clone() => new TestResponse { Echo = Echo, ProcessedValue = ProcessedValue };
    public bool Equals(TestResponse other) =>
        other != null && Echo == other.Echo && ProcessedValue == other.ProcessedValue;
    public void MergeFrom(TestResponse message)
    {
        Echo = message.Echo;
        ProcessedValue = message.ProcessedValue;
    }
    public void MergeFrom(CodedInputStream input)
    {
        Echo = input.ReadString();
        ProcessedValue = input.ReadInt32();
    }
    public void WriteTo(CodedOutputStream output)
    {
        output.WriteString(Echo);
        output.WriteInt32(ProcessedValue);
    }
    public int CalculateSize() =>
        CodedOutputStream.ComputeStringSize(Echo) +
        CodedOutputStream.ComputeInt32Size(ProcessedValue);
}

public class GenericRequestResponseProtocolTests
{
    [Test]
    public async Task SetsPropertiesCorrectly()
    {
        const string protocolId = "test-protocol";

        var handler = new Func<TestRequest, ISessionContext, Task<TestResponse>>((req, ctx) =>
            Task.FromResult(new TestResponse
            {
                Echo = req.Message,
                ProcessedValue = req.Value * 2,
            }));

        var protocol = new GenericRequestResponseProtocol<TestRequest, TestResponse>(
            protocolId, handler);

        Assert.That(protocol.Id, Is.EqualTo(protocolId));

        // Deserialisation handler check
        var sampleRequest = new TestRequest { Message = "hi", Value = 5 };
        var result = await handler(sampleRequest, null);

        Assert.That(result.Echo, Is.EqualTo("hi"));
        Assert.That(result.ProcessedValue, Is.EqualTo(10));
    }

    // ToDo : Add more tests.
}

[TestFixture]
public class RequestResponseExtensionsTests
{
    [Test]
    public void AddGenericRequestResponseProtocol_RegistersProtocolCorrectly()
    {
        const string protocolId = "test-extension-protocol";
        var mockBuilder = Substitute.For<IPeerFactoryBuilder>();

        mockBuilder.AddAppLayerProtocol(Arg.Any<IProtocol>(), Arg.Any<bool>()).Returns(mockBuilder);

        var handler = new Func<TestRequest, ISessionContext, Task<TestResponse>>((req, ctx) =>
            Task.FromResult(new TestResponse
            {
                Echo = req.Message,
                ProcessedValue = req.Value * 2,
            }));

        var result = mockBuilder.AddGenericRequestResponseProtocol<TestRequest, TestResponse>(
            protocolId,
            handler,
            isExposed: true);

        Assert.That(result, Is.EqualTo(mockBuilder), "Extension method should return the same builder with the protocol instance added.");

        // Verify that AddAppLayerProtocol was called with a GenericRequestResponseProtocol instance
        mockBuilder.Received(1).AddAppLayerProtocol(
            Arg.Is<GenericRequestResponseProtocol<TestRequest, TestResponse>>(p => p.Id == protocolId),
            Arg.Is<bool>(exposed => exposed == true));
    }

    [Test]
    public void AddGenericRequestResponseProtocol_NullProtocolId_ThrowsArgumentNullException()
    {
        var mockBuilder = Substitute.For<IPeerFactoryBuilder>();
        var handler = new Func<TestRequest, ISessionContext, Task<TestResponse>>((req, ctx) =>
            Task.FromResult(new TestResponse()));

        Assert.Throws<ArgumentNullException>(() =>
            mockBuilder.AddGenericRequestResponseProtocol<TestRequest, TestResponse>(
                null!,
                handler));
    }

    [Test]
    public void AddGenericRequestResponseProtocol_EmptyProtocolId_CreatesProtocolWithEmptyId()
    {
        const string protocolId = "";
        var mockBuilder = Substitute.For<IPeerFactoryBuilder>();
        mockBuilder.AddAppLayerProtocol(Arg.Any<IProtocol>(), Arg.Any<bool>()).Returns(mockBuilder);

        var handler = new Func<TestRequest, ISessionContext, Task<TestResponse>>((req, ctx) =>
            Task.FromResult(new TestResponse()));

        var result = mockBuilder.AddGenericRequestResponseProtocol<TestRequest, TestResponse>(
            protocolId,
            handler);

        Assert.That(result, Is.EqualTo(mockBuilder));

        // Verify that AddAppLayerProtocol was called with empty protocol ID
        mockBuilder.Received(1).AddAppLayerProtocol(
            Arg.Is<GenericRequestResponseProtocol<TestRequest, TestResponse>>(p => p.Id == protocolId),
            Arg.Is<bool>(exposed => exposed == true));
    }
}
