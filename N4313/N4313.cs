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
  private TaskCompletionSource<bool>? _commandTcs;
  private TaskCompletionSource<string>? _scanTcs;
  private readonly ILogger<N4313> _logger;
  private readonly SemaphoreSlim _semaphore = new(1, 1);
  protected PipeWriter _writer;
  protected PipeReader _reader;

  public N4313(ILogger<N4313> logger, IDuplexPipe duplexPipe)
  {
    _logger = logger;
    _reader = duplexPipe.Input;
    _writer = duplexPipe.Output;

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
      _scanTcs = null;
      _semaphore.Release();
    }
  }

  public async Task SetMode(EScannerMode mode, CancellationToken cancellationToken)
  {
    if (_currentMode == mode)
    {
      _logger.LogInformation("Scanner already in {Mode} mode, no action taken.", mode);
      return;
    }

    await _semaphore.WaitAsync();

    cancellationToken.ThrowIfCancellationRequested();

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

      _logger.LogDebug("Sending command '{Command}' for mode {Mode}", message, mode);

      _commandTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

      await SendCommandAsync(message, cancellationToken);

      await _commandTcs.Task.WaitAsync(cancellationToken);

      _currentMode = mode;

      _logger.LogInformation("Scanner mode successfully changed to {Mode}", mode);
    }
    catch
    {
      _commandTcs = null;
      throw;
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
      await _writer.WriteAsync(commandBytes, cancellationToken);
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
        var result = await _reader.ReadAsync(ct);
        var buffer = result.Buffer;
        var consumed = buffer.Start;
        var examined = buffer.End;

        // Look for line endings (\r, \n, or \r\n)
        while (TryReadLine(buffer, _commandTcs != null, out ReadOnlySequence<byte> line, out SequencePosition lineEnd))
        {
          // Update consumed to the position after the line ending
          consumed = lineEnd;

          // Update buffer to start from the consumed position
          buffer = buffer.Slice(consumed);

          if (_commandTcs != null)
          {
            _commandTcs.TrySetResult(true);
            _commandTcs = null;
            continue;
          }

          ProcessLine(line);
        }

        // Update the consumed position
        _reader.AdvanceTo(consumed, examined);

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

    // Skip empty lines
    if (string.IsNullOrEmpty(text))
    {
      return;
    }

    _logger.LogDebug("Received line: '{Text}' in mode: {Mode}", text, _currentMode);

    if (_currentMode == EScannerMode.Trigger && _scanTcs != null)
    {
      _logger.LogDebug("Setting scan result: '{Text}'", text);
      if (_scanTcs.TrySetResult(text))
      {
        _logger.LogDebug("Scan result successfully set");
      }
    }
    else if (_currentMode == EScannerMode.Continuous)
    {
      OnGoodRead?.Invoke(this, text);
    }
    else
    {
      _logger.LogWarning("Received data '{Text}' but no active scan or wrong mode", text);
    }
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

      await _writer.CompleteAsync();
      await _reader.CompleteAsync();

      _listenerCts.Dispose();
    }
  }

  private static bool TryReadLine(ReadOnlySequence<byte> buffer, bool isCommandMode, out ReadOnlySequence<byte> line, out SequencePosition end)
  {
    var reader = new SequenceReader<byte>(buffer);

    if (isCommandMode)
    {
      // Parse up to ! or .
      if (reader.TryReadToAny(out ReadOnlySequence<byte> lineData, new byte[] { (byte)'!', (byte)'.' }, advancePastDelimiter: true))
      {
        line = lineData;
        end = reader.Position;
        return true;
      }
    }
    else
    {
      // Look for \r or \n
      if (reader.TryReadTo(out ReadOnlySequence<byte> lineData, (byte)'\r', (byte)'\n', advancePastDelimiter: true))
      {
        line = lineData;
        end = reader.Position;
        return true;
      }
    }

    line = default;
    end = default;
    return false;
  }
}
