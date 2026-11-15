using System.Net;
using System.Net.Sockets;
using AiCodeAgent.Tests.Loopback;

namespace AiCodeAgent.Core.Tests.Network;

/// <summary>The helper behind the local servers of the network tests: it must not fail because of ports other sockets hold.</summary>
public class LoopbackListenerTests
{
    private static readonly string[] Hosts = { "127.0.0.1", "localhost" };

    [Fact]
    public async Task AStartedListenerServesRequests()
    {
        var listener = LoopbackListener.Start(Hosts, out var port);
        try
        {
            var serve = Task.Run(async () =>
            {
                var ctx = await listener.GetContextAsync();
                ctx.Response.StatusCode = 204;
                ctx.Response.Close();
            });

            using var client = new HttpClient();
            using var response = await client.GetAsync($"http://127.0.0.1:{port}/");
            await serve;

            Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        }
        finally { LoopbackListener.Close(listener); }
    }

    [Fact]
    public void WhenTheChosenPortIsTaken_AnotherOneIsTried()
    {
        var taken = new TcpListener(IPAddress.Loopback, 0);
        taken.Start();
        try
        {
            var takenPort = ((IPEndPoint)taken.LocalEndpoint).Port;
            var free = new TcpListener(IPAddress.Loopback, 0);
            free.Start();
            var freePort = ((IPEndPoint)free.LocalEndpoint).Port;
            free.Stop();
            var candidates = new Queue<int>(new[] { takenPort, takenPort, freePort });

            var listener = LoopbackListener.Start(Hosts, out var port, candidates.Dequeue);
            try
            {
                Assert.Equal(freePort, port);
                Assert.True(listener.IsListening);
            }
            finally { LoopbackListener.Close(listener); }
        }
        finally { taken.Stop(); }
    }

    [Fact]
    public void WhenEveryAttemptFails_TheErrorIsReported()
    {
        var taken = new TcpListener(IPAddress.Loopback, 0);
        taken.Start();
        try
        {
            var takenPort = ((IPEndPoint)taken.LocalEndpoint).Port;

            Assert.Throws<HttpListenerException>(() => LoopbackListener.Start(Hosts, out _, () => takenPort));
        }
        finally { taken.Stop(); }
    }

    [Fact]
    public void ClosingTwice_AndClosingAfterAnotherSocketTookThePort_DoesNotThrow()
    {
        var listener = LoopbackListener.Start(Hosts, out var port);
        LoopbackListener.Close(listener);

        // The old helper stopped and then closed: closing re-bound the port and threw when somebody else held it.
        var thief = new TcpListener(IPAddress.Loopback, port);
        thief.Start();
        try
        {
            LoopbackListener.Close(listener);
        }
        finally { thief.Stop(); }
    }
}
