using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using Campfire.Valheim.Core;
using Xunit;

namespace Campfire.Valheim.Tests;

public sealed class PublisherTests
{
    private sealed class Transport : IPublicationTransport
    {
        public List<byte[]> Requests { get; } = new();
        public Queue<PublishReply> Replies { get; } = new();
        public Exception? Failure { get; set; }
        public List<byte[]> Registrations { get; } = new();
        public Queue<PublishReply> RegistrationReplies { get; } = new();
        public Exception? RegistrationFailure { get; set; }
        public List<string> Order { get; } = new();
        public PublishReply Register(byte[] registration, CancellationToken cancellation)
        {
            Order.Add("register");
            Registrations.Add(registration.ToArray());
            if (RegistrationFailure != null) { throw RegistrationFailure; }
            return RegistrationReplies.Count > 0 ? RegistrationReplies.Dequeue() : new PublishReply(204);
        }

        public PublishReply Publish(byte[] publication, CancellationToken cancellation)
        {
            Order.Add("publish");
            Requests.Add(publication.ToArray());
            if (Failure != null) { throw Failure; }
            return Replies.Count > 0 ? Replies.Dequeue() : new PublishReply(Encoding.UTF8.GetString(publication).Contains("\"state\"") ? 204 : 202);
        }
    }

    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-10-24T00:00:00Z");

    [Fact]
    public void RegistersBeforeStateAndEventsAndDoesNotRepeatLabelsOnHeartbeats()
    {
        var transport = new Transport();
        using var publisher = new Publisher(transport, _ => { });
        publisher.SetState(new ServerSnapshot("one", "Meadows", 10, Array.Empty<PlayerSnapshot>(), Now));
        publisher.Enqueue(new GameEvent { Title = "Hello", Data = new() { ["message"] = "Hello" } }, Now);
        publisher.ProcessOnce(Now, CancellationToken.None);
        Assert.Equal(new[] { "register", "publish", "publish" }, transport.Order);
        Assert.Single(transport.Registrations);
        Assert.Equal(0, publisher.PendingEvents);
        Assert.All(transport.Requests, bytes => Assert.DoesNotContain("field_labels", Encoding.UTF8.GetString(bytes)));
        publisher.SetState(new ServerSnapshot("one", "Black Forest", 10, Array.Empty<PlayerSnapshot>(), Now.AddSeconds(15)));
        publisher.ProcessOnce(Now.AddSeconds(15), CancellationToken.None);
        Assert.Single(transport.Registrations);
        Assert.Equal(3, transport.Requests.Count);
    }

    [Fact]
    public void RegistrationRetriesBeforePublishingAndKeepsOnlyTheNewestState()
    {
        var transport = new Transport();
        transport.RegistrationReplies.Enqueue(new PublishReply(429, TimeSpan.FromSeconds(5)));
        using var publisher = new Publisher(transport, _ => { });
        publisher.SetState(new ServerSnapshot("one", "Old world", 10, Array.Empty<PlayerSnapshot>(), Now));
        publisher.Enqueue(new GameEvent { Title = "Original event" }, Now);
        publisher.ProcessOnce(Now, CancellationToken.None);
        Assert.Empty(transport.Requests);
        Assert.Equal(1, publisher.PendingEvents);
        publisher.SetState(new ServerSnapshot("one", "New world", 10, Array.Empty<PlayerSnapshot>(), Now.AddSeconds(1)));
        publisher.ProcessOnce(Now.AddSeconds(4), CancellationToken.None);
        Assert.Single(transport.Registrations);
        publisher.ProcessOnce(Now.AddSeconds(5), CancellationToken.None);
        Assert.Equal(2, transport.Registrations.Count);
        Assert.Equal(transport.Registrations[0], transport.Registrations[1]);
        Assert.Equal(new[] { "register", "register", "publish", "publish" }, transport.Order);
        Assert.Contains("New world", Encoding.UTF8.GetString(transport.Requests[0]));
        Assert.DoesNotContain("Old world", Encoding.UTF8.GetString(transport.Requests[0]));
        Assert.Equal(0, publisher.PendingEvents);
    }

    [Fact]
    public void LostRegistrationAcknowledgementRetriesTheSameManifestWithoutLosingEvents()
    {
        var transport = new Transport { RegistrationFailure = new IOException("Acknowledgement lost") };
        using var publisher = new Publisher(transport, _ => { });
        publisher.Enqueue(new GameEvent { Title = "Retained event" }, Now);
        publisher.ProcessOnce(Now, CancellationToken.None);
        Assert.Empty(transport.Requests);
        Assert.Equal(1, publisher.PendingEvents);
        transport.RegistrationFailure = null;
        publisher.ProcessOnce(Now.AddSeconds(2), CancellationToken.None);
        Assert.Equal(2, transport.Registrations.Count);
        Assert.Equal(transport.Registrations[0], transport.Registrations[1]);
        Assert.Single(transport.Requests);
        Assert.Equal(0, publisher.PendingEvents);
    }

    [Theory]
    [InlineData(400)]
    [InlineData(403)]
    [InlineData(404)]
    [InlineData(409)]
    public void PermanentRegistrationRejectionPausesPublishingButKeepsQueueBoundedAndExpiring(int code)
    {
        var transport = new Transport();
        transport.RegistrationReplies.Enqueue(new PublishReply(code));
        var warnings = new List<string>();
        using var publisher = new Publisher(transport, warnings.Add, maximum: 1);
        publisher.Enqueue(new GameEvent { Title = "Retained" }, Now);
        publisher.SetState(new ServerSnapshot("one", "Meadows", 10, Array.Empty<PlayerSnapshot>(), Now));
        publisher.ProcessOnce(Now, CancellationToken.None);
        publisher.ProcessOnce(Now.AddMinutes(1), CancellationToken.None);
        Assert.Single(transport.Registrations);
        Assert.Empty(transport.Requests);
        Assert.Equal(1, publisher.PendingEvents);
        Assert.False(publisher.Enqueue(new GameEvent { Title = "Overflow" }, Now));
        Assert.Contains(warnings, message => message.Contains("rejected registration"));
        publisher.ProcessOnce(Now.AddHours(24), CancellationToken.None);
        Assert.Equal(0, publisher.PendingEvents);
        Assert.Empty(transport.Requests);
    }

    [Theory]
    [InlineData(200)]
    [InlineData(202)]
    [InlineData(503)]
    public void RegistrationRequiresTheExactCommittedAcknowledgement(int code)
    {
        var transport = new Transport();
        transport.RegistrationReplies.Enqueue(new PublishReply(code));
        using var publisher = new Publisher(transport, _ => { });
        publisher.Enqueue(new GameEvent { Title = "Retained" }, Now);
        publisher.ProcessOnce(Now, CancellationToken.None);
        Assert.Empty(transport.Requests);
        Assert.Equal(1, publisher.PendingEvents);
        publisher.ProcessOnce(Now.AddSeconds(2), CancellationToken.None);
        Assert.Equal(2, transport.Registrations.Count);
        Assert.Single(transport.Requests);
        Assert.Equal(0, publisher.PendingEvents);
    }

    [Fact]
    public void RetryRetainsExactEventBytesAndRespectsRetryAfter()
    {
        var transport = new Transport();
        transport.Replies.Enqueue(new PublishReply(429, TimeSpan.FromSeconds(5)));
        using var publisher = new Publisher(transport, _ => { });
        var gameEvent = new GameEvent { Title = "Alex died", Type = "player.died" };
        Assert.True(publisher.Enqueue(gameEvent, Now));
        gameEvent.Title = "Must not change a retry";
        publisher.ProcessOnce(Now, CancellationToken.None);
        Assert.Equal(1, publisher.PendingEvents);
        publisher.ProcessOnce(Now.AddSeconds(4), CancellationToken.None);
        Assert.Single(transport.Requests);
        publisher.ProcessOnce(Now.AddSeconds(5), CancellationToken.None);
        Assert.Equal(0, publisher.PendingEvents);
        Assert.Equal(transport.Requests[0], transport.Requests[1]);
        Assert.DoesNotContain("Must not change", Encoding.UTF8.GetString(transport.Requests[1]));
    }

    [Fact]
    public void ConnectionFailureRetainsEventsAndRecoveryPublishesTheLatestState()
    {
        var transport = new Transport { Failure = new IOException("Spark unavailable") };
        var warnings = new List<string>();
        using var publisher = new Publisher(transport, warnings.Add);
        Assert.True(publisher.Enqueue(new GameEvent { Title = "Alex died", Type = "player.died" }, Now));
        publisher.ProcessOnce(Now, CancellationToken.None);
        var failedEvent = Assert.Single(transport.Requests);
        Assert.Equal(1, publisher.PendingEvents);
        Assert.Contains(warnings, message => message.Contains("Spark connection unavailable"));

        transport.Failure = null;
        publisher.SetState(new ServerSnapshot("one", "Old world", 10, Array.Empty<PlayerSnapshot>(), Now));
        publisher.SetState(new ServerSnapshot("two", "New world", 20, new[] { new PlayerSnapshot(1, "Alex") }, Now.AddSeconds(1)));
        publisher.ProcessOnce(Now.AddSeconds(1), CancellationToken.None);
        Assert.Single(transport.Requests);
        publisher.ProcessOnce(Now.AddSeconds(2), CancellationToken.None);

        Assert.Equal(3, transport.Requests.Count);
        var recoveredState = Encoding.UTF8.GetString(transport.Requests[1]);
        Assert.Contains("New world", recoveredState);
        Assert.DoesNotContain("Old world", recoveredState);
        Assert.Equal(failedEvent, transport.Requests[2]);
        Assert.Equal(2, transport.Registrations.Count);
        Assert.Equal(transport.Registrations[0], transport.Registrations[1]);
        Assert.Equal(0, publisher.PendingEvents);
        publisher.ProcessOnce(Now.AddSeconds(3), CancellationToken.None);
        Assert.Equal(3, transport.Requests.Count);
    }

    [Fact]
    public void QueueIsBoundedAndPermanentRejectionsDoNotBlockLaterEvents()
    {
        var transport = new Transport();
        transport.Replies.Enqueue(new PublishReply(409));
        var warnings = new List<string>();
        using var publisher = new Publisher(transport, warnings.Add, maximum: 1);
        Assert.True(publisher.Enqueue(new GameEvent { Title = "one" }, Now));
        Assert.False(publisher.Enqueue(new GameEvent { Title = "two" }, Now));
        publisher.ProcessOnce(Now, CancellationToken.None);
        Assert.Equal(0, publisher.PendingEvents);
        Assert.Contains(warnings, message => message.Contains("permanently"));
        Assert.True(publisher.Enqueue(new GameEvent { Title = "three" }, Now));
        publisher.ProcessOnce(Now, CancellationToken.None);
        Assert.Equal(0, publisher.PendingEvents);
    }

    [Fact]
    public void StateIsLatestOnlyAndStopsRefreshingWhenSamplingStops()
    {
        var transport = new Transport();
        using var publisher = new Publisher(transport, _ => { });
        publisher.SetState(new ServerSnapshot("one", "Meadows", 10, Array.Empty<PlayerSnapshot>(), Now));
        publisher.ProcessOnce(Now, CancellationToken.None);
        publisher.ProcessOnce(Now.AddSeconds(5), CancellationToken.None);
        Assert.Single(transport.Requests);
        publisher.SetState(new ServerSnapshot("one", null, null, new[] { new PlayerSnapshot(1, "Alex") }, Now.AddSeconds(15)));
        publisher.ProcessOnce(Now.AddSeconds(15), CancellationToken.None);
        var latest = Encoding.UTF8.GetString(transport.Requests[1]);
        Assert.DoesNotContain("Meadows", latest);
        Assert.DoesNotContain("capacity", latest);
        publisher.ProcessOnce(Now.AddSeconds(46), CancellationToken.None);
        Assert.Equal(2, transport.Requests.Count);
        publisher.SetState(null);
        publisher.ProcessOnce(Now.AddMinutes(1), CancellationToken.None);
        Assert.Equal(2, transport.Requests.Count);
    }

    [Fact]
    public void OldEventsExpireAndNonDurableAcknowledgementsAreNotAccepted()
    {
        var transport = new Transport();
        transport.Replies.Enqueue(new PublishReply(204));
        using var publisher = new Publisher(transport, _ => { });
        publisher.Enqueue(new GameEvent { Title = "one" }, Now);
        publisher.ProcessOnce(Now, CancellationToken.None);
        Assert.Equal(1, publisher.PendingEvents);
        publisher.ProcessOnce(Now.AddHours(24), CancellationToken.None);
        Assert.Equal(0, publisher.PendingEvents);
        Assert.Single(transport.Requests);
    }
}
