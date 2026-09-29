// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using Google.Protobuf;
using Microsoft.Extensions.Logging;
using Multiformats.Address;
using Nethermind.Libp2p.Core.Discovery;
using Nethermind.Libp2p.Protocols.Pubsub.Dto;

namespace Nethermind.Libp2p.Protocols.Pubsub.Tests;

[TestFixture]
public class DeferredValidationTests
{
    private const string Topic = "deferred";

    [Test]
    public void Accept_DeliversAndForwardsOnlyAfterCompletion()
    {
        using PubsubRouter router = CreateRouter();
        List<byte[]> deliveries = [];
        router.GetTopic(Topic).OnMessage += (_, data) => deliveries.Add(data);
        List<Rpc> forwarded = ConnectForwardingPeer(router);
        Message? pending = null;
        TaskCompletionSource validationWork = new();
        router.VerifyMessage = (_, _) => MessageValidity.Deferred;
        router.OnDeferredMessage = (peer, message) =>
        {
            Assert.That(peer, Is.EqualTo(TestPeers.PeerId(1)));
            pending = message;
            return validationWork.Task;
        };

        Message message = NewMessage(1);
        router.OnRpc(TestPeers.PeerId(1), RpcWith(message));

        Assert.Multiple(() =>
        {
            Assert.That(deliveries, Is.Empty);
            Assert.That(forwarded, Is.Empty);
            Assert.That(router.PendingValidationCount, Is.EqualTo(1));
        });

        Assert.That(router.CompleteValidation(pending!, MessageValidity.Accepted), Is.True);
        validationWork.SetResult();
        Assert.Multiple(() =>
        {
            Assert.That(deliveries, Is.EqualTo(new[] { new byte[] { 1 } }));
            Assert.That(forwarded.SelectMany(rpc => rpc.Publish).Select(m => m.Data.ToByteArray()), Is.EqualTo(new[] { new byte[] { 1 } }));
            Assert.That(router.PendingValidationCount, Is.Zero);
            Assert.That(router.CompleteValidation(pending!, MessageValidity.Accepted), Is.False);
        });

        router.OnRpc(TestPeers.PeerId(1), RpcWith(message));
        Assert.That(deliveries, Has.Count.EqualTo(1));
    }

    [Test]
    public void Reject_PenalizesTheDeliveringPeerWithoutDeliveryOrForwarding()
    {
        PubsubSettings settings = Settings();
        settings.GraylistThreshold = -0.1;
        using PubsubRouter router = CreateRouter(settings);
        List<byte[]> deliveries = [];
        router.GetTopic(Topic).OnMessage += (_, data) => deliveries.Add(data);
        List<Rpc> forwarded = ConnectForwardingPeer(router);
        int validations = 0;
        Message? pending = null;
        TaskCompletionSource validationWork = new();
        router.VerifyMessage = (_, _) =>
        {
            validations++;
            return MessageValidity.Deferred;
        };
        router.OnDeferredMessage = (_, message) =>
        {
            pending = message;
            return validationWork.Task;
        };

        router.OnRpc(TestPeers.PeerId(1), RpcWith(NewMessage(1)));
        Assert.That(router.CompleteValidation(pending!, MessageValidity.Rejected), Is.True);
        validationWork.SetResult();
        router.OnRpc(TestPeers.PeerId(1), RpcWith(NewMessage(2)));

        Assert.Multiple(() =>
        {
            Assert.That(validations, Is.EqualTo(1), "The rejection must lower the source peer's score below the graylist threshold.");
            Assert.That(deliveries, Is.Empty);
            Assert.That(forwarded, Is.Empty);
        });
    }

    [Test]
    public void PendingMessagesAreBoundedExpireAndCannotBeCompletedByAStaleMessage()
    {
        TestClock clock = new();
        PubsubSettings settings = Settings();
        settings.MaxPendingValidationMessages = 1;
        settings.MaxPendingValidationBytes = 128;
        settings.PendingValidationTimeout = TimeSpan.FromSeconds(5);
        using PubsubRouter router = CreateRouter(settings, clock);
        int validations = 0;
        List<Message> admitted = [];
        List<TaskCompletionSource> validationWork = [];
        router.VerifyMessage = (_, _) =>
        {
            validations++;
            return MessageValidity.Deferred;
        };
        router.OnDeferredMessage = (peer, message) =>
        {
            Assert.That(Monitor.IsEntered(router), Is.False);
            Assert.That(peer, Is.EqualTo(TestPeers.PeerId(1)));
            admitted.Add(message);
            TaskCompletionSource work = new();
            validationWork.Add(work);
            return work.Task;
        };
        router.GetTopic(Topic);

        Message first = NewMessage(1);
        router.OnRpc(TestPeers.PeerId(1), RpcWith(first));
        router.OnRpc(TestPeers.PeerId(1), RpcWith(first));
        router.OnRpc(TestPeers.PeerId(1), RpcWith(NewMessage(2)));
        Assert.Multiple(() =>
        {
            Assert.That(validations, Is.EqualTo(2));
            Assert.That(admitted, Has.Count.EqualTo(1), "A full pending store must not start async validation.");
            Assert.That(router.PendingValidationCount, Is.EqualTo(1));
            Assert.That(router.CompleteValidation(NewMessage(1), MessageValidity.Accepted), Is.False);
        });

        clock.UtcNow += TimeSpan.FromSeconds(5);
        Assert.That(router.CompleteValidation(first, MessageValidity.Accepted), Is.False);
        Assert.That(router.PendingValidationCount, Is.Zero);

        Message replacement = NewMessage(1);
        router.OnRpc(TestPeers.PeerId(1), RpcWith(replacement));
        Assert.Multiple(() =>
        {
            Assert.That(validations, Is.EqualTo(3));
            Assert.That(admitted, Has.Count.EqualTo(2));
            Assert.That(router.PendingValidationCount, Is.EqualTo(1));
            Assert.That(router.CompleteValidation(first, MessageValidity.Accepted), Is.False);
        });
        validationWork[0].SetResult();
        Assert.That(router.PendingValidationCount, Is.EqualTo(1), "The old task must not remove the replacement entry.");

        router.Dispose();
        Assert.That(router.PendingValidationCount, Is.Zero);
        Assert.That(router.CompleteValidation(replacement, MessageValidity.Accepted), Is.False);
        validationWork[1].SetResult();
    }

    [Test]
    public void PendingByteLimitAndMutationCannotForwardUnvalidatedBytes()
    {
        PubsubSettings settings = Settings();
        settings.MaxPendingValidationBytes = NewMessage(1).CalculateSize();
        using PubsubRouter router = CreateRouter(settings);
        List<byte[]> delivered = [];
        router.GetTopic(Topic).OnMessage += (_, data) => delivered.Add(data);
        int validations = 0;
        TaskCompletionSource validationWork = new();
        router.VerifyMessage = (_, _) =>
        {
            validations++;
            return MessageValidity.Deferred;
        };
        router.OnDeferredMessage = (_, _) => validationWork.Task;

        Message first = NewMessage(1);
        router.OnRpc(TestPeers.PeerId(1), RpcWith(first));
        router.OnRpc(TestPeers.PeerId(1), RpcWith(NewMessage(2)));
        Assert.That(router.PendingValidationCount, Is.EqualTo(1));

        first.Data = ByteString.CopyFrom([3]);
        Assert.Multiple(() =>
        {
            Assert.That(router.CompleteValidation(first, MessageValidity.Accepted), Is.False);
            Assert.That(delivered, Is.Empty);
            Assert.That(router.PendingValidationCount, Is.Zero);
        });
        validationWork.SetResult();
        router.OnRpc(TestPeers.PeerId(1), RpcWith(NewMessage(2)));
        Assert.That(validations, Is.EqualTo(3));
    }

    [Test]
    public void ThrowingLocalSubscriberDoesNotPreventAcceptedMessageFromForwarding()
    {
        using PubsubRouter router = CreateRouter();
        ITopic topic = router.GetTopic(Topic);
        topic.OnMessage += (_, _) => throw new InvalidOperationException("subscriber failed");
        int otherSubscriberDeliveries = 0;
        topic.OnMessage += (_, _) => otherSubscriberDeliveries++;
        List<Rpc> forwarded = ConnectForwardingPeer(router);
        router.VerifyMessage = (_, _) => MessageValidity.Deferred;
        TaskCompletionSource validationWork = new();
        router.OnDeferredMessage = (_, _) => validationWork.Task;

        Message message = NewMessage(1);
        router.OnRpc(TestPeers.PeerId(1), RpcWith(message));

        Assert.Multiple(() =>
        {
            Assert.That(() => router.CompleteValidation(message, MessageValidity.Accepted), Throws.Nothing);
            Assert.That(otherSubscriberDeliveries, Is.EqualTo(1));
            Assert.That(forwarded.SelectMany(rpc => rpc.Publish).Count(), Is.EqualTo(1));
        });
        validationWork.SetResult();
    }

    [Test]
    public void ImmediateAcceptanceDeliversLocallyBeforeForwarding()
    {
        using PubsubRouter router = CreateRouter();
        List<string> order = [];
        router.GetTopic(Topic).OnMessage += (_, _) => order.Add("local");
        ConnectForwardingPeer(router, () => order.Add("forward"));
        router.VerifyMessage = (_, _) => MessageValidity.Accepted;

        router.OnRpc(TestPeers.PeerId(1), RpcWith(NewMessage(1)));

        Assert.That(order, Is.EqualTo(new[] { "local", "forward" }));
    }

    [TestCase(MessageValidity.Deferred)]
    [TestCase(MessageValidity.Accepted)]
    public void ValidatorMutationCannotAdmitOrForwardChangedMessage(MessageValidity verdict)
    {
        using PubsubRouter router = CreateRouter();
        List<byte[]> delivered = [];
        router.GetTopic(Topic).OnMessage += (_, bytes) => delivered.Add(bytes);
        List<Rpc> forwarded = ConnectForwardingPeer(router);
        int admitted = 0;
        router.OnDeferredMessage = (_, _) =>
        {
            admitted++;
            return Task.CompletedTask;
        };
        router.VerifyMessage = (_, message) =>
        {
            message.Data = ByteString.CopyFrom([2]);
            return verdict;
        };

        router.OnRpc(TestPeers.PeerId(1), RpcWith(NewMessage(1)));

        Assert.Multiple(() =>
        {
            Assert.That(admitted, Is.Zero);
            Assert.That(router.PendingValidationCount, Is.Zero);
            Assert.That(delivered, Is.Empty);
            Assert.That(forwarded, Is.Empty);
        });
    }

    [Test]
    public void UndefinedValidatorVerdictCannotForwardAMessage()
    {
        using PubsubRouter router = CreateRouter();
        List<byte[]> delivered = [];
        router.GetTopic(Topic).OnMessage += (_, bytes) => delivered.Add(bytes);
        List<Rpc> forwarded = ConnectForwardingPeer(router);
        router.VerifyMessage = (_, _) => (MessageValidity)int.MaxValue;

        router.OnRpc(TestPeers.PeerId(1), RpcWith(NewMessage(1)));

        Assert.Multiple(() =>
        {
            Assert.That(delivered, Is.Empty);
            Assert.That(forwarded, Is.Empty);
        });
    }

    [Test]
    public void DeferredCallbackRunsForAnAdmittedMessageWhenLaterValidationThrows()
    {
        using PubsubRouter router = CreateRouter();
        router.GetTopic(Topic);
        Message? admitted = null;
        int validations = 0;
        TaskCompletionSource validationWork = new();
        router.VerifyMessage = (_, _) => ++validations == 1
            ? MessageValidity.Deferred
            : throw new InvalidOperationException("validator failed");
        router.OnDeferredMessage = (_, message) =>
        {
            admitted = message;
            return validationWork.Task;
        };

        Rpc rpc = RpcWith(NewMessage(1));
        rpc.Publish.Add(NewMessage(2));
        router.OnRpc(TestPeers.PeerId(1), rpc);

        Assert.That(admitted, Is.SameAs(rpc.Publish[0]));
        Assert.That(router.PendingValidationCount, Is.EqualTo(1));
        Assert.That(router.CompleteValidation(admitted!, MessageValidity.Accepted), Is.True);
        validationWork.SetResult();
    }

    [Test]
    public async Task DeferredAsyncFaultAfterDisposalIsObserved()
    {
        using WarningLoggerFactory logs = new();
        using PubsubRouter router = new(new PeerStore(), Settings(), logs);
        router.GetTopic(Topic);
        TaskCompletionSource validationGate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        router.VerifyMessage = (_, _) => MessageValidity.Deferred;
        router.OnDeferredMessage = async (_, _) =>
        {
            await validationGate.Task;
            throw new InvalidOperationException("async validator failed");
        };

        router.OnRpc(TestPeers.PeerId(1), RpcWith(NewMessage(1)));
        Assert.That(router.PendingValidationCount, Is.EqualTo(1));
        router.Dispose();
        validationGate.SetResult();

        Exception warning = await logs.Warning.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.That(warning, Is.TypeOf<InvalidOperationException>()
            .With.Property(nameof(Exception.Message)).EqualTo("async validator failed"));
    }

    [Test]
    public void PendingMessageIdIsNotRequestedFromIHave()
    {
        using PubsubRouter router = CreateRouter();
        router.GetTopic(Topic);
        TaskCompletionSource closed = new();
        List<Rpc> responses = [];
        router.OutboundConnection(TestPeers.Multiaddr(1), PubsubRouter.GossipsubProtocolVersionV11, closed.Task, responses.Add);
        responses.Clear();
        router.VerifyMessage = (_, _) => MessageValidity.Deferred;
        TaskCompletionSource validationWork = new();
        router.OnDeferredMessage = (_, _) => validationWork.Task;

        router.OnRpc(TestPeers.PeerId(1), RpcWith(NewMessage(1)));
        Rpc ihave = new() { Control = new ControlMessage() };
        ihave.Control.Ihave.Add(new ControlIHave
        {
            TopicID = Topic,
            MessageIDs = { ByteString.CopyFrom([1]), ByteString.CopyFrom([2]) }
        });
        router.OnRpc(TestPeers.PeerId(1), ihave);

        Assert.That(responses.SelectMany(response => response.Control?.Iwant ?? [])
            .SelectMany(iwant => iwant.MessageIDs), Is.EqualTo(new[] { ByteString.CopyFrom([2]) }));
        validationWork.SetResult();
    }

    [Test]
    public void DeferredVerdictWithoutSubscriberDoesNotOccupyPendingStore()
    {
        using PubsubRouter router = CreateRouter();
        int validations = 0;
        router.VerifyMessage = (_, _) =>
        {
            validations++;
            return MessageValidity.Deferred;
        };

        router.OnRpc(TestPeers.PeerId(1), RpcWith(NewMessage(1)));
        router.OnRpc(TestPeers.PeerId(1), RpcWith(NewMessage(1)));

        Assert.Multiple(() =>
        {
            Assert.That(validations, Is.EqualTo(2));
            Assert.That(router.PendingValidationCount, Is.Zero);
        });
    }

    [Test]
    public void SynchronousCallbackFailureReleasesTheExactPendingEntry()
    {
        using PubsubRouter router = CreateRouter();
        int validations = 0;
        int callbacks = 0;
        router.VerifyMessage = (_, _) =>
        {
            validations++;
            return MessageValidity.Deferred;
        };
        router.OnDeferredMessage = (_, _) =>
        {
            callbacks++;
            throw new InvalidOperationException("callback failed");
        };

        router.OnRpc(TestPeers.PeerId(1), RpcWith(NewMessage(1)));
        Assert.That(router.PendingValidationCount, Is.Zero);
        router.OnRpc(TestPeers.PeerId(1), RpcWith(NewMessage(1)));

        Assert.Multiple(() =>
        {
            Assert.That(validations, Is.EqualTo(2));
            Assert.That(callbacks, Is.EqualTo(2));
            Assert.That(router.PendingValidationCount, Is.Zero);
        });
    }

    [Test]
    public void FaultedCallbackTaskReleasesPendingEntryForRetry()
    {
        using PubsubRouter router = CreateRouter();
        int validations = 0;
        router.VerifyMessage = (_, _) =>
        {
            validations++;
            return MessageValidity.Deferred;
        };
        router.OnDeferredMessage = (_, _) => Task.FromException(new InvalidOperationException("validation failed"));

        router.OnRpc(TestPeers.PeerId(1), RpcWith(NewMessage(1)));
        Assert.That(router.PendingValidationCount, Is.Zero);
        router.OnRpc(TestPeers.PeerId(1), RpcWith(NewMessage(1)));

        Assert.Multiple(() =>
        {
            Assert.That(validations, Is.EqualTo(2));
            Assert.That(router.PendingValidationCount, Is.Zero);
        });
    }

    [Test]
    public void DeferredVerdictHasOneCallbackOwner()
    {
        using PubsubRouter router = CreateRouter();
        router.OnDeferredMessage = (_, _) => Task.CompletedTask;

        Assert.Throws<ArgumentException>(() => router.OnDeferredMessage += (_, _) => Task.CompletedTask);
    }

    [Test]
    public void PendingTimeoutStartsWhenEachCallbackIsDispatched()
    {
        TestClock clock = new();
        PubsubSettings settings = Settings();
        settings.PendingValidationTimeout = TimeSpan.FromSeconds(5);
        using PubsubRouter router = CreateRouter(settings, clock);
        router.VerifyMessage = (_, _) => MessageValidity.Deferred;
        Message? second = null;
        TaskCompletionSource validationWork = new();
        router.OnDeferredMessage = (_, message) =>
        {
            if (message.Data.Span[0] == 1)
            {
                clock.UtcNow += TimeSpan.FromSeconds(6);
                Assert.That(router.PendingValidationCount, Is.EqualTo(1));
                return Task.CompletedTask;
            }

            second = message;
            return validationWork.Task;
        };

        Rpc rpc = RpcWith(NewMessage(1));
        rpc.Publish.Add(NewMessage(2));
        router.OnRpc(TestPeers.PeerId(1), rpc);

        Assert.That(second, Is.SameAs(rpc.Publish[1]));
        Assert.That(router.CompleteValidation(second!, MessageValidity.Accepted), Is.True);
        validationWork.SetResult();
    }

    private static PubsubSettings Settings() => new()
    {
        DefaultSignaturePolicy = PubsubSettings.SignaturePolicy.StrictNoSign,
        GetMessageId = message => new(message.Data.ToByteArray()),
    };

    private static PubsubRouter CreateRouter(PubsubSettings? settings = null, TimeProvider? clock = null)
        => new(new PeerStore(), settings ?? Settings(), timeProvider: clock);

    private static List<Rpc> ConnectForwardingPeer(PubsubRouter router, Action? onForward = null)
    {
        TaskCompletionSource closed = new();
        router.OutboundConnection(TestPeers.Multiaddr(1), PubsubRouter.FloodsubProtocolVersion, closed.Task, _ => { });
        List<Rpc> forwarded = [];
        Multiaddress target = TestPeers.Multiaddr(2);
        router.OutboundConnection(target, PubsubRouter.FloodsubProtocolVersion, closed.Task, rpc =>
        {
            if (rpc.Publish.Count > 0)
            {
                forwarded.Add(rpc);
                onForward?.Invoke();
            }
        });
        router.OnRpc(target.GetPeerId()!, new Rpc().WithTopics([Topic], []));
        return forwarded;
    }

    private static Message NewMessage(byte data) => new() { Topic = Topic, Data = ByteString.CopyFrom([data]) };

    private static Rpc RpcWith(Message message)
    {
        Rpc rpc = new();
        rpc.Publish.Add(message);
        return rpc;
    }

    private sealed class TestClock : TimeProvider
    {
        public DateTimeOffset UtcNow { get; set; } = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => UtcNow;
    }

    private sealed class WarningLoggerFactory : ILoggerFactory
    {
        public TaskCompletionSource<Exception> Warning { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public ILogger CreateLogger(string categoryName) => new WarningLogger(Warning);
        public void AddProvider(ILoggerProvider provider) { }
        public void Dispose() { }

        private sealed class WarningLogger(TaskCompletionSource<Exception> warning) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel logLevel) => logLevel == LogLevel.Warning;
            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
                Func<TState, Exception?, string> formatter)
            {
                if (logLevel == LogLevel.Warning && exception is not null)
                {
                    warning.TrySetResult(exception);
                }
            }
        }
    }
}
