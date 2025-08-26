using Microsoft.Extensions.Logging;
using N4313.Enums;
using N4313.Interfaces;
using System.IO.Pipelines;
using System.Text;
using System.Buffers;

namespace N4313;

public class N4313 : IBarcodeScanner
{
  public event EventHandler<string>? OnGoodRead;
  private const string COMMAND_PREFIX = "\x16" + "M" + "\x0D";
  private const string ACTIVATE_ENGINE_COMMAND = "\x16" + "T" + "\x0D";
  private const string DEACTIVATE_ENGINE_COMMAND = "\x16" + "U" + "\x0D";
  private readonly string CONTINUOUS_SCAN_MODE_COMMAND = COMMAND_PREFIX + "pappm3!";
  private readonly string TRIGGER_SCAN_MODE_COMMAND = COMMAND_PREFIX + "aosdft!";
  private EScannerMode _currentMode;
  public EScannerMode CurrentMode => _currentMode;
  private CancellationTokenSource? _listenerCts;
  private Task? _listenerTask;
  private TaskCompletionSource<string>? _scanTcs;
  private readonly ILogger<N4313> _logger;
  private readonly SemaphoreSlim _semaphore = new(1, 1);
  protected PipeWriter Writer { get; }
  protected PipeReader Reader { get; }

  public N4313(ILogger<N4313> logger, IDuplexPipe pipe)
  {
    _logger = logger;
    Reader = pipe.Input;
    Writer = pipe.Output;

    _listenerCts = new CancellationTokenSource();
    _listenerTask = Task.Run(() => ReadLoopAsync(_listenerCts.Token));
  }

  public async Task<string> Scan(CancellationToken cancellationToken)
  {
    if (_currentMode == EScannerMode.Continuous)
    {
      _logger.LogWarning("Scan attempted in Continuous mode. Operation not allowed.");
      throw new InvalidOperationException("Can't trigger scan in continous mode!");
    }

    await _semaphore.WaitAsync();

    cancellationToken.ThrowIfCancellationRequested();

    try
    {
      _logger.LogInformation("Starting scan...");

      _scanTcs = new TaskCompletionSource<string>();

      using var Registration = cancellationToken.Register(() =>
      {
        _scanTcs?.TrySetCanceled(cancellationToken);
      });


      await SendCommandAsync(ACTIVATE_ENGINE_COMMAND, cancellationToken);

      string scanResult = await _scanTcs.Task;

      _logger.LogInformation("Scan completed successfully. Result: {Result}", scanResult);
      return scanResult;
    }
    catch (OperationCanceledException)
    {
      _logger.LogWarning("Scan operation was cancelled.");
      throw;
    }
    catch (Exception ex)
    {
      _logger.LogError(ex, "Scan failed due to an unexpected error.");
      throw new InvalidOperationException("Failed to scan", ex);
    }
    finally
    {
      _semaphore.Release();
    }
  }

  public async Task SetMode(EScannerMode mode)
  {
    if (_currentMode == mode)
    {
      _logger.LogInformation("Scanner already in {Mode} mode, no action taken.", mode);
      return;
    }

    await _semaphore.WaitAsync();

    try
    {
      _logger.LogInformation("Attempting to set scanner mode to {Mode}", mode);

      _logger.LogDebug("Connection ensured, preparing mode change command.");

      string message = mode switch
      {
        EScannerMode.Continuous => CONTINUOUS_SCAN_MODE_COMMAND,
        EScannerMode.Trigger => TRIGGER_SCAN_MODE_COMMAND,
        _ => throw new ArgumentOutOfRangeException(nameof(mode))
      };

      using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));

      _logger.LogDebug("Sending command '{Command}' for mode {Mode}", message, mode);
      await SendCommandAsync(message, cts.Token);

      _currentMode = mode;

      _logger.LogInformation("Scanner mode successfully changed to {Mode}", mode);
    }
    catch (OperationCanceledException)
    {
      _logger.LogWarning("Setting scanner mode to {Mode} timed out.", mode);
      throw;
    }
    catch (Exception ex)
    {
      _logger.LogError(ex, "Failed to set scanner mode to {Mode}", mode);
      throw new InvalidOperationException("Failed to set scanner mode.", ex);
    }
    finally
    {
      _semaphore.Release();
    }
  }

  private async Task SendCommandAsync(string command, CancellationToken cancellationToken)
  {
    try
    {
      string fullCommand = command;
      byte[] commandBytes = Encoding.ASCII.GetBytes(fullCommand);
      await Writer.WriteAsync(commandBytes, cancellationToken);
    }
    catch (Exception ex)
    {
      throw new InvalidOperationException("Failed to send command to scanner.", ex);
    }
  }

  private async Task ReadLoopAsync(CancellationToken ct)
  {
    try
    {
      while (!ct.IsCancellationRequested)
      {
        var result = await Reader.ReadAsync(ct);
        var buffer = result.Buffer;

        SequencePosition? position = buffer.PositionOf((byte)'\n');
        while (position != null)
        {
          var line = buffer.Slice(0, position.Value);
          ProcessLine(line); // interpret and handle

          // Move buffer past the delimiter so we can look for more
          buffer = buffer.Slice(buffer.GetPosition(1, position.Value));
          position = buffer.PositionOf((byte)'\n');
        }

        // Tell PipeReader how much have been consumed/examined
        Reader.AdvanceTo(buffer.Start, buffer.End);

        if (result.IsCompleted)
        {
          break;
        }
      }
    }
    catch (Exception ex)
    {
      _logger.LogError(ex, "Error in ReadLoopAsync");
    }
  }

  private void ProcessLine(ReadOnlySequence<byte> lineBytes)
  {
    var text = Encoding.ASCII.GetString(lineBytes.ToArray()).Trim();

    // Example: Different actions depending on mode
    if (_currentMode == EScannerMode.Trigger)
    {
      _scanTcs?.TrySetResult(text);
      _scanTcs = null;
    }
    else if (_currentMode == EScannerMode.Continuous)
    {
      OnGoodRead?.Invoke(this, text);
    }
    // You can add more protocol-specific parsing here: ACK/NAK, error codes, etc.
  }

  public async ValueTask DisposeAsync()
  {
    _listenerCts!.Cancel();

    try
    {
      await _listenerTask!;
    }
    catch (OperationCanceledException)
    {

    }
    finally
    {

      await Writer.CompleteAsync();
      await Reader.CompleteAsync();

      _listenerCts.Dispose();
    }
  }
}
