using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading.Channels;
using Andy.MCP.Protocol;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Andy.MCP.Transport;

/// <summary>
/// Configuration for a stdio client transport.
/// </summary>
public sealed record StdioClientTransportOptions
{
    /// <summary>
    /// The command to execute (e.g., "/usr/bin/mcp-server" or "npx").
    /// </summary>
    public required string Command { get; init; }

    /// <summary>
    /// Command-line arguments.
    /// </summary>
    public string? Arguments { get; init; }

    /// <summary>
    /// Working directory for the process.
    /// </summary>
    public string? WorkingDirectory { get; init; }

    /// <summary>
    /// Environment variables to pass to the process (merged with inherited env).
    /// </summary>
    public IDictionary<string, string>? EnvironmentVariables { get; init; }

    /// <summary>
    /// How long to wait for the process to exit after closing stdin.
    /// </summary>
    public TimeSpan ShutdownTimeout { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Grace period after SIGTERM before sending SIGKILL.
    /// </summary>
    public TimeSpan KillGraceTimeout { get; init; } = TimeSpan.FromSeconds(3);
}

/// <summary>
/// Client transport that communicates with an MCP server via stdin/stdout of a child process.
/// Messages are newline-delimited JSON-RPC.
/// </summary>
public sealed class StdioClientTransport : IClientTransport
{
    private readonly StdioClientTransportOptions _options;
    private readonly ILogger _logger;
    private readonly Channel<JsonRpcMessage> _outgoing;
    private Process? _process;
    private Task? _readLoop;
    private Task? _writeLoop;
    private Task? _stderrLoop;
    private CancellationTokenSource? _cts;
    private volatile bool _connected;
    private volatile bool _disposed;

    public bool IsConnected => _connected;
    public event EventHandler<TransportDisconnectedEventArgs>? Disconnected;

    public StdioClientTransport(StdioClientTransportOptions options, ILogger? logger = null)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? NullLogger.Instance;
        _outgoing = Channel.CreateUnbounded<JsonRpcMessage>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false
        });
    }

    public async Task ConnectAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_connected) throw new InvalidOperationException("Transport is already connected.");

        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        var psi = new ProcessStartInfo
        {
            FileName = _options.Command,
            Arguments = _options.Arguments ?? "",
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardInputEncoding = new UTF8Encoding(false),
            StandardOutputEncoding = new UTF8Encoding(false),
            StandardErrorEncoding = new UTF8Encoding(false),
        };

        if (_options.WorkingDirectory is not null)
            psi.WorkingDirectory = _options.WorkingDirectory;

        if (_options.EnvironmentVariables is not null)
        {
            foreach (var (key, value) in _options.EnvironmentVariables)
                psi.Environment[key] = value;
        }

        _process = new Process { StartInfo = psi, EnableRaisingEvents = true };
        _process.Exited += OnProcessExited;

        if (!_process.Start())
            throw new InvalidOperationException($"Failed to start process: {_options.Command}");

        _connected = true;
        _logger.LogInformation("stdio transport connected: PID {Pid}, command: {Command}", _process.Id, _options.Command);

        _readLoop = Task.Run(() => ReadLoopAsync(_cts.Token), _cts.Token);
        _writeLoop = Task.Run(() => WriteLoopAsync(_cts.Token), _cts.Token);
        _stderrLoop = Task.Run(() => StderrLoopAsync(_cts.Token), _cts.Token);
    }

    public async Task SendAsync(JsonRpcMessage message, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_connected) throw new InvalidOperationException("Transport is not connected.");

        await _outgoing.Writer.WriteAsync(message, cancellationToken);
    }

    public IAsyncEnumerable<JsonRpcMessage> Messages => ReadMessagesAsync();

    private readonly Channel<JsonRpcMessage> _incoming = Channel.CreateUnbounded<JsonRpcMessage>(
        new UnboundedChannelOptions { SingleWriter = true, SingleReader = false });

    private async IAsyncEnumerable<JsonRpcMessage> ReadMessagesAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (var message in _incoming.Reader.ReadAllAsync(cancellationToken))
        {
            yield return message;
        }
    }

    private async Task ReadLoopAsync(CancellationToken ct)
    {
        try
        {
            var reader = _process!.StandardOutput;
            while (!ct.IsCancellationRequested)
            {
                var line = await reader.ReadLineAsync(ct);
                if (line is null) break; // EOF — process closed stdout

                if (string.IsNullOrWhiteSpace(line)) continue; // Skip empty lines

                try
                {
                    var message = McpJsonDefaults.Deserialize(line);
                    await _incoming.Writer.WriteAsync(message, ct);
                }
                catch (JsonRpcParseException ex)
                {
                    _logger.LogWarning(ex, "Failed to parse JSON-RPC message from stdout: {Line}", line);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogWarning(ex, "Error deserializing message from stdout");
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Read loop error");
        }
        finally
        {
            _incoming.Writer.TryComplete();
        }
    }

    private async Task WriteLoopAsync(CancellationToken ct)
    {
        try
        {
            var writer = _process!.StandardInput;
            await foreach (var message in _outgoing.Reader.ReadAllAsync(ct))
            {
                var json = McpJsonDefaults.Serialize(message);
                await writer.WriteLineAsync(json.AsMemory(), ct);
                await writer.FlushAsync(ct);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Write loop error");
        }
    }

    private async Task StderrLoopAsync(CancellationToken ct)
    {
        try
        {
            var reader = _process!.StandardError;
            while (!ct.IsCancellationRequested)
            {
                var line = await reader.ReadLineAsync(ct);
                if (line is null) break;
                _logger.LogDebug("[stderr] {Line}", line);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Stderr loop ended");
        }
    }

    private void OnProcessExited(object? sender, EventArgs e)
    {
        var exitCode = _process?.ExitCode;
        _logger.LogInformation("stdio process exited with code {ExitCode}", exitCode);
        _connected = false;
        _incoming.Writer.TryComplete();
        Disconnected?.Invoke(this, new TransportDisconnectedEventArgs
        {
            Reason = $"Process exited with code {exitCode}",
            ExitCode = exitCode
        });
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        _connected = false;
        _outgoing.Writer.TryComplete();

        if (_process is not null && !_process.HasExited)
        {
            try
            {
                // Step 1: Close stdin to signal EOF
                try { _process.StandardInput.Close(); } catch { }

                // Step 2: Wait for graceful exit
                var exited = _process.WaitForExit((int)_options.ShutdownTimeout.TotalMilliseconds);

                if (!exited)
                {
                    // Step 3: SIGTERM (Unix) or Kill (Windows)
                    _logger.LogWarning("Process did not exit within timeout, sending kill signal");
                    try { _process.Kill(entireProcessTree: true); } catch { }
                    _process.WaitForExit((int)_options.KillGraceTimeout.TotalMilliseconds);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error during process shutdown");
            }
        }

        _cts?.Cancel();

        // Wait for loops to finish
        try
        {
            if (_readLoop is not null) await _readLoop.ConfigureAwait(false);
        }
        catch { }
        try
        {
            if (_writeLoop is not null) await _writeLoop.ConfigureAwait(false);
        }
        catch { }
        try
        {
            if (_stderrLoop is not null) await _stderrLoop.ConfigureAwait(false);
        }
        catch { }

        _process?.Dispose();
        _cts?.Dispose();
    }
}
