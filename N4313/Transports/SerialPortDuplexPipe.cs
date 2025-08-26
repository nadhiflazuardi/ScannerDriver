using System.IO.Pipelines;
using System.IO.Ports;
using System.Buffers;

namespace N4313.Transports;

public class SerialPortDuplexPipe : IDuplexPipe, IAsyncDisposable
{
    private readonly SerialPort _serialPort;
    private readonly Pipe _incoming;
    private readonly Pipe _outgoing;
    private readonly CancellationTokenSource _cts = new();

    public PipeReader Input => _incoming.Reader;
    public PipeWriter Output => _outgoing.Writer;
    public SerialPortDuplexPipe(string portName, int baudRate)
    {
        _serialPort = new SerialPort(portName, baudRate)
        {
            ReadTimeout = -1, // Let the Pipe handle timeouts/cancellation
            WriteTimeout = -1,
            DataBits = 8,
            Parity = Parity.None,
            StopBits = StopBits.One,
            Handshake = Handshake.None
        };

        _serialPort.Open();

        _incoming = new Pipe();
        _outgoing = new Pipe();

        _ = Task.Run(() => FillIncomingAsync(_cts.Token));
        _ = Task.Run(() => DrainOutgoingAsync(_cts.Token));
    }

    private async Task FillIncomingAsync(CancellationToken ct)
    {
        var stream = _serialPort.BaseStream;
        try
        {
            while (!ct.IsCancellationRequested)
            {
                Memory<byte> memory = _incoming.Writer.GetMemory(256);
                int bytesRead = await stream.ReadAsync(memory, ct).ConfigureAwait(false);
                if (bytesRead == 0) break; // disconnected
                _incoming.Writer.Advance(bytesRead);

                var result = await _incoming.Writer.FlushAsync(ct).ConfigureAwait(false);
                if (result.IsCompleted) break;
            }
        }
        catch (OperationCanceledException) { }
        finally
        {
            await _incoming.Writer.CompleteAsync();
        }
    }

    private async Task DrainOutgoingAsync(CancellationToken ct)
    {
        var stream = _serialPort.BaseStream;
        try
        {
            while (!ct.IsCancellationRequested)
            {
                ReadResult result = await _outgoing.Reader.ReadAsync(ct).ConfigureAwait(false);
                ReadOnlySequence<byte> buffer = result.Buffer;

                foreach (var segment in buffer)
                {
                    await stream.WriteAsync(segment, ct).ConfigureAwait(false);
                }

                _outgoing.Reader.AdvanceTo(buffer.End);

                if (result.IsCompleted) break;
            }
        }
        catch (OperationCanceledException) { }
        finally
        {
            await _outgoing.Reader.CompleteAsync();
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _incoming.Reader.CompleteAsync();
        await _outgoing.Writer.CompleteAsync();
        _serialPort.Close();
        _serialPort.Dispose();
    }
}
