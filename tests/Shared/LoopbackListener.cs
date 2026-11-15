using System.Net;
using System.Net.Sockets;

namespace AiCodeAgent.Tests.Loopback;

/// <summary>
/// An <see cref="HttpListener"/> on a free loopback port for tests that need a tiny local server. This file is linked (not
/// copied) into the test projects that use it.
/// <list type="bullet">
/// <item>The port is chosen by asking the system for one and releasing it again, so another socket can take it before the
/// listener starts. <see cref="Start"/> therefore tries again with a new port when starting fails.</item>
/// <item>The listener is closed with <see cref="HttpListener.Close()"/> only. Calling <c>Stop()</c> first makes <c>Close()</c>
/// remove the prefixes a second time, which binds the port again and throws "Address already in use" when another socket
/// (for example an outgoing connection of the test itself) has taken it in the meantime.</item>
/// </list>
/// </summary>
internal static class LoopbackListener
{
    private const int Attempts = 5;

    /// <param name="hosts">Host names for the prefixes, such as <c>127.0.0.1</c> and <c>localhost</c>.</param>
    /// <param name="port">The port the listener ended up on.</param>
    /// <param name="pickPort">Chooses a candidate port; tests of this helper replace it. Defaults to a free system port.</param>
    public static HttpListener Start(IReadOnlyList<string> hosts, out int port, Func<int>? pickPort = null)
    {
        pickPort ??= FreePort;
        for (var attempt = 1; ; attempt++)
        {
            port = pickPort();
            var listener = new HttpListener();
            foreach (var host in hosts) listener.Prefixes.Add($"http://{host}:{port}/");
            try
            {
                listener.Start();
                return listener;
            }
            catch (HttpListenerException) when (attempt < Attempts)
            {
                Close(listener); // the port was taken in between: try another one
            }
        }
    }

    /// <summary>Stops and releases the listener. Safe to call twice and after a failed start.</summary>
    public static void Close(HttpListener listener)
    {
        try { listener.Close(); }
        catch (Exception ex) when (ex is HttpListenerException or ObjectDisposedException) { /* already gone */ }
    }

    private static int FreePort()
    {
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        try { return ((IPEndPoint)probe.LocalEndpoint).Port; }
        finally { probe.Stop(); }
    }
}
