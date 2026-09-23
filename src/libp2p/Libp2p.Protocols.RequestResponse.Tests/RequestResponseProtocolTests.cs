// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using Google.Protobuf;
using Google.Protobuf.Reflection;
using Google.Protobuf.WellKnownTypes;
using Nethermind.Libp2p.Core;
using NUnit.Framework;
using NSubstitute;
using ChannelClosedException = Nethermind.Libp2p.Core.Exceptions.ChannelClosedException;

namespace Nethermind.Libp2p.Protocols.Tests;

public class TestRequest : IMessage<TestRequest>
{
    public string Message { get; set; } = string.Empty;
    public int Value { get; set; }

    public static MessageParser<TestRequest> Parser { get; } =
        new MessageParser<TestRequest>(() => new TestRequest());
    public static MessageDescriptor StaticDescriptor { get; } = null!;
    MessageDescriptor IMessage.Descriptor => StaticDescriptor;

    public TestRequest Clone() => new TestRequest { Message = Message, Value = Value };
    public bool Equals(TestRequest? other) => other != null && Message == other.Message && Value == other.Value;
    public void MergeFrom(TestRequest message) { Message = message.Message; Value = message.Value; }
    public void MergeFrom(CodedInputStream input) { Message = input.ReadString(); Value = input.ReadInt32(); }
    public void WriteTo(CodedOutputStream output) { output.WriteString(Message); output.WriteInt32(Value); }
    public int CalculateSize() => CodedOutputStream.ComputeStringSize(Message) + CodedOutputStream.ComputeInt32Size(Value);
}

// Mock interface initialization for IMessage->TestResponse
public class TestResponse : IMessage<TestResponse>
{
    public string Echo { get; set; } = string.Empty;
    public int ProcessedValue { get; set; }

    public static MessageParser<TestResponse> Parser { get; } =
        new MessageParser<TestResponse>(() => new TestResponse());
    public static MessageDescriptor StaticDescriptor { get; } = null!;
    MessageDescriptor IMessage.Descriptor => StaticDescriptor;

    public TestResponse Clone() => new TestResponse { Echo = Echo, ProcessedValue = ProcessedValue };
    public bool Equals(TestResponse? other) =>
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

public class RequestResponseProtocolTests
{
    [Test]
    public async Task DialAndListen_ReturnResponseByDefault()
    {
        var protocol = new RequestResponseProtocol<StringValue, StringValue>(
            "/test/1.0.0", (request, _) => Task.FromResult(new StringValue
            {
                Value = request.Value + "!"
            }));
        var channel = new Channel();
        var context = Substitute.For<ISessionContext>();
        var listen = protocol.ListenAsync(channel.Reverse, context);

        var response = await protocol.DialAsync(channel, context, new StringValue { Value = "hello" })
            .WaitAsync(TimeSpan.FromSeconds(2));
        await listen.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.That(response.Value, Is.EqualTo("hello!"));
    }

    [Test]
    public async Task ListenAsync_ThrowsWhenResponseWriteEnds()
    {
        TaskCompletionSource responseReady = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource resumeHandler = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var protocol = new RequestResponseProtocol<StringValue, StringValue>(
            "/test/1.0.0", async (request, _) =>
            {
                responseReady.SetResult();
                await resumeHandler.Task;
                return new StringValue { Value = request.Value };
            });
        var channel = new Channel();
        var context = Substitute.For<ISessionContext>();
        Task listen = protocol.ListenAsync(channel.Reverse, context);

        await ((IWriter)channel).WriteSizeAndDataAsync(new StringValue { Value = "hello" }.ToByteArray()).OrThrow();
        await responseReady.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await channel.Reverse.WriteEofAsync();
        resumeHandler.SetResult();

        Assert.ThrowsAsync<ChannelClosedException>(async () => await listen.WaitAsync(TimeSpan.FromSeconds(2)));
    }

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

        var protocol = new RequestResponseProtocol<TestRequest, TestResponse>(
            protocolId, handler);

        Assert.That(protocol.Id, Is.EqualTo(protocolId));

        // Deserialization handler check
        var sampleRequest = new TestRequest { Message = "hi", Value = 5 };
        var result = await handler(sampleRequest, Substitute.For<ISessionContext>());

        Assert.That(result.Echo, Is.EqualTo("hi"));
        Assert.That(result.ProcessedValue, Is.EqualTo(10));
    }

    // ToDo : Add more tests.
}

[TestFixture]
public class RequestResponseExtensionsTests
{
    [Test]
    public void AddRequestResponseProtocol_RegistersProtocolCorrectly()
    {
        const string protocolId = "test-extension-protocol";
        var mockBuilder = Substitute.For<IPeerFactoryBuilder>();

        mockBuilder.AddProtocol(Arg.Any<IProtocol>(), Arg.Any<bool>()).Returns(mockBuilder);

        var handler = new Func<TestRequest, ISessionContext, Task<TestResponse>>((req, ctx) =>
            Task.FromResult(new TestResponse
            {
                Echo = req.Message,
                ProcessedValue = req.Value * 2,
            }));

        var result = mockBuilder.AddRequestResponseProtocol<TestRequest, TestResponse>(
            protocolId,
            handler,
            isExposed: true);

        Assert.That(result, Is.EqualTo(mockBuilder), "Extension method should return the same builder with the protocol instance added.");

        // Verify that AddProtocol was called with a RequestResponseProtocol instance
        mockBuilder.Received(1).AddProtocol(
            Arg.Is<RequestResponseProtocol<TestRequest, TestResponse>>(p => p.Id == protocolId),
            Arg.Is<bool>(exposed => exposed == true));
    }

    [Test]
    public void AddRequestResponseProtocol_NullProtocolId_ThrowsArgumentNullException()
    {
        var mockBuilder = Substitute.For<IPeerFactoryBuilder>();
        var handler = new Func<TestRequest, ISessionContext, Task<TestResponse>>((req, ctx) =>
            Task.FromResult(new TestResponse()));

        Assert.That(() =>
            mockBuilder.AddRequestResponseProtocol<TestRequest, TestResponse>(
                null!,
                handler),
            Throws.TypeOf<ArgumentNullException>());
    }

    [Test]
    public void AddRequestResponseProtocol_EmptyProtocolId_CreatesProtocolWithEmptyId()
    {
        const string protocolId = "";
        var mockBuilder = Substitute.For<IPeerFactoryBuilder>();
        mockBuilder.AddProtocol(Arg.Any<IProtocol>(), Arg.Any<bool>()).Returns(mockBuilder);

        var handler = new Func<TestRequest, ISessionContext, Task<TestResponse>>((req, ctx) =>
            Task.FromResult(new TestResponse()));

        var result = mockBuilder.AddRequestResponseProtocol<TestRequest, TestResponse>(
            protocolId,
            handler);

        Assert.That(result, Is.EqualTo(mockBuilder));

        // Verify that AddProtocol was called with empty protocol ID
        mockBuilder.Received(1).AddProtocol(
            Arg.Is<RequestResponseProtocol<TestRequest, TestResponse>>(p => p.Id == protocolId),
            Arg.Is<bool>(exposed => exposed == true));
    }
}
