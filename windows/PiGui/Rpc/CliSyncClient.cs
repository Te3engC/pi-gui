using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using PiGui.Models;

namespace PiGui.Rpc;

public sealed class CliSyncClient : IAsyncDisposable
{
    private readonly HttpClient _http = new() { Timeout = Timeout.InfiniteTimeSpan };
    private Process? _tunnel;
    private CancellationTokenSource? _cancel;
    public event Action<JsonElement>? EventReceived;
    public event Action<string>? ErrorReceived;
    public bool IsConnected => _tunnel is { HasExited: false };

    public async Task ConnectAsync(ConnectionOptions options, string token, int remotePort, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(token)) throw new ArgumentException("请在 SSH 设置中填入 /gui-sync start 显示的 Token。");
        var port = remotePort;
        var info = new ProcessStartInfo { FileName = options.SshExecutable, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        info.ArgumentList.Add("-N"); info.ArgumentList.Add("-o"); info.ArgumentList.Add("BatchMode=yes"); info.ArgumentList.Add("-L"); info.ArgumentList.Add($"{port}:127.0.0.1:{remotePort}"); info.ArgumentList.Add(options.Target);
        _tunnel = Process.Start(info) ?? throw new InvalidOperationException("无法启动 SSH 隧道。");
        await Task.Delay(500, cancellationToken);
        if (_tunnel.HasExited)
        {
            var error = await _tunnel.StandardError.ReadToEndAsync();
            throw new InvalidOperationException($"SSH 隧道启动失败：{error.Trim()}");
        }
        try
        {
            using var checkCancel = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            checkCancel.CancelAfter(TimeSpan.FromSeconds(5));
            using var check = new HttpRequestMessage(HttpMethod.Get, $"http://127.0.0.1:{port}/history");
            check.Headers.Add("x-pi-gui-token", token);
            using var response = await _http.SendAsync(check, checkCancel.Token);
            response.EnsureSuccessStatusCode();
        }
        catch (Exception error)
        {
            try { _tunnel.Kill(true); } catch { }
            throw new InvalidOperationException("无法访问 CLI 同步插件。请确认 CLI 已执行 /gui-sync start 18765，端口和 Token 完全一致。", error);
        }
        _cancel = new CancellationTokenSource();
        _ = Task.Run(() => ReadEventsAsync(port, token, _cancel.Token));
    }
    public async Task<CommandItem[]> GetCommandsAsync(int port, string token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"http://127.0.0.1:{port}/commands"); request.Headers.Add("x-pi-gui-token", token);
        using var response = await _http.SendAsync(request); response.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return doc.RootElement.GetProperty("commands").EnumerateArray().Select(command => new CommandItem(
            "/" + (command.TryGetProperty("name", out var name) ? name.GetString() : ""),
            command.TryGetProperty("description", out var description) ? description.GetString() ?? "" : "",
            command.TryGetProperty("source", out var source) ? source.GetString() ?? "" : "")).ToArray();
    }
    public async Task SendPromptAsync(int port, string token, string message, object? image)
    {
        var body = JsonSerializer.Serialize(new { message, image });
        using var request = new HttpRequestMessage(HttpMethod.Post, $"http://127.0.0.1:{port}/prompt") { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        request.Headers.Add("x-pi-gui-token", token);
        using var response = await _http.SendAsync(request); response.EnsureSuccessStatusCode();
    }

    public async Task AbortAsync(int port, string token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, $"http://127.0.0.1:{port}/abort");
        request.Headers.Add("x-pi-gui-token", token);
        using var response = await _http.SendAsync(request); response.EnsureSuccessStatusCode();
    }
    private async Task ReadEventsAsync(int port, string token, CancellationToken cancellationToken)
    {
        try {
            using var response = await _http.GetAsync($"http://127.0.0.1:{port}/events?token={Uri.EscapeDataString(token)}", HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            response.EnsureSuccessStatusCode(); using var stream = await response.Content.ReadAsStreamAsync(cancellationToken); using var reader = new StreamReader(stream);
            while (!cancellationToken.IsCancellationRequested && await reader.ReadLineAsync(cancellationToken) is { } line) {
                if (!line.StartsWith("data: ")) continue; using var json = JsonDocument.Parse(line[6..]); EventReceived?.Invoke(json.RootElement.Clone());
            }
        } catch (Exception e) when (!cancellationToken.IsCancellationRequested) { ErrorReceived?.Invoke("实时同步断开：" + e.Message); }
    }
    public ValueTask DisposeAsync() { _cancel?.Cancel(); try { if (_tunnel is { HasExited: false }) _tunnel.Kill(true); } catch { } _http.Dispose(); return ValueTask.CompletedTask; }
}
