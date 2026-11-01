using System;
using System.Globalization;
using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Campfire.Valheim.Core;
using Xunit;

namespace Campfire.Valheim.Tests;

public sealed class SparkTransportTests
{
    [Fact]
    public async Task PublishesValidHTTPOverAnActualUnixSocket()
    {
        var path = Path.Combine(Path.GetTempPath(), "cfvh-" + Guid.NewGuid().ToString("N") + ".sock");
        using var listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        listener.Bind(new UnixDomainSocketEndPoint(path));
        listener.Listen(1);
        try
        {
            var server = Task.Run(() =>
            {
                using var connection = listener.Accept();
                using var stream = new NetworkStream(connection);
                using var headers = new MemoryStream();
                var tail = "";
                while (!tail.EndsWith("\r\n\r\n", StringComparison.Ordinal))
                {
                    var next = stream.ReadByte();
                    Assert.NotEqual(-1, next);
                    headers.WriteByte((byte)next);
                    tail = Encoding.ASCII.GetString(headers.ToArray());
                    Assert.True(headers.Length < 8192);
                }

                Assert.StartsWith("POST /v1/publish HTTP/1.1\r\n", tail);
                var contentLength = 0;
                foreach (var line in tail.Split("\r\n"))
                {
                    if (line.StartsWith("Content-Length: ", StringComparison.Ordinal))
                    {
                        contentLength = int.Parse(line.Substring(16), CultureInfo.InvariantCulture);
                    }
                }

                var body = new byte[contentLength];
                stream.ReadExactly(body);
                using var json = JsonDocument.Parse(body);
                Assert.Equal(1, json.RootElement.GetProperty("version").GetInt32());
                Assert.Equal("player.died", json.RootElement.GetProperty("event").GetProperty("type").GetString());
                Assert.False(json.RootElement.TryGetProperty("server_id", out _));
                var reply = Encoding.ASCII.GetBytes("HTTP/1.1 202 Accepted\r\nContent-Length: 0\r\nConnection: close\r\n\r\n");
                stream.Write(reply);
            });
            var publication = new Publication { Event = new GameEvent { Type = "player.died", Title = "Alex died" } }.Encode();
            var result = await Task.Run(() => new SparkTransport(path).Publish(publication, CancellationToken.None));
            Assert.Equal(202, result.StatusCode);
            await server.WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally { File.Delete(path); }
    }

    [Theory]
    [InlineData("HTTP/1.1 429 Too Many Requests\r\nRetry-After: 5\r\n\r\n", 429, 5)]
    [InlineData("HTTP/1.1 503 Unavailable\r\nretry-after: 9999\r\n\r\n", 503, 300)]
    [InlineData("HTTP/1.1 204 No Content\r\n\r\n", 204, 0)]
    public void ParsesBoundedAcknowledgements(string response, int code, int delay)
    {
        using var stream = new MemoryStream(Encoding.ASCII.GetBytes(response));
        var reply = SparkTransport.ReadReply(stream);
        Assert.Equal(code, reply.StatusCode);
        Assert.Equal(TimeSpan.FromSeconds(delay), reply.RetryAfter);
    }

    [Fact]
    public async Task CancellationClosesAHungSocket()
    {
        var path = Path.Combine(Path.GetTempPath(), "cfvh-" + Guid.NewGuid().ToString("N") + ".sock");
        using var listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        listener.Bind(new UnixDomainSocketEndPoint(path));
        listener.Listen(1);
        using var cancellation = new CancellationTokenSource();
        var accepted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            var server = Task.Run(() =>
            {
                using var connection = listener.Accept();
                using var stream = new NetworkStream(connection);
                accepted.SetResult();
                var buffer = new byte[1024];
                while (stream.Read(buffer) > 0) { }
            });
            var publication = new Publication { Event = new GameEvent { Title = "test" } }.Encode();
            var sending = Task.Run(() => new SparkTransport(path).Publish(publication, cancellation.Token));
            await accepted.Task.WaitAsync(TimeSpan.FromSeconds(3));
            cancellation.Cancel();
            var error = await Record.ExceptionAsync(async () => await sending.WaitAsync(TimeSpan.FromSeconds(3)));
            Assert.True(error is IOException or SocketException or ObjectDisposedException or OperationCanceledException);
            await server.WaitAsync(TimeSpan.FromSeconds(3));
        }
        finally { File.Delete(path); }
    }

    [Theory]
    [InlineData("relative.sock")]
    [InlineData("/bad\0socket")]
    public void RejectsUnsafeSocketPaths(string path) => Assert.Throws<ArgumentException>(() => new SparkTransport(path));

    [Fact]
    public void RejectsMalformedOrUnboundedAcknowledgements()
    {
        foreach (var response in new[] { "HTTP/1.1 wat Nope\r\n\r\n", new string('x', 8193), "HTTP/1.1 202" })
        {
            using var stream = new MemoryStream(Encoding.ASCII.GetBytes(response));
            Assert.Throws<IOException>(() => SparkTransport.ReadReply(stream));
        }
    }
}
