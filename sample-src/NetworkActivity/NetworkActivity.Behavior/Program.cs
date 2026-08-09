using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;

namespace NetworkActivity;

internal static class Program
{
    private static readonly TimeSpan IoTimeout = TimeSpan.FromSeconds(15);

    public static async Task<int> Main(string[] args)
    {
        try
        {
            var options = ArgumentReader.Parse(args);
            return options.Require("role") switch
            {
                "helper" => await RunHelper(options),
                "actor" => await RunActor(options),
                var role => throw new ArgumentException($"不支持的网络行为角色：{role}"),
            };
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(exception);
            return 20;
        }
    }

    private static async Task<int> RunHelper(ArgumentReader options)
    {
        var operation = options.Require("operation");
        var readyPath = Path.GetFullPath(options.Require("ready"));
        var resultPath = Path.GetFullPath(options.Require("result"));
        var nonce = options.Require("nonce");
        var payloadSize = options.GetInt("payload-size", 8_192, 256, 1_048_576);
        try
        {
            var result = operation switch
            {
                "tcp_connect" => await ServeTcp(operation, readyPath, nonce),
                "udp_connect" => await ServeUdp(operation, readyPath, nonce, 0),
                "dns_query" => await ServeDns(operation, readyPath, nonce),
                "url_access" or "file_download" => await ServeHttp(operation, readyPath, nonce, payloadSize),
                _ => throw new ArgumentException($"不支持的网络操作：{operation}"),
            };
            ProtocolJson.WriteAtomic(resultPath, result);
            return result.Succeeded ? 0 : 20;
        }
        catch (Exception exception)
        {
            ProtocolJson.WriteAtomic(resultPath, new HelperResult
            {
                Operation = operation,
                Succeeded = false,
                ObservedAtUtc = DateTimeOffset.UtcNow,
                Local = Endpoint(IPAddress.Loopback, 0),
                Error = exception.Message,
            });
            throw;
        }
    }

    private static async Task<int> RunActor(ArgumentReader options)
    {
        var operation = options.Require("operation");
        var resultPath = Path.GetFullPath(options.Require("result"));
        var address = IPAddress.Parse(options.Require("address"));
        var port = options.GetInt("port", 0, 1, 65_535);
        var nonce = options.Require("nonce");
        var holdMs = options.GetInt("hold-ms", 1_500, 0, 30_000);
        BehaviorResult result;
        try
        {
            result = operation switch
            {
                "tcp_connect" => await ConnectTcp(operation, address, port, nonce),
                "udp_connect" => await ConnectUdp(operation, address, port, nonce),
                "dns_query" => await QueryDns(operation, address, port, options.Require("question"), nonce),
                "url_access" => await RequestHttp(operation, address, port, options.Require("url"), nonce, null),
                "file_download" => await RequestHttp(operation, address, port, options.Require("url"), nonce,
                    Path.GetFullPath(options.Require("destination"))),
                _ => throw new ArgumentException($"不支持的网络操作：{operation}"),
            };
        }
        catch (Exception exception)
        {
            result = new BehaviorResult
            {
                Operation = operation,
                Succeeded = false,
                OccurredAtUtc = DateTimeOffset.UtcNow,
                CompletedAtUtc = DateTimeOffset.UtcNow,
                Local = Endpoint(IPAddress.Any, 0),
                Remote = Endpoint(address, port),
                BytesSent = 0,
                BytesReceived = 0,
                RequestNonce = nonce,
                Error = exception.Message,
            };
        }
        ProtocolJson.WriteAtomic(resultPath, result);
        if (holdMs > 0) await Task.Delay(holdMs);
        return result.Succeeded ? 0 : 20;
    }

    private static async Task<HelperResult> ServeTcp(string operation, string readyPath, string nonce)
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start(1);
        var local = (IPEndPoint)listener.LocalEndpoint;
        ProtocolJson.WriteAtomic(readyPath, new HelperReady { Operation = operation, Endpoint = Endpoint(local) });
        using var client = await listener.AcceptTcpClientAsync().WaitAsync(IoTimeout);
        await using var stream = client.GetStream();
        var buffer = new byte[512];
        var count = await stream.ReadAsync(buffer).AsTask().WaitAsync(IoTimeout);
        var received = Encoding.UTF8.GetString(buffer, 0, count);
        var reply = Encoding.UTF8.GetBytes("ACK|" + nonce);
        await stream.WriteAsync(reply);
        await stream.FlushAsync();
        return new HelperResult
        {
            Operation = operation,
            Succeeded = received == "EDRTEST|" + nonce + "|TCP",
            ObservedAtUtc = DateTimeOffset.UtcNow,
            Local = Endpoint(local),
            Remote = Endpoint((IPEndPoint)client.Client.RemoteEndPoint!),
            ReceivedNonce = received.Contains(nonce, StringComparison.Ordinal) ? nonce : null,
        };
    }

    private static async Task<HelperResult> ServeUdp(string operation, string readyPath, string nonce, int port)
    {
        using var udp = new UdpClient(new IPEndPoint(IPAddress.Loopback, port));
        var local = (IPEndPoint)udp.Client.LocalEndPoint!;
        ProtocolJson.WriteAtomic(readyPath, new HelperReady { Operation = operation, Endpoint = Endpoint(local) });
        using var timeout = new CancellationTokenSource(IoTimeout);
        var packet = await udp.ReceiveAsync(timeout.Token);
        var received = Encoding.UTF8.GetString(packet.Buffer);
        var reply = Encoding.UTF8.GetBytes("ACK|" + nonce);
        await udp.SendAsync(reply, packet.RemoteEndPoint, timeout.Token);
        return new HelperResult
        {
            Operation = operation,
            Succeeded = received == "EDRTEST|" + nonce + "|UDP",
            ObservedAtUtc = DateTimeOffset.UtcNow,
            Local = Endpoint(local),
            Remote = Endpoint(packet.RemoteEndPoint),
            ReceivedNonce = received.Contains(nonce, StringComparison.Ordinal) ? nonce : null,
        };
    }

    private static async Task<HelperResult> ServeDns(string operation, string readyPath, string nonce)
    {
        using var udp = new UdpClient(new IPEndPoint(IPAddress.Loopback, 53));
        var local = (IPEndPoint)udp.Client.LocalEndPoint!;
        var question = $"{SanitizeLabel(nonce)}.edrtest.invalid";
        ProtocolJson.WriteAtomic(readyPath, new HelperReady
        {
            Operation = operation,
            Endpoint = Endpoint(local),
            DnsQuestion = question,
        });
        using var timeout = new CancellationTokenSource(IoTimeout);
        var packet = await udp.ReceiveAsync(timeout.Token);
        var parsedQuestion = ReadDnsQuestion(packet.Buffer);
        var response = BuildDnsResponse(packet.Buffer, IPAddress.Parse("192.0.2.123"));
        await udp.SendAsync(response, packet.RemoteEndPoint, timeout.Token);
        return new HelperResult
        {
            Operation = operation,
            Succeeded = string.Equals(parsedQuestion, question, StringComparison.OrdinalIgnoreCase),
            ObservedAtUtc = DateTimeOffset.UtcNow,
            Local = Endpoint(local),
            Remote = Endpoint(packet.RemoteEndPoint),
            ReceivedNonce = parsedQuestion.StartsWith(SanitizeLabel(nonce), StringComparison.OrdinalIgnoreCase) ? nonce : null,
            DnsQuestion = parsedQuestion,
        };
    }

    private static async Task<HelperResult> ServeHttp(string operation, string readyPath, string nonce, int payloadSize)
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start(1);
        var local = (IPEndPoint)listener.LocalEndpoint;
        var path = $"/edrtest/{(operation == "file_download" ? "download" : "url")}/{SanitizeLabel(nonce)}?nonce={Uri.EscapeDataString(nonce)}";
        var url = $"http://127.0.0.1:{local.Port}{path}";
        ProtocolJson.WriteAtomic(readyPath, new HelperReady { Operation = operation, Endpoint = Endpoint(local), Url = url });
        using var client = await listener.AcceptTcpClientAsync().WaitAsync(IoTimeout);
        await using var stream = client.GetStream();
        var header = Encoding.ASCII.GetString(await ReadHeader(stream));
        var requestLine = header.Split("\r\n", StringSplitOptions.None)[0];
        var requestedPath = requestLine.Split(' ', StringSplitOptions.RemoveEmptyEntries).ElementAtOrDefault(1);
        var body = operation == "file_download"
            ? Payload(nonce, payloadSize)
            : Encoding.UTF8.GetBytes($"{{\"schema_version\":\"1.0\",\"nonce\":\"{nonce}\",\"status\":\"ok\"}}");
        var responseHeader = Encoding.ASCII.GetBytes(
            $"HTTP/1.1 200 OK\r\nContent-Type: {(operation == "file_download" ? "application/octet-stream" : "application/json")}\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n");
        await stream.WriteAsync(responseHeader);
        await stream.WriteAsync(body);
        await stream.FlushAsync();
        return new HelperResult
        {
            Operation = operation,
            Succeeded = requestLine.StartsWith("GET ", StringComparison.Ordinal) && requestedPath == path,
            ObservedAtUtc = DateTimeOffset.UtcNow,
            Local = Endpoint(local),
            Remote = Endpoint((IPEndPoint)client.Client.RemoteEndPoint!),
            ReceivedNonce = requestedPath?.Contains(Uri.EscapeDataString(nonce), StringComparison.Ordinal) == true ? nonce : null,
            RequestedPath = requestedPath,
        };
    }

    private static async Task<BehaviorResult> ConnectTcp(string operation, IPAddress address, int port, string nonce)
    {
        using var client = new TcpClient(address.AddressFamily);
        using var timeout = new CancellationTokenSource(IoTimeout);
        var occurred = DateTimeOffset.UtcNow;
        await client.ConnectAsync(address, port, timeout.Token);
        var local = (IPEndPoint)client.Client.LocalEndPoint!;
        var remote = (IPEndPoint)client.Client.RemoteEndPoint!;
        await using var stream = client.GetStream();
        var payload = Encoding.UTF8.GetBytes("EDRTEST|" + nonce + "|TCP");
        await stream.WriteAsync(payload, timeout.Token);
        var buffer = new byte[512];
        var count = await stream.ReadAsync(buffer, timeout.Token);
        var reply = Encoding.UTF8.GetString(buffer, 0, count);
        return Result(operation, occurred, local, remote, payload.Length, count, reply == "ACK|" + nonce, nonce);
    }

    private static async Task<BehaviorResult> ConnectUdp(string operation, IPAddress address, int port, string nonce)
    {
        using var udp = new UdpClient(address.AddressFamily);
        udp.Connect(address, port);
        var payload = Encoding.UTF8.GetBytes("EDRTEST|" + nonce + "|UDP");
        using var timeout = new CancellationTokenSource(IoTimeout);
        var occurred = DateTimeOffset.UtcNow;
        await udp.SendAsync(payload, timeout.Token);
        var packet = await udp.ReceiveAsync(timeout.Token);
        var local = (IPEndPoint)udp.Client.LocalEndPoint!;
        var remote = (IPEndPoint)udp.Client.RemoteEndPoint!;
        var reply = Encoding.UTF8.GetString(packet.Buffer);
        return Result(operation, occurred, local, remote, payload.Length, packet.Buffer.Length, reply == "ACK|" + nonce, nonce);
    }

    private static async Task<BehaviorResult> QueryDns(string operation, IPAddress address, int port, string question, string nonce)
    {
        using var udp = new UdpClient(address.AddressFamily);
        udp.Connect(address, port);
        var query = BuildDnsQuery(question, (ushort)Random.Shared.Next(1, ushort.MaxValue));
        using var timeout = new CancellationTokenSource(IoTimeout);
        var occurred = DateTimeOffset.UtcNow;
        await udp.SendAsync(query, timeout.Token);
        var packet = await udp.ReceiveAsync(timeout.Token);
        var local = (IPEndPoint)udp.Client.LocalEndPoint!;
        var remote = (IPEndPoint)udp.Client.RemoteEndPoint!;
        var answers = ReadDnsAnswers(packet.Buffer);
        return new BehaviorResult
        {
            Operation = operation,
            Succeeded = answers.Contains("192.0.2.123", StringComparer.Ordinal),
            OccurredAtUtc = occurred,
            CompletedAtUtc = DateTimeOffset.UtcNow,
            Local = Endpoint(local),
            Remote = Endpoint(remote),
            BytesSent = query.Length,
            BytesReceived = packet.Buffer.Length,
            RequestNonce = nonce,
            DnsQuestion = question,
            DnsQueryType = "A",
            DnsAnswers = answers,
        };
    }

    private static async Task<BehaviorResult> RequestHttp(
        string operation, IPAddress address, int port, string url, string nonce, string? destination)
    {
        var uri = new Uri(url);
        using var client = new TcpClient(address.AddressFamily);
        using var timeout = new CancellationTokenSource(IoTimeout);
        var occurred = DateTimeOffset.UtcNow;
        await client.ConnectAsync(address, port, timeout.Token);
        var local = (IPEndPoint)client.Client.LocalEndPoint!;
        var remote = (IPEndPoint)client.Client.RemoteEndPoint!;
        await using var stream = client.GetStream();
        var request = Encoding.ASCII.GetBytes($"GET {uri.PathAndQuery} HTTP/1.1\r\nHost: {uri.Host}:{uri.Port}\r\nConnection: close\r\n\r\n");
        await stream.WriteAsync(request, timeout.Token);
        var response = await ReadToEnd(stream, timeout.Token);
        var separator = FindSequence(response, [13, 10, 13, 10]);
        if (separator < 0) throw new InvalidDataException("HTTP 响应缺少头部终止符。");
        var responseHeader = Encoding.ASCII.GetString(response, 0, separator);
        var statusCode = int.Parse(responseHeader.Split(' ', StringSplitOptions.RemoveEmptyEntries)[1]);
        var body = response[(separator + 4)..];
        DateTimeOffset? fileOccurred = null;
        string? md5 = null;
        string? sha256 = null;
        if (destination is not null)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            fileOccurred = DateTimeOffset.UtcNow;
            await File.WriteAllBytesAsync(destination, body, timeout.Token);
            md5 = Convert.ToHexString(MD5.HashData(body)).ToLowerInvariant();
            sha256 = Convert.ToHexString(SHA256.HashData(body)).ToLowerInvariant();
        }
        var bodyText = destination is null ? Encoding.UTF8.GetString(body) : string.Empty;
        var succeeded = statusCode == 200 && (destination is not null
            ? File.Exists(destination) && new FileInfo(destination).Length == body.Length
            : bodyText.Contains(nonce, StringComparison.Ordinal));
        return new BehaviorResult
        {
            Operation = operation,
            Succeeded = succeeded,
            OccurredAtUtc = occurred,
            CompletedAtUtc = DateTimeOffset.UtcNow,
            Local = Endpoint(local),
            Remote = Endpoint(remote),
            BytesSent = request.Length,
            BytesReceived = response.Length,
            Url = url,
            Method = "GET",
            StatusCode = statusCode,
            RequestNonce = nonce,
            DestinationPath = destination,
            DownloadSizeBytes = destination is null ? null : body.Length,
            DownloadMd5 = md5,
            DownloadSha256 = sha256,
            FileOccurredAtUtc = fileOccurred,
        };
    }

    private static BehaviorResult Result(string operation, DateTimeOffset occurred, IPEndPoint local, IPEndPoint remote,
        long sent, long received, bool succeeded, string nonce) => new()
    {
        Operation = operation,
        Succeeded = succeeded,
        OccurredAtUtc = occurred,
        CompletedAtUtc = DateTimeOffset.UtcNow,
        Local = Endpoint(local),
        Remote = Endpoint(remote),
        BytesSent = sent,
        BytesReceived = received,
        RequestNonce = nonce,
        Error = succeeded ? null : "Helper 返回的 nonce 不一致。",
    };

    private static EndpointInfo Endpoint(IPEndPoint endpoint) => Endpoint(endpoint.Address, endpoint.Port);
    private static EndpointInfo Endpoint(IPAddress address, int port) => new()
    {
        Address = address.ToString(),
        Port = port,
        Family = address.AddressFamily == AddressFamily.InterNetworkV6 ? "ipv6" : "ipv4",
    };

    private static string SanitizeLabel(string value)
    {
        var label = new string(value.ToLowerInvariant().Where(char.IsLetterOrDigit).Take(40).ToArray());
        return string.IsNullOrWhiteSpace(label) ? "edrtest" : label;
    }

    private static byte[] Payload(string nonce, int size)
    {
        var marker = Encoding.UTF8.GetBytes($"EDRTEST|{nonce}|NETWORK_FILE_DOWNLOAD|");
        return Enumerable.Range(0, size).Select(index => marker[index % marker.Length]).ToArray();
    }

    private static async Task<byte[]> ReadHeader(NetworkStream stream)
    {
        var bytes = new List<byte>();
        var buffer = new byte[1];
        while (bytes.Count < 16_384)
        {
            var count = await stream.ReadAsync(buffer).AsTask().WaitAsync(IoTimeout);
            if (count == 0) break;
            bytes.Add(buffer[0]);
            if (bytes.Count >= 4 && bytes[^4] == 13 && bytes[^3] == 10 && bytes[^2] == 13 && bytes[^1] == 10) break;
        }
        return bytes.ToArray();
    }

    private static async Task<byte[]> ReadToEnd(NetworkStream stream, CancellationToken token)
    {
        using var output = new MemoryStream();
        var buffer = new byte[8_192];
        while (true)
        {
            var count = await stream.ReadAsync(buffer, token);
            if (count == 0) break;
            await output.WriteAsync(buffer.AsMemory(0, count), token);
        }
        return output.ToArray();
    }

    private static int FindSequence(byte[] source, byte[] sequence)
    {
        for (var index = 0; index <= source.Length - sequence.Length; index++)
            if (source.AsSpan(index, sequence.Length).SequenceEqual(sequence)) return index;
        return -1;
    }

    private static byte[] BuildDnsQuery(string question, ushort id)
    {
        using var output = new MemoryStream();
        WriteUInt16(output, id); WriteUInt16(output, 0x0100); WriteUInt16(output, 1);
        WriteUInt16(output, 0); WriteUInt16(output, 0); WriteUInt16(output, 0);
        WriteDnsName(output, question); WriteUInt16(output, 1); WriteUInt16(output, 1);
        return output.ToArray();
    }

    private static byte[] BuildDnsResponse(byte[] query, IPAddress answer)
    {
        var questionLength = DnsQuestionEnd(query) - 12 + 5;
        using var output = new MemoryStream();
        output.Write(query, 0, 2); WriteUInt16(output, 0x8180); WriteUInt16(output, 1);
        WriteUInt16(output, 1); WriteUInt16(output, 0); WriteUInt16(output, 0);
        output.Write(query, 12, questionLength);
        WriteUInt16(output, 0xC00C); WriteUInt16(output, 1); WriteUInt16(output, 1);
        output.Write([0, 0, 0, 30]); WriteUInt16(output, 4); output.Write(answer.GetAddressBytes());
        return output.ToArray();
    }

    private static string ReadDnsQuestion(byte[] packet)
    {
        var offset = 12;
        var labels = new List<string>();
        while (packet[offset] != 0)
        {
            var length = packet[offset++];
            labels.Add(Encoding.ASCII.GetString(packet, offset, length));
            offset += length;
        }
        return string.Join('.', labels);
    }

    private static IReadOnlyList<string> ReadDnsAnswers(byte[] packet)
    {
        var answerOffset = DnsQuestionEnd(packet) + 5;
        if (packet.Length < answerOffset + 16) return [];
        var dataLength = (packet[answerOffset + 10] << 8) | packet[answerOffset + 11];
        return dataLength == 4 ? [new IPAddress(packet.AsSpan(answerOffset + 12, 4)).ToString()] : [];
    }

    private static int DnsQuestionEnd(byte[] packet)
    {
        var offset = 12;
        while (offset < packet.Length && packet[offset] != 0) offset += packet[offset] + 1;
        if (offset >= packet.Length) throw new InvalidDataException("DNS 查询名格式无效。");
        return offset;
    }

    private static void WriteDnsName(Stream output, string name)
    {
        foreach (var label in name.Split('.'))
        {
            var bytes = Encoding.ASCII.GetBytes(label);
            if (bytes.Length is 0 or > 63) throw new ArgumentException("DNS 标签长度无效。", nameof(name));
            output.WriteByte((byte)bytes.Length); output.Write(bytes);
        }
        output.WriteByte(0);
    }

    private static void WriteUInt16(Stream output, int value)
    {
        output.WriteByte((byte)(value >> 8));
        output.WriteByte((byte)value);
    }
}
