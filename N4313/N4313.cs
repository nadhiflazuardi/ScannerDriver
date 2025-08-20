using Microsoft.Extensions.Logging;
using N4313.Enums;
using N4313.Interfaces;
using System.IO.Ports;
using System.Text;

namespace N4313;

public class N4313: IBarcodeScanner
{
  public event EventHandler<string>? OnGoodRead;
  private const string COMMAND_PREFIX = "\x16" + "M" + "\x0D";
  private const string ACTIVATE_ENGINE_COMMAND = "\x16" + "T" + "\x0D";
  private const string DEACTIVATE_ENGINE_COMMAND = "\x16" + "U" + "\x0D";
  private readonly StringBuilder _barcodeBuffer = new();
  private EScannerMode _currentMode;
  private TaskCompletionSource<string>? _tcs;
  private readonly ILogger<N4313> _logger;
  private readonly SemaphoreSlim _semaphore = new(1, 1);

  private SerialPort _serialPort;

  public N4313(ILogger<N4313> logger, SerialPort serialPort)
  {
    _logger = logger;
    _serialPort = serialPort;
    _serialPort.DataReceived += OnDataReceived;
  }

  public void Connect()
  {
    if (!_serialPort.IsOpen)
    {
      _serialPort.Open();
    }
  }

  public void Disconnect()
  {
    if (_serialPort.IsOpen)
    {
      _serialPort.Close();
    }
  }

  public async Task<string> Scan(CancellationToken cancellationToken)
  {
    await _semaphore.WaitAsync(cancellationToken);

    if (_currentMode == EScannerMode.Continuous)
    {
      _logger.LogWarning("Scan attempted in Continuous mode. Operation not allowed.");
      throw new InvalidOperationException("Can't trigger scan in continous mode!");
    }

    cancellationToken.ThrowIfCancellationRequested();

    try
    {
      _logger.LogInformation("Starting scan...");

      EnsureConnected();
      _logger.LogDebug("Connection ensured, sending scan command.");

      var result = await SendCommandAndListenAsync(ACTIVATE_ENGINE_COMMAND, cancellationToken);

      _logger.LogInformation("Scan completed successfully. Result: {Result}", result);
      return result;
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
    await _semaphore.WaitAsync();

    if (_currentMode == mode)
    {
      _logger.LogInformation("Scanner already in {Mode} mode, no action taken.", mode);
      return;
    }

    try
    {
      _logger.LogInformation("Attempting to set scanner mode to {Mode}", mode);

      EnsureConnected();
      _logger.LogDebug("Connection ensured, preparing mode change command.");

      string message = mode switch
      {
        EScannerMode.Continuous => COMMAND_PREFIX + "pappm3!",
        EScannerMode.Trigger => COMMAND_PREFIX + "aosdft!",
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

  private void EnsureConnected()
  {
    if (!_serialPort.IsOpen)
    {
      throw new InvalidOperationException("ScannerDriver is not connected. Call Connect() before using this method.");
    }
  }

  private void OnDataReceived(object? sender, SerialDataReceivedEventArgs e)
  {
    try
    {
      while (_serialPort.BytesToRead > 0)
      {
        char ch = (char)_serialPort.ReadChar();
        _logger.LogTrace("Read char: {Char} (0x{Hex})", ch, ((int)ch).ToString("X2"));

        if (ch == '\x06')
        {
          OnResponseReceived(ECommandResponse.ACK);
        }
        else if (ch == '\x15')
        {
          OnResponseReceived(ECommandResponse.NAK);
        }
        else if (ch == '\x05')
        {
          OnResponseReceived(ECommandResponse.ENQ);
        }

        if (ch == '\r')
        {
          string line = _barcodeBuffer.ToString();
          _barcodeBuffer.Clear();

          _logger.LogInformation("Complete barcode received: {Barcode}", line);

          if (_currentMode == EScannerMode.Trigger)
          {
            _tcs?.TrySetResult(line);
          }
          else if (_currentMode == EScannerMode.Continuous)
          {
            OnGoodRead?.Invoke(this, line);
          }
        }
        else
        {
          _logger.LogTrace("Appending char '{Char}' to buffer.", ch);
          _barcodeBuffer.Append(ch);
        }

        if (ch == '!' || ch == '.' || ch == '\n')
        {
          _logger.LogTrace("Punctuation detected. Clearing buffer.");
          _barcodeBuffer.Clear();
        }
      }
    }
    catch (Exception ex)
    {
      _logger.LogError(ex, "Error while processing data from serial port.");
      if (_currentMode == EScannerMode.Trigger)
      {
        _tcs?.TrySetException(ex);
      }
    }
  }

  private void OnResponseReceived(ECommandResponse commandResponse)
  {
    string response = _barcodeBuffer.ToString();

    _logger.LogDebug("Response of type {type} received: {response}", commandResponse, response);
  }

  private async Task<string> SendCommandAndListenAsync(string command, CancellationToken cancellationToken)
  {
    try
    {
      _logger.LogInformation("Sending command: {Command}", command);

      _tcs = new TaskCompletionSource<string>();

      using var registration = cancellationToken.Register(() =>
      {
        _logger.LogWarning("Command '{Command}' was cancelled.", command);
        _tcs?.TrySetCanceled(cancellationToken);
      });

      string fullCommand = command;
      byte[] commandBytes = Encoding.ASCII.GetBytes(fullCommand);

      _logger.LogDebug("Writing {Length} bytes to serial port: {Bytes}", commandBytes.Length, BitConverter.ToString(commandBytes));
      await _serialPort.BaseStream.WriteAsync(commandBytes, 0, commandBytes.Length, cancellationToken);

      string response = await _tcs.Task;

      _logger.LogInformation("Received response for command '{Command}': {Response}", command, response);
      return response;
    }
    catch (OperationCanceledException)
    {
      _logger.LogWarning("Command '{Command}' was cancelled by caller.", command);
      throw;
    }
    catch (Exception ex)
    {
      _logger.LogError(ex, "Error while sending command '{Command}'", command);
      _tcs = null;
      throw new Exception("An error occurred while sending command.", ex);
    }
  }

  private async Task SendCommandAsync(string command, CancellationToken cancellationToken)
  {
    try
    {
      EnsureConnected();

      string fullCommand = command;
      byte[] commandBytes = Encoding.ASCII.GetBytes(fullCommand);
      await _serialPort.BaseStream.WriteAsync(commandBytes, 0, commandBytes.Length, cancellationToken);
    }
    catch (Exception ex)
    {
      throw new InvalidOperationException("Failed to send command to scanner.", ex);
    }
  }
}
