using System.Net;
using System.Net.Sockets;
using TelltaleKube.Core.Api;

namespace TelltaleKube.Core.Tests;

public class PortForwarderTests
{
    private static KubeClient Client() => new(new HttpClientHandler(), new Uri("https://cluster.test/"));

    [Fact]
    public async Task Prefers_the_same_local_port_and_binds_loopback_only()
    {
        var port = FreePort();
        using var client = Client();
        await using var fwd = PortForwarder.Start(client, "ns", "pod", port);
        Assert.Equal(port, fwd.LocalPort);
    }

    [Fact]
    public async Task Falls_back_to_a_free_port_when_the_same_port_is_taken()
    {
        var busy = new TcpListener(IPAddress.Loopback, 0);
        busy.Start();
        try
        {
            var taken = ((IPEndPoint)busy.LocalEndpoint).Port;
            using var client = Client();
            await using var fwd = PortForwarder.Start(client, "ns", "pod", taken);
            Assert.NotEqual(taken, fwd.LocalPort);
            Assert.True(fwd.LocalPort > 0);
        }
        finally
        {
            busy.Stop();
        }
    }

    [Fact]
    public async Task Privileged_pod_ports_get_a_random_local_port()
    {
        using var client = Client();
        await using var fwd = PortForwarder.Start(client, "ns", "pod", 80);
        Assert.NotEqual(80, fwd.LocalPort);
    }

    [Theory]
    [InlineData(443, null, "https")]
    [InlineData(8443, null, "https")]
    [InlineData(9000, "https-web", "https")]
    [InlineData(8080, "http", "http")]
    public void Scheme_guess(int port, string? name, string expected) => Assert.Equal(expected, PortForwarder.GuessScheme(port, name));

    private static int FreePort()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        var p = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return p;
    }
}
