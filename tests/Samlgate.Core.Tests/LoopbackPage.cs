using System.Net;
using System.Net.Sockets;
using System.Text;

namespace Samlgate.Tests;

/// <summary>A stable HTTP origin for synthetic browser storage tests; loopback only.</summary>
internal sealed class LoopbackPage : IAsyncDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _serve;
    private readonly byte[] _response;

    public Uri Url { get; }

    public LoopbackPage(string html)
    {
        var body = Encoding.UTF8.GetBytes(html);
        var header = Encoding.ASCII.GetBytes(
            $"HTTP/1.1 200 OK\r\nContent-Type: text/html; charset=utf-8\r\nContent-Length: {body.Length}\r\nConnection: close\r\nCache-Control: no-store\r\n\r\n");
        _response = [.. header, .. body];
        _listener.Start();
        Url = new Uri($"http://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}/idp");
        _serve = ServeAsync();
    }

    private async Task ServeAsync()
    {
        try
        {
            while (!_stop.IsCancellationRequested)
            {
                using var client = await _listener.AcceptTcpClientAsync(_stop.Token);
                using var stream = client.GetStream();
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
                deadline.CancelAfter(TimeSpan.FromSeconds(5));
                try
                {
                    // Read only request headers, never an assertion POST body.
                    using var reader = new StreamReader(stream, Encoding.ASCII, leaveOpen: true);
                    while (await reader.ReadLineAsync(deadline.Token) is { Length: > 0 }) { }
                    await stream.WriteAsync(_response, deadline.Token);
                }
                catch (IOException) { }
                catch (OperationCanceledException) when (!_stop.IsCancellationRequested) { }
            }
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
    }

    public async ValueTask DisposeAsync()
    {
        _stop.Cancel();
        try { await _serve; }
        finally
        {
            _listener.Stop();
            _stop.Dispose();
        }
    }
}
