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
        public PublishReply Publish(byte[] publication, CancellationToken cancellation)
        {
            Requests.Add(publication.ToArray());
            if (Failure != null) { throw Failure; }
            return Replies.Count > 0 ? Replies.Dequeue() : new PublishReply(Encoding.UTF8.GetString(publication).Contains("\"state\"") ? 204 : 202);
        }
    }

    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-10-24T00:00:00Z");

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
