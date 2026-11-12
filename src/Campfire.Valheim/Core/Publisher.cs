using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Campfire.Valheim.Core;

// Spark owns the durable queue. This producer retains a bounded in-memory queue
// until Spark returns 202, and always sends the original encoded event on retry.
public sealed class Publisher : IDisposable
{
    private sealed class Pending
    {
        public byte[] Bytes { get; }
        public DateTimeOffset QueuedAt { get; }
        public Pending(byte[] bytes, DateTimeOffset queuedAt) { Bytes = bytes; QueuedAt = queuedAt; }
    }

    private readonly IPublicationTransport transport;
    private readonly Action<string> warn;
    private readonly int maximum;
    private readonly byte[] registration = ValheimRegistration.CreatePublication().Encode();
    private readonly object gate = new();
    private readonly Queue<Pending> events = new();
    private readonly CancellationTokenSource cancellation = new();
    private ServerSnapshot? latest;
    private Task? worker;
    private DateTimeOffset nextState;
    private DateTimeOffset nextAttempt;
    private int failures;
    private bool registered;
    private bool registrationRejected;
    private int disposed;

    public Publisher(IPublicationTransport transport, Action<string> warn, int maximum = 256)
    {
        if (maximum < 1 || maximum > 4096) { throw new ArgumentOutOfRangeException(nameof(maximum)); }
        this.transport = transport;
        this.warn = warn;
        this.maximum = maximum;
    }

    public int PendingEvents { get { lock (gate) { return events.Count; } } }

    public void SetState(ServerSnapshot? snapshot) => Volatile.Write(ref latest, snapshot);

    public bool Enqueue(GameEvent gameEvent, DateTimeOffset now)
    {
        var bytes = new Publication { Event = gameEvent }.Encode();
        lock (gate)
        {
            if (Volatile.Read(ref disposed) != 0 || events.Count >= maximum)
            {
                warn("Campfire event queue is full or stopped; a new event was not queued.");
                return false;
            }

            events.Enqueue(new Pending(bytes, now));
            return true;
        }
    }

    public void Start()
    {
        if (Volatile.Read(ref disposed) != 0) { throw new ObjectDisposedException(nameof(Publisher)); }
        if (worker != null)
        {
            throw new InvalidOperationException("Publisher is already running.");
        }

        worker = Task.Run(() =>
        {
            while (!cancellation.IsCancellationRequested)
            {
                ProcessOnce(DateTimeOffset.UtcNow, cancellation.Token);
                cancellation.Token.WaitHandle.WaitOne(TimeSpan.FromSeconds(1));
            }
        });
    }

    // One worker owns delivery. Explicit time keeps native functional tests deterministic.
    internal void ProcessOnce(DateTimeOffset now, CancellationToken token)
    {
        if (now < nextAttempt || token.IsCancellationRequested)
        {
            return;
        }

        ExpireEvents(now);
        if (registrationRejected) { return; }

        try
        {
            if (!registered)
            {
                var reply = transport.Register(registration, token);
                if (reply.StatusCode != 204)
                {
                    if (reply.Retryable || reply.StatusCode >= 200 && reply.StatusCode < 300)
                    {
                        Backoff(now, reply.RetryAfter, "Registration not committed: HTTP " + reply.StatusCode);
                    }
                    else
                    {
                        registrationRejected = true;
                        warn("Campfire rejected registration: HTTP " + reply.StatusCode
                            + ". Publishing paused; check the server's game integration configuration and registration revision, then restart the plugin.");
                    }

                    return;
                }

                registered = true;
            }

            var state = Volatile.Read(ref latest);
            if (state != null && now >= nextState && now - state.CapturedAt < TimeSpan.FromSeconds(30))
            {
                var reply = transport.Publish(state.StatePublication().Encode(), token);
                if (reply.StatusCode != 204 && reply.Retryable)
                {
                    Backoff(now, reply.RetryAfter, "State publication returned HTTP " + reply.StatusCode);
                    return;
                }

                if (reply.StatusCode != 204)
                {
                    warn("Campfire rejected state publication: HTTP " + reply.StatusCode);
                }

                nextState = now.AddSeconds(15);
            }

            for (var sent = 0; sent < 8; sent++)
            {
                Pending pending;
                lock (gate)
                {
                    if (events.Count == 0) { break; }
                    pending = events.Peek();
                }

                if (now - pending.QueuedAt >= TimeSpan.FromHours(24))
                {
                    RemovePending();
                    warn("An unaccepted Campfire event expired after 24 hours.");
                    continue;
                }

                var reply = transport.Publish(pending.Bytes, token);
                if (reply.StatusCode == 202)
                {
                    RemovePending();
                }
                else if (reply.Retryable || reply.StatusCode >= 200 && reply.StatusCode < 300)
                {
                    Backoff(now, reply.RetryAfter, "Event publication not durably accepted: HTTP " + reply.StatusCode);
                    return;
                }
                else
                {
                    RemovePending();
                    warn("Campfire permanently rejected an event: HTTP " + reply.StatusCode);
                }
            }

            failures = 0;
        }
        catch (Exception error) when (error is IOException || error is System.Net.Sockets.SocketException || error is ObjectDisposedException || error is OperationCanceledException || error is PlatformNotSupportedException)
        {
            if (!token.IsCancellationRequested)
            {
                registered = false;
                Backoff(now, TimeSpan.Zero, "Spark connection unavailable: " + error.Message);
            }
        }
    }

    private void ExpireEvents(DateTimeOffset now)
    {
        var expired = 0;
        lock (gate)
        {
            while (events.Count > 0 && now - events.Peek().QueuedAt >= TimeSpan.FromHours(24))
            {
                events.Dequeue();
                expired++;
            }
        }

        if (expired > 0) { warn(expired + " unaccepted Campfire events expired after 24 hours."); }
    }

    private void RemovePending() { lock (gate) { events.Dequeue(); } }

    private void Backoff(DateTimeOffset now, TimeSpan retryAfter, string message)
    {
        failures = Math.Min(failures + 1, 5);
        var seconds = Math.Max(Math.Min(30, 1 << failures), retryAfter.TotalSeconds);
        nextAttempt = now.AddSeconds(seconds);
        warn(message + "; retrying in " + seconds + " seconds.");
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) { return; }
        SetState(null);
        cancellation.Cancel();
        if (worker != null && !worker.Wait(TimeSpan.FromSeconds(2)))
        {
            warn("Campfire publisher is still stopping; unaccepted events remain in memory only.");
        }

        if (PendingEvents > 0)
        {
            warn("Campfire publisher stopped with " + PendingEvents + " events not yet accepted by Spark.");
        }

        if (worker == null || worker.IsCompleted) { cancellation.Dispose(); }
    }
}
