using System;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;

namespace Campfire.Valheim.Core;

public sealed class PublishReply
{
    public int StatusCode { get; }
    public TimeSpan RetryAfter { get; }

    public PublishReply(int statusCode, TimeSpan retryAfter = default)
    {
        StatusCode = statusCode;
        RetryAfter = retryAfter;
    }

    public bool Retryable => StatusCode == 408 || StatusCode == 429 || StatusCode >= 500;
}

public interface IPublicationTransport
{
    PublishReply Register(byte[] registration, CancellationToken cancellation);
    PublishReply Publish(byte[] publication, CancellationToken cancellation);
}

// A short, length-delimited HTTP request over the mounted socket. No TCP fallback,
// credentials, native library, HttpClient ConnectCallback, or newer CLR required.
public sealed class SparkTransport : IPublicationTransport
{
    private readonly EndPoint endpoint;
    private readonly TimeSpan timeout;

    public SparkTransport(string socketPath, TimeSpan? timeout = null)
    {
        endpoint = new LocalSocketEndpoint(socketPath);
        this.timeout = timeout ?? TimeSpan.FromSeconds(10);
    }

    public PublishReply Register(byte[] registration, CancellationToken cancellation) =>
        Send("PUT", "/v1/registration", registration, cancellation);

    public PublishReply Publish(byte[] publication, CancellationToken cancellation) =>
        Send("POST", "/v1/publish", publication, cancellation);

    private PublishReply Send(string method, string path, byte[] publication, CancellationToken cancellation)
    {
        if (publication.Length == 0 || publication.Length > 16 * 1024)
        {
            throw new ArgumentOutOfRangeException(nameof(publication));
        }

        cancellation.ThrowIfCancellationRequested();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        deadline.CancelAfter(timeout);
        using var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        using var registration = deadline.Token.Register(() => socket.Dispose());
        socket.Connect(endpoint);
        using var stream = new NetworkStream(socket, ownsSocket: false);
        var headers = Encoding.ASCII.GetBytes(method + " " + path + " HTTP/1.1\r\nHost: spark\r\nContent-Type: application/json\r\nContent-Length: "
            + publication.Length.ToString(CultureInfo.InvariantCulture) + "\r\nConnection: close\r\n\r\n");
        stream.Write(headers, 0, headers.Length);
        stream.Write(publication, 0, publication.Length);
        return ReadReply(stream);
    }

    internal static PublishReply ReadReply(Stream stream)
    {
        var bytes = new byte[8192];
        var length = 0;
        while (length < bytes.Length)
        {
            var next = stream.ReadByte();
            if (next < 0)
            {
                throw new IOException("Spark closed the connection before acknowledging the publication.");
            }

            bytes[length++] = (byte)next;
            if (length >= 4 && bytes[length - 4] == 13 && bytes[length - 3] == 10
                && bytes[length - 2] == 13 && bytes[length - 1] == 10)
            {
                var lines = Encoding.ASCII.GetString(bytes, 0, length).Split(new[] { "\r\n" }, StringSplitOptions.None);
                var status = lines[0].Split(' ');
                if (status.Length < 2 || (status[0] != "HTTP/1.1" && status[0] != "HTTP/1.0")
                    || !int.TryParse(status[1], NumberStyles.None, CultureInfo.InvariantCulture, out var code)
                    || code < 200 || code > 599)
                {
                    throw new IOException("Invalid Spark HTTP acknowledgement.");
                }

                var retry = TimeSpan.Zero;
                foreach (var line in lines)
                {
                    var colon = line.IndexOf(':');
                    if (colon > 0 && line.Substring(0, colon).Equals("Retry-After", StringComparison.OrdinalIgnoreCase)
                        && int.TryParse(line.Substring(colon + 1).Trim(), out var seconds))
                    {
                        retry = TimeSpan.FromSeconds(Math.Max(0, Math.Min(300, seconds)));
                    }
                }

                return new PublishReply(code, retry);
            }
        }

        throw new IOException("Spark HTTP acknowledgement headers exceeded 8 KiB.");
    }
}

// Unity's Mono does not consistently expose UnixDomainSocketEndPoint, although
// Socket supports AF_UNIX. Encode the filesystem endpoint using the CLR API.
internal sealed class LocalSocketEndpoint : EndPoint
{
    private readonly string path;
    public override AddressFamily AddressFamily => AddressFamily.Unix;

    public LocalSocketEndpoint(string path)
    {
        if (string.IsNullOrEmpty(path) || path[0] != '/' || path.IndexOf('\0') >= 0 || Encoding.UTF8.GetByteCount(path) > 107)
        {
            throw new ArgumentException("Spark socket must be an absolute Linux path of at most 107 UTF-8 bytes.", nameof(path));
        }

        this.path = path;
    }

    public override SocketAddress Serialize()
    {
        var bytes = Encoding.UTF8.GetBytes(path);
        var address = new SocketAddress(AddressFamily.Unix, bytes.Length + 3);
        for (var index = 0; index < bytes.Length; index++)
        {
            address[index + 2] = bytes[index];
        }

        return address;
    }

    public override EndPoint Create(SocketAddress socketAddress) => new LocalSocketEndpoint(path);
    public override string ToString() => path;
}
