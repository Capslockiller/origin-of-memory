// yazan: codex · gpt-5
using System.Net;
using System.Net.Sockets;
using System.Text;
using Oom.Contracts;

namespace Oom.Tests;

public sealed class LoopbackScars
{
    [Fact]
    public void Runner_DirectNonLoopbackUrl_IsRejected() =>
        Assert.Throws<FormatException>(() => new Runner(null, localUrl: "http://192.0.2.10:11434/v1"));

    [Fact]
    public void HttpTransport_NonLoopbackDestination_IsRejected() =>
        Assert.Throws<FormatException>(() => new HttpTransport().Send("POST", "http://192.0.2.10:11434/api/chat", "{}"));

    [Fact]
    public void HttpTransport_RedirectToNonLoopback_IsNotFollowed()
    {
        using var server = new OneShotServer(
            "HTTP/1.1 302 Found\r\nLocation: http://192.0.2.10/escaped\r\nContent-Length: 0\r\nConnection: close\r\n\r\n");

        var error = Assert.Throws<IOException>(() =>
            new HttpTransport().Send("POST", server.Url + "api/chat", "{}"));

        Assert.Contains("HTTP 302", error.Message);
        Assert.Equal(1, server.Requests);
    }

    [Fact]
    public void HttpTransport_ConfiguredSystemProxy_IsNotUsed()
    {
        using var server = new OneShotServer(
            "HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Length: 2\r\nConnection: close\r\n\r\n{}");
        var original = HttpClient.DefaultProxy;
        var proxy = new RecordingProxy();
        HttpClient.DefaultProxy = proxy;
        try
        {
            using var control = new HttpClient();
            Assert.Throws<HttpRequestException>(() => control.GetStringAsync("http://127.0.0.1:1").GetAwaiter().GetResult());
            Assert.True(proxy.Accesses > 0, "Kontrol istemcisi yapılandırılmış sistem proxy'sini görmedi.");
            proxy.Reset();

            Assert.Equal("{}", new HttpTransport().Send("POST", server.Url + "api/chat", "{}"));
            Assert.Equal(0, proxy.Accesses);
            Assert.Equal(1, server.Requests);
        }
        finally
        {
            HttpClient.DefaultProxy = original;
        }
    }

    private sealed class RecordingProxy : IWebProxy
    {
        public int Accesses { get; private set; }
        public ICredentials? Credentials { get; set; }
        public void Reset() => Accesses = 0;

        public Uri GetProxy(Uri destination)
        {
            Accesses++;
            return new Uri("http://127.0.0.1:1");
        }

        public bool IsBypassed(Uri host)
        {
            Accesses++;
            return false;
        }
    }

    private sealed class OneShotServer : IDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly Task _exchange;

        public OneShotServer(string response)
        {
            _listener.Start();
            Url = $"http://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}/";
            _exchange = Task.Run(async () =>
            {
                using var client = await _listener.AcceptTcpClientAsync();
                using var stream = client.GetStream();
                using var reader = new StreamReader(stream, Encoding.ASCII, leaveOpen: true);
                while (await reader.ReadLineAsync() is { Length: > 0 }) { }
                Requests++;
                await stream.WriteAsync(Encoding.ASCII.GetBytes(response));
            });
        }

        public string Url { get; }
        public int Requests { get; private set; }

        public void Dispose()
        {
            _listener.Stop();
            Assert.True(_exchange.Wait(TimeSpan.FromSeconds(3)), "Yerel HTTP sunucusu isteği tamamlamadı.");
            _exchange.GetAwaiter().GetResult();
        }
    }
}
