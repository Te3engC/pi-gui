using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;

namespace PiGui.Rpc;

public sealed class SshRpcClient : IAsyncDisposable
{
    private readonly ConcurrentDictionary<string, TaskCompletionSource<JsonElement>> _responses = new();
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly JsonSerializerOptions _json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    private Process? _process;
    private Task? _stdoutTask;
    private Task? _stderrTask;

    public event Action<JsonElement>? EventReceived;
    public event Action<string>? ErrorReceived;
    public event Action? Disconnected;
    public bool IsConnected => _process is { HasExited: false };

    public async Task ConnectAsync(ConnectionOptions options, CancellationToken cancellationToken)
    {
        if (IsConnected) throw new InvalidOperationException("Pi 已连接。");
        if (string.IsNullOrWhiteSpace(options.Target))
            throw new ArgumentException("SSH 目标不能为空。");

        // SSH non-interactive sessions usually do not load .bashrc. Explicitly
        // select the Node 22 runtime required by the installed Pi package.
        var remoteCommand = $"export PATH={QuoteShell(options.NodeBinDirectory)}:$PATH; cd -- {QuoteShell(options.WorkingDirectory)} && exec {QuoteShell(options.PiExecutable)} --mode rpc";
        var startInfo = new ProcessStartInfo
        {
            FileName = options.SshExecutable,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardInputEncoding = new UTF8Encoding(false),
            StandardOutputEncoding = new UTF8Encoding(false),
            StandardErrorEncoding = new UTF8Encoding(false),
        };
        startInfo.ArgumentList.Add("-T");
        startInfo.ArgumentList.Add("-o");
        startInfo.ArgumentList.Add("BatchMode=yes");
        startInfo.ArgumentList.Add(options.Target);
        startInfo.ArgumentList.Add(remoteCommand);

        var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        process.Exited += (_, _) => OnDisconnected();
        if (!process.Start()) throw new InvalidOperationException("无法启动 ssh.exe。");
        _process = process;
        _stdoutTask = Task.Run(ReadStdoutAsync);
        _stderrTask = Task.Run(ReadStderrAsync);

        try
        {
            await SendCommandAsync(new { type = "get_state" }, cancellationToken);
        }
        catch
        {
            await DisconnectAsync();
            throw;
        }
    }

    public async Task<JsonElement> SendCommandAsync(object command, CancellationToken cancellationToken = default)
    {
        if (_process is not { HasExited: false } process)
            throw new InvalidOperationException("Pi 未连接。");

        var element = JsonSerializer.SerializeToElement(command, _json);
        var id = element.TryGetProperty("id", out var idElement) ? idElement.GetString() : null;
        id ??= Guid.NewGuid().ToString("N");
        var map = element.EnumerateObject().ToDictionary(property => property.Name, property => property.Value);
        map["id"] = JsonSerializer.SerializeToElement(id);
        var json = JsonSerializer.Serialize(map, _json);
        var completion = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_responses.TryAdd(id, completion)) throw new InvalidOperationException("RPC 请求 ID 冲突。");

        try
        {
            await _writeLock.WaitAsync(cancellationToken);
            try
            {
                await process.StandardInput.WriteLineAsync(json);
                await process.StandardInput.FlushAsync();
            }
            finally
            {
                _writeLock.Release();
            }
            return await completion.Task.WaitAsync(cancellationToken);
        }
        finally
        {
            _responses.TryRemove(id, out _);
        }
    }

    public async Task DisconnectAsync()
    {
        var process = _process;
        _process = null;
        if (process is null) return;
        try
        {
            process.StandardInput.Close();
            if (!process.WaitForExit(1000)) process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException)
        {
            // Process has already exited.
        }
        await Task.WhenAll(_stdoutTask ?? Task.CompletedTask, _stderrTask ?? Task.CompletedTask);
        process.Dispose();
        OnDisconnected();
    }

    private async Task ReadStdoutAsync()
    {
        try
        {
            while (_process is { } process && await process.StandardOutput.ReadLineAsync() is { } line)
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                using var document = JsonDocument.Parse(line);
                var item = document.RootElement.Clone();
                if (item.TryGetProperty("type", out var type) && type.GetString() == "response" &&
                    item.TryGetProperty("id", out var id) && id.GetString() is { } responseId &&
                    _responses.TryGetValue(responseId, out var completion))
                {
                    completion.TrySetResult(item);
                }
                else
                {
                    EventReceived?.Invoke(item);
                }
            }
        }
        catch (Exception exception)
        {
            ErrorReceived?.Invoke($"Pi RPC 输出解析失败：{exception.Message}");
        }
    }

    private async Task ReadStderrAsync()
    {
        try
        {
            while (_process is { } process && await process.StandardError.ReadLineAsync() is { } line)
            {
                if (!string.IsNullOrWhiteSpace(line)) ErrorReceived?.Invoke(line);
            }
        }
        catch (Exception exception)
        {
            ErrorReceived?.Invoke($"SSH 错误输出读取失败：{exception.Message}");
        }
    }

    private void OnDisconnected()
    {
        foreach (var response in _responses.Values)
            response.TrySetException(new IOException("Pi SSH 连接已断开。"));
        Disconnected?.Invoke();
    }

    private static string QuoteShell(string value) => "'" + value.Replace("'", "'\"'\"'") + "'";

    public async ValueTask DisposeAsync()
    {
        await DisconnectAsync();
        _writeLock.Dispose();
    }
}

public sealed record ConnectionOptions(string Target, string WorkingDirectory, string PiExecutable, string NodeBinDirectory, string SshExecutable, string SyncToken = "", int SyncPort = 18765);
