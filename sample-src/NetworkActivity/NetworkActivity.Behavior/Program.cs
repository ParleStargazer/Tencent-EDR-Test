using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
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
        var methodId = options.Get("method") ?? operation switch
        {
            "url_access" => "raw_socket",
            "dns_query" => "raw_udp",
            "file_download" => "raw_http_three_part",
            _ => "socket",
        };
        var resultPath = Path.GetFullPath(options.Require("result"));
        var address = IPAddress.Parse(options.Get("address") ?? "0.0.0.0");
        var port = options.GetInt("port", operation == "dns_query" ? 53 : 0,
            operation == "dns_query" ? 1 : 0, 65_535);
        var nonce = options.Require("nonce");
        var holdMs = options.GetInt("hold-ms", 1_500, 0, 30_000);
        BehaviorResult result;
        try
        {
            result = (operation, methodId) switch
            {
                ("tcp_connect", _) => await ConnectTcp(operation, methodId, address, port, nonce),
                ("udp_connect", _) => await ConnectUdp(operation, methodId, address, port, nonce),
                ("dns_query", "raw_udp") => await QueryDns(operation, methodId, address, port, options.Require("question"), nonce),
                ("dns_query", "windows_dns_client") => QuerySystemDns(operation, methodId, options.Require("question"), nonce),
                ("url_access", "raw_socket") => await RequestHttp(operation, methodId, address, port, options.Require("url"), nonce, null),
                ("url_access", "wininet") => RequestWinInet(operation, methodId, address, port, options.Require("url"), nonce),
                ("file_download", _) => await RequestHttp(operation, methodId, address, port, options.Require("url"), nonce,
                    Path.GetFullPath(options.Require("destination"))),
                _ => throw new ArgumentException($"不支持的网络操作或测试方法：{operation}/{methodId}"),
            };
        }
        catch (Exception exception)
        {
            result = new BehaviorResult
            {
                Operation = operation,
                MethodId = methodId,
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

    private static async Task<BehaviorResult> ConnectTcp(string operation, string methodId, IPAddress address, int port, string nonce)
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
        return Result(operation, methodId, occurred, local, remote, payload.Length, count, reply == "ACK|" + nonce, nonce);
    }

    private static async Task<BehaviorResult> ConnectUdp(string operation, string methodId, IPAddress address, int port, string nonce)
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
        return Result(operation, methodId, occurred, local, remote, payload.Length, packet.Buffer.Length, reply == "ACK|" + nonce, nonce);
    }

    private static async Task<BehaviorResult> QueryDns(
        string operation, string methodId, IPAddress address, int port, string question, string nonce)
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
            MethodId = methodId,
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
        string operation, string methodId, IPAddress address, int port, string url, string nonce, string? destination)
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
        DateTimeOffset? fileCompleted = null;
        string? md5 = null;
        string? sha256 = null;
        if (destination is not null)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            fileOccurred = DateTimeOffset.UtcNow;
            await File.WriteAllBytesAsync(destination, body, timeout.Token);
            fileCompleted = DateTimeOffset.UtcNow;
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
            MethodId = methodId,
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
            FileCompletedAtUtc = fileCompleted,
        };
    }

    private static BehaviorResult RequestWinInet(
        string operation, string methodId, IPAddress address, int port, string url, string nonce)
    {
        var occurred = DateTimeOffset.UtcNow;
        var session = InternetOpen("Tencent-EDR-Test/0.2", InternetOpenTypeDirect, null, null, 0);
        if (session == IntPtr.Zero) throw new IOException($"InternetOpenW 失败，Win32={Marshal.GetLastWin32Error()}。");
        try
        {
            var headers = $"Cache-Control: no-cache\r\nPragma: no-cache\r\nX-EDR-Test-Nonce: {nonce}\r\n";
            var request = InternetOpenUrl(session, url, headers, headers.Length,
                InternetFlagReload | InternetFlagNoCacheWrite | InternetFlagNoUi, UIntPtr.Zero);
            if (request == IntPtr.Zero)
                throw new IOException($"InternetOpenUrlW 失败，Win32={Marshal.GetLastWin32Error()}。");
            try
            {
                var statusCode = QueryHttpStatusCode(request);
                using var output = new MemoryStream();
                var buffer = new byte[8_192];
                while (true)
                {
                    if (!InternetReadFile(request, buffer, buffer.Length, out var read))
                        throw new IOException($"InternetReadFile 失败，Win32={Marshal.GetLastWin32Error()}。");
                    if (read == 0) break;
                    output.Write(buffer, 0, checked((int)read));
                }
                var body = output.ToArray();
                var succeeded = statusCode == 200 && Encoding.UTF8.GetString(body).Contains(nonce, StringComparison.Ordinal);
                return new BehaviorResult
                {
                    Operation = operation,
                    MethodId = methodId,
                    Succeeded = succeeded,
                    OccurredAtUtc = occurred,
                    CompletedAtUtc = DateTimeOffset.UtcNow,
                    Local = Endpoint(IPAddress.Any, 0),
                    Remote = Endpoint(address, port),
                    BytesSent = headers.Length,
                    BytesReceived = body.Length,
                    Url = url,
                    Method = "GET",
                    StatusCode = statusCode,
                    RequestNonce = nonce,
                    NativeApi = "InternetOpenUrlW",
                    NativeStatusCode = statusCode,
                    Error = succeeded ? null : "WinINet 响应未通过状态码或 nonce 校验。",
                };
            }
            finally
            {
                InternetCloseHandle(request);
            }
        }
        finally
        {
            InternetCloseHandle(session);
        }
    }

    private static BehaviorResult QuerySystemDns(string operation, string methodId, string question, string nonce)
    {
        var occurred = DateTimeOffset.UtcNow;
        var status = DnsQuery(question, DnsTypeA,
            DnsQueryBypassCache | DnsQueryNoHostsFile | DnsQueryTreatAsFqdn,
            IntPtr.Zero, out var records, IntPtr.Zero);
        if (records != IntPtr.Zero) DnsRecordListFree(records, DnsFreeRecordList);
        var succeeded = status is DnsSuccess or DnsErrorRcodeNameError;
        return new BehaviorResult
        {
            Operation = operation,
            MethodId = methodId,
            Succeeded = succeeded,
            OccurredAtUtc = occurred,
            CompletedAtUtc = DateTimeOffset.UtcNow,
            Local = Endpoint(IPAddress.Any, 0),
            Remote = Endpoint(IPAddress.Any, 53),
            BytesSent = 0,
            BytesReceived = 0,
            RequestNonce = nonce,
            DnsQuestion = question,
            DnsQueryType = "A",
            DnsAnswers = [],
            NativeApi = "DnsQuery_W",
            NativeStatusCode = status,
            Error = succeeded ? null : $"DnsQuery_W 返回 DNS_STATUS={status}。",
        };
    }

    private static int QueryHttpStatusCode(IntPtr request)
    {
        var size = sizeof(int);
        var statusCode = 0;
        if (!HttpQueryInfo(request, HttpQueryStatusCode | HttpQueryFlagNumber, ref statusCode, ref size, IntPtr.Zero))
            throw new IOException($"HttpQueryInfoW 失败，Win32={Marshal.GetLastWin32Error()}。");
        return statusCode;
    }

    private static BehaviorResult Result(string operation, string methodId, DateTimeOffset occurred, IPEndPoint local, IPEndPoint remote,
        long sent, long received, bool succeeded, string nonce) => new()
    {
        Operation = operation,
        MethodId = methodId,
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

    private const int InternetOpenTypeDirect = 1;
    private const int InternetFlagReload = unchecked((int)0x80000000);
    private const int InternetFlagNoCacheWrite = 0x04000000;
    private const int InternetFlagNoUi = 0x00000200;
    private const int HttpQueryStatusCode = 19;
    private const int HttpQueryFlagNumber = 0x20000000;
    private const ushort DnsTypeA = 1;
    private const uint DnsQueryBypassCache = 0x00000008;
    private const uint DnsQueryNoHostsFile = 0x00000040;
    private const uint DnsQueryTreatAsFqdn = 0x00001000;
    private const int DnsSuccess = 0;
    private const int DnsErrorRcodeNameError = 9003;
    private const int DnsFreeRecordList = 1;

    [DllImport("wininet.dll", EntryPoint = "InternetOpenW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr InternetOpen(
        string agent, int accessType, string? proxy, string? proxyBypass, int flags);

    [DllImport("wininet.dll", EntryPoint = "InternetOpenUrlW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr InternetOpenUrl(
        IntPtr internet, string url, string? headers, int headersLength, int flags, UIntPtr context);

    [DllImport("wininet.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool InternetReadFile(IntPtr file, byte[] buffer, int bytesToRead, out uint bytesRead);

    [DllImport("wininet.dll", EntryPoint = "HttpQueryInfoW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool HttpQueryInfo(
        IntPtr request, int infoLevel, ref int buffer, ref int bufferLength, IntPtr index);

    [DllImport("wininet.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool InternetCloseHandle(IntPtr internet);

    [DllImport("dnsapi.dll", EntryPoint = "DnsQuery_W", CharSet = CharSet.Unicode)]
    private static extern int DnsQuery(
        string name, ushort type, uint options, IntPtr extra, out IntPtr queryResults, IntPtr reserved);

    [DllImport("dnsapi.dll")]
    private static extern void DnsRecordListFree(IntPtr recordList, int freeType);
}
