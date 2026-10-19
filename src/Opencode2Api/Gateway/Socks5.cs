using System.Net;
using System.Net.Sockets;

namespace Opencode2Api.Gateway;

/// <summary>
/// 最小化的 SOCKS5 客户端，覆盖 Go 的
/// golang.org/x/net/proxy 支持的两种认证方式：无认证与用户名/口令。
/// 它通过 SocketsHttpHandler.ConnectCallback 注入，因为
/// 运行时没有内置的 SOCKS 传输。
/// </summary>
internal static class Socks5
{
    private const byte NoAuthentication = 0x00;
    private const byte UsernamePassword = 0x02;
    private const byte CommandConnect = 0x01;
    private const byte AddressIPv4 = 0x01;
    private const byte AddressIPv6 = 0x04;
    private const byte AddressDomain = 0x03;
    private const int MaxDomainLength = 255;

    public static async Task<Stream> ConnectAsync(ProxyEndpoint proxy, string host, int port, TimeSpan timeout, CancellationToken cancellationToken)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        linked.CancelAfter(timeout);
        var token = linked.Token;

        var socket = new Socket(
            Socket.OSSupportsIPv6 ? AddressFamily.InterNetworkV6 : AddressFamily.InterNetwork,
            SocketType.Stream, ProtocolType.Tcp)
        {
            NoDelay = true,
        };
        if (socket.AddressFamily == AddressFamily.InterNetworkV6)
        {
            socket.DualMode = true;
        }
        NetworkStream? stream = null;
        try
        {
            await socket.ConnectAsync(new DnsEndPoint(proxy.Host, proxy.Port), token);
            stream = new NetworkStream(socket, ownsSocket: true);
            await HandshakeAsync(stream, proxy, host, port, token);
            return stream;
        }
        catch
        {
            stream?.Dispose();
            socket.Dispose();
            throw;
        }
    }

    private static async Task HandshakeAsync(NetworkStream stream, ProxyEndpoint proxy, string host, int port, CancellationToken token)
    {
        byte[] methods = proxy.Username.Length > 0 || proxy.Password != null
            ? [UsernamePassword, NoAuthentication]
            : [NoAuthentication];
        var greeting = new byte[2 + methods.Length];
        greeting[0] = 0x05;
        greeting[1] = (byte)methods.Length;
        methods.CopyTo(greeting, 2);
        await stream.WriteAsync(greeting, token);

        var selection = await ReadExactAsync(stream, 2, token);
        if (selection[0] != 0x05)
        {
            throw new GatewayException("socks5 handshake: unexpected protocol version");
        }
        switch (selection[1])
        {
            case NoAuthentication:
                break;
            case UsernamePassword:
                await AuthenticateAsync(stream, proxy, token);
                break;
            case 0xFF:
                throw new GatewayException("socks5 handshake: no acceptable authentication methods");
            default:
                throw new GatewayException($"socks5 handshake: unsupported authentication method {selection[1]}");
        }

        var request = await BuildRequestAsync(proxy, host, port, token);
        await stream.WriteAsync(request, token);

        var header = await ReadExactAsync(stream, 5, token);
        if (header[0] != 0x05)
        {
            throw new GatewayException("socks5 connect: unexpected protocol version");
        }
        if (header[1] != 0x00)
        {
            throw new GatewayException($"socks5 connect: host unreachable ({DescribeReply(header[1])})");
        }
        // CONNECT 不需要绑定地址，但其后残留字节必须
        // 被排空，否则中继流会以残留的帧头开始。
        switch (header[3])
        {
            case AddressIPv4:
                await ReadExactAsync(stream, 4 + 2, token);
                break;
            case AddressIPv6:
                await ReadExactAsync(stream, 16 + 2, token);
                break;
            case AddressDomain:
            {
                var length = await ReadExactAsync(stream, 1, token);
                await ReadExactAsync(stream, length[0] + 2, token);
                break;
            }
            default:
                throw new GatewayException($"socks5 connect: unsupported address type {header[3]}");
        }
    }

    private static async Task AuthenticateAsync(NetworkStream stream, ProxyEndpoint proxy, CancellationToken token)
    {
        var user = System.Text.Encoding.UTF8.GetBytes(proxy.Username);
        var pass = System.Text.Encoding.UTF8.GetBytes(proxy.Password ?? "");
        if (user.Length > 255 || pass.Length > 255)
        {
            throw new GatewayException("socks5 authentication: credential too long");
        }
        var payload = new byte[3 + user.Length + pass.Length];
        payload[0] = 0x01;
        payload[1] = (byte)user.Length;
        user.CopyTo(payload, 2);
        payload[2 + user.Length] = (byte)pass.Length;
        pass.CopyTo(payload, 3 + user.Length);
        await stream.WriteAsync(payload, token);

        var status = await ReadExactAsync(stream, 2, token);
        if (status[0] != 0x01)
        {
            throw new GatewayException("socks5 authentication: unexpected reply version");
        }
        if (status[1] != 0x00)
        {
            throw new GatewayException("socks5 authentication rejected by the proxy");
        }
    }

    /// <summary>
    /// 构造 CONNECT 请求。socks5h 总是把域名交给代理
    /// 去解析；普通 socks5 先在本地解析，因此没有 DNS 的代理
    /// 也能到达调用方给出的地址。
    /// </summary>
    private static async Task<byte[]> BuildRequestAsync(ProxyEndpoint proxy, string host, int port, CancellationToken token)
    {
        var request = new List<byte> { 0x05, CommandConnect, 0x00 };
        if (IPAddress.TryParse(host, out var literal))
        {
            var bytes = literal.GetAddressBytes();
            request.Add(bytes.Length == 4 ? AddressIPv4 : AddressIPv6);
            request.AddRange(bytes);
        }
        else if (host.Length is > 0 and <= MaxDomainLength)
        {
            IPAddress? resolved = null;
            if (!proxy.RemoteResolution)
            {
                try
                {
                    resolved = (await Dns.GetHostAddressesAsync(host, token)).FirstOrDefault();
                }
                catch (SocketException)
                {
                    // 无法解析的名称仍按域名发出，
                    // 让代理可以报告真实的失败原因。
                }
            }
            if (resolved != null)
            {
                var bytes = resolved.GetAddressBytes();
                request.Add(bytes.Length == 4 ? AddressIPv4 : AddressIPv6);
                request.AddRange(bytes);
            }
            else
            {
                request.Add(AddressDomain);
                request.Add((byte)host.Length);
                request.AddRange(System.Text.Encoding.UTF8.GetBytes(host));
            }
        }
        else
        {
            throw new GatewayException("socks5 connect: invalid destination host");
        }
        request.Add((byte)(port >> 8));
        request.Add((byte)(port & 0xFF));
        return request.ToArray();
    }

    private static string DescribeReply(byte code) => code switch
    {
        0x01 => "general failure",
        0x02 => "connection not allowed by ruleset",
        0x03 => "network unreachable",
        0x04 => "host unreachable",
        0x05 => "connection refused by destination host",
        0x06 => "TTL expired",
        0x07 => "command not supported",
        0x08 => "address type not supported",
        _ => $"reply code {code}",
    };

    private static async Task<byte[]> ReadExactAsync(NetworkStream stream, int count, CancellationToken token)
    {
        var buffer = new byte[count];
        var read = 0;
        while (read < count)
        {
            var got = await stream.ReadAsync(buffer.AsMemory(read, count - read), token);
            if (got == 0)
            {
                throw new GatewayException("socks5 handshake: connection closed by the proxy");
            }
            read += got;
        }
        return buffer;
    }
}

/// <summary>向网关调用方暴露的启动与传输失败。</summary>
public sealed class GatewayException(string message, Exception? inner = null) : Exception(message, inner);
