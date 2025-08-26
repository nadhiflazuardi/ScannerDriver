using System.IO.Pipelines;
using System.IO.Ports;

namespace N4313.Transports;

public class SerialPortDuplexPipe : IDuplexPipe, IAsyncDisposable
{
    private readonly SerialPort _serialPort;

    private readonly PipeReader _pipeReader;
    private readonly PipeWriter _pipeWriter;

    public PipeReader Input => _pipeReader;
    public PipeWriter Output => _pipeWriter;
    public SerialPortDuplexPipe(string portName, int baudRate)
    {
        _serialPort = new SerialPort(portName, baudRate)
        {
            ReadTimeout = -1, // Let the Pipe handle timeouts/cancellation
            WriteTimeout = 500,
            DataBits = 8,
            Parity = Parity.None,
            StopBits = StopBits.One,
            Handshake = Handshake.None
        };

        _serialPort.Open();

        // Wrap the serial port's stream with a PipeReader and PipeWriter
        _pipeReader = PipeReader.Create(_serialPort.BaseStream);
        _pipeWriter = PipeWriter.Create(_serialPort.BaseStream);
    }

    public async ValueTask DisposeAsync()
    {
        await _pipeReader.CompleteAsync();
        await _pipeWriter.CompleteAsync();
        _serialPort.Close();
        _serialPort.Dispose();
    }
}
