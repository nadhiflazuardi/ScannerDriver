using System.IO.Pipelines;
using System.IO.Ports;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Console;
using N4313.Enums;
using N4313.Transports;
using Moq;
using System.Text;

namespace N4313.Tests;

public class N4313Tests
{
    private IReadOnlyDictionary<string, string> _deviceResponses = N4313Commands.DeviceResponses;
    private DuplexPipeMock _duplexPipeMock;
    private N4313 _sut;
    private const string ExpectedScanResult = "123456789";
    private ILogger<N4313> _logger;

    [SetUp]
    public void Setup()
    {
        _logger = Mock.Of<ILogger<N4313>>();
        _duplexPipeMock = new DuplexPipeMock(_deviceResponses);
        _sut = new N4313(_logger, _duplexPipeMock);
    }

    [TearDown]
    public async Task Teardown()
    {
        await _duplexPipeMock.DisposeAsync();
        await _sut.DisposeAsync();
    }

    [Test]
    public async Task Scan_WhenInTriggerMode_ShouldReturnScanResult()
    {
        // Arrange
        var cts = new CancellationTokenSource();

        // Act
        string result = await _sut.Scan(cts.Token);

        cts.CancelAfter(TimeSpan.FromSeconds(5));

        // Assert
        Assert.Multiple(() =>
        {
            Assert.That(result, Is.Not.Null.And.Not.Empty);
            Assert.That(result, Is.EqualTo(ExpectedScanResult));
        });
    }

    [Test]
    public async Task Scan_WhenInContinuousMode_ShouldThrow()
    {
        // Arrange
        var cts = new CancellationTokenSource();

        await _sut.SetMode(EScannerMode.Continuous, cts.Token);

        cts.CancelAfter(TimeSpan.FromSeconds(5));

        // Act & Assert
        Assert.ThrowsAsync<InvalidOperationException>(() => _sut.Scan(CancellationToken.None));
    }

    [Test]
    public async Task SetMode_ShouldChangeCurrentMode_ToContinuous()
    {
        // Act
        await _sut.SetMode(EScannerMode.Continuous, CancellationToken.None);

        // Assert
        Assert.That(_sut.CurrentMode, Is.EqualTo(EScannerMode.Continuous));
    }

    [Test]
    public async Task SetMode_ShouldChangeCurrentMode_ToTrigger()
    {
        // Act
        await _sut.SetMode(EScannerMode.Trigger, CancellationToken.None);

        // Assert
        Assert.That(_sut.CurrentMode, Is.EqualTo(EScannerMode.Trigger));
    }

    // [Test]
    // public async Task OnGoodRead_InContinuousMode_ShouldBeInvoked()
    // {
    //     await _sut.SetMode(EScannerMode.Continuous, CancellationToken.None);

    //     var tcs = new TaskCompletionSource<string>();

    //     void OnGoodReadTestHandler(object? sender, string data)
    //     {
    //         if (!tcs.Task.IsCompleted)
    //         {
    //             tcs.SetResult(data);
    //         }
    //     }

    //     _sut.OnGoodRead += OnGoodReadTestHandler;

    //     try
    //     {
    //         await _duplexPipeMock.Output.WriteAsync(Encoding.ASCII.GetBytes(N4313Commands.ContinuousTestTrigger));

    //         var completed = await Task.WhenAny(tcs.Task, Task.Delay(5000));

    //         if (completed != tcs.Task)
    //         {
    //             Assert.Fail("OnGoodRead was not invoked in time");
    //         }

    //         string result = await tcs.Task;

    //         //Assert
    //         Assert.That(result, Is.EqualTo(ExpectedScanResult));
    //     }
    //     finally
    //     {
    //         _sut.OnGoodRead -= OnGoodReadTestHandler;
    //     }
    // }

    [Test]
    public async Task OnGoodRead_InTriggerMode_ShouldNotBeInvoked()
    {
        var tcs = new TaskCompletionSource<string>();

        void OnGoodReadTestHandler(object? sender, string data)
        {
            if (!tcs.Task.IsCompleted)
            {
                tcs.SetResult(data);
            }
        }

        _sut.OnGoodRead += OnGoodReadTestHandler;

        try
        {
            await _duplexPipeMock.Output.WriteAsync(Encoding.ASCII.GetBytes(N4313Commands.ContinuousTestTrigger));

            var completed = await Task.WhenAny(tcs.Task, Task.Delay(5000));

            if (completed == tcs.Task)
            {
                Assert.Fail("OnGoodRead was invoked unexpectedly in Manual mode.");
            }
            else
            {
                Assert.Pass("OnGoodRead was not invoked, as expected.");
            }
        }
        finally
        {
            _sut.OnGoodRead -= OnGoodReadTestHandler;
        }
    }
}
