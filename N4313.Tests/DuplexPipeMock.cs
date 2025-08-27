using System.IO.Pipelines;
using System.Buffers;
using System.Text;

namespace N4313.Tests
{
    public class DuplexPipeMock: IDuplexPipe, IAsyncDisposable
    {
        private readonly Pipe _inputPipe = new Pipe();
        private readonly Pipe _outputPipe = new Pipe();
        private readonly CancellationTokenSource _cts = new CancellationTokenSource();
        private readonly Task _simulationTask;

        public PipeReader Input => _inputPipe.Reader;
        public PipeWriter Output => _outputPipe.Writer;

        private PipeReader DeviceReader => _outputPipe.Reader;
        private PipeWriter DeviceWriter => _inputPipe.Writer;

        public int SimulatedDelayMs { get; set; } = 0;

        public DuplexPipeMock(IReadOnlyDictionary<string, string> commandResponses)
        {
            _simulationTask = Task.Run(() => SimulateDeviceAsync(commandResponses, _cts.Token));
        }

        private async Task SimulateDeviceAsync(IReadOnlyDictionary<string, string> commandResponses, CancellationToken ct)
        {
            try
            {
                while (!ct.IsCancellationRequested)
                {
                    ReadResult result = await DeviceReader.ReadAsync(ct);
                    ReadOnlySequence<byte> buffer = result.Buffer;

                    if (buffer.IsEmpty && result.IsCompleted)
                    {
                        break;
                    }

                    string command = Encoding.ASCII.GetString(buffer.ToArray());

                    if (command.Contains(N4313Commands.ActivationSequence))
                    {
                        Thread.Sleep(SimulatedDelayMs);
                    }

                    foreach (var pair in commandResponses)
                    {
                        if (command.Contains(pair.Key))
                        {
                            var responseBytes = Encoding.ASCII.GetBytes(pair.Value);
                            await DeviceWriter.WriteAsync(responseBytes, ct);
                            break;
                        }
                    }

                    DeviceReader.AdvanceTo(buffer.End);

                    if (result.IsCompleted)
                    {
                        break;
                    }
                }
            }
            catch (OperationCanceledException)
            {
            }
            finally
            {
                await DeviceWriter.CompleteAsync();
                await DeviceReader.CompleteAsync();
            }
        }

        public async ValueTask DisposeAsync()
        {
            _cts.Cancel();
            await _simulationTask;
            _cts.Dispose();
        }
    }
}