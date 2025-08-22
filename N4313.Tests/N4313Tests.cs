using System.IO.Ports;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Console;
using N4313.Enums;

namespace N4313.Tests;

public class N4313Tests
{
    private N4313 _sut;
    private const char ACK = '\x06';
    private const char SYN = '\x16';
    private const string ActivationSequence = $"\x16T\r";
    private const string ExpectedScanResult = "123456789";
    private const string FirstPortName = "/dev/ttyV0";
    private const string SecondPortName = "/dev/ttyV1";
    private const string ContinuousTestTrigger = "Continuous_mode_test";
    private const int BaudRate = 9600;
    private SerialPort _serialPort1;
    private SerialPort _serialPort2;
    private ILogger<N4313> _logger;

    private readonly Dictionary<string, string> _deviceResponses = new()
    {
        { "REVINF", "REVINFProduct Name: Laser Engine-N4300\r\n" +
            "Boot Revision: CA000064BCC\r\n" +
            "Software Part Number: CA000064BCC\r\n" +
            "Software Revision: 15448|/tags/CA000064BCC\r\n" +
            "Serial Number: 20067B450A\r\n" +
            "Supported IF: Standard\r\n" +
            $"PCB Assembly ID: 0{ACK}." },
        { "Test", "Test\r\n" },
        { "PAPPM3!", $"PAPPM3{ACK}!" },
        { "DEFALT!", $"DEFALT{ACK}!" },
        { ActivationSequence, ExpectedScanResult + "\r" },
        { ContinuousTestTrigger, ExpectedScanResult + "\r"}
    };

    [OneTimeSetUp]
    public void OneTimeSetup()
    {
        _serialPort1 = new SerialPort(FirstPortName, BaudRate)
        {
            Parity = Parity.None,
            DataBits = 8,
            StopBits = StopBits.One,
            Handshake = Handshake.None,
        };
        _serialPort2 = new SerialPort(SecondPortName, BaudRate)
        {
            Parity = Parity.None,
            DataBits = 8,
            StopBits = StopBits.One,
            Handshake = Handshake.None,
        };

        using var loggerFactory = LoggerFactory.Create(builder =>
        {
            builder
                .AddSimpleConsole(options =>
                {
                    options.SingleLine = true;
                    options.TimestampFormat = "hh:mm:ss ";
                })
                .SetMinimumLevel(LogLevel.Debug);
        });
        _logger = loggerFactory.CreateLogger<N4313>();
    }

    [SetUp]
    public void Setup()
    {
        _serialPort1!.Open();
        _serialPort2!.Open();

        _serialPort1.DiscardInBuffer();
        _serialPort2.DiscardOutBuffer();
        _serialPort2.WriteLine("Test");

        Thread.Sleep(300);
        string testString = _serialPort1.ReadLine();

        Assume.That(testString, Is.EqualTo("Test").Or.EqualTo("Test\r"), "Loopback check on port pair failed");

        _sut = new N4313(_logger, _serialPort1);
        _sut.Connect();
    }

    [TearDown]
    public void TearDown()
    {
        _sut?.Disconnect();

        if (_serialPort2 != null)
        {
            if (_serialPort2.IsOpen)
            {
                _serialPort2.Close();
            }
        }
    }

    [OneTimeTearDown]
    public void OneTimeTearDown()
    {
        // Dispose of the serial ports if they were opened
        _serialPort1?.Dispose();
        _serialPort2?.Dispose();
    }

    [Test]
    public async Task Scan_WhenInTriggerMode_ShouldReturnScanResult()
    {
        // Arrange
        var cts = new CancellationTokenSource();

        // Act
        var scanTask = _sut.Scan(cts.Token);

        cts.CancelAfter(10000);

        await Task.Delay(2000);
        _serialPort2.Write(ExpectedScanResult + "\r\n");

        var result = await scanTask;
        
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
        await _sut.SetMode(EScannerMode.Continuous);

        // Act & Assert
        Assert.ThrowsAsync<InvalidOperationException>(() => _sut.Scan(CancellationToken.None));
    }

    [Test]
    public void Scan_WhenNotConnectedToPort_ShouldThrow()
    {
        // Arrange
        _sut.Disconnect();

        // Act & Assert
        Assert.ThrowsAsync<InvalidOperationException>(() => _sut.Scan(CancellationToken.None));
    }

    [Test]
    public async Task SetMode_ShouldChangeCurrentMode_ToContinuous()
    {
        // Act
        await _sut.SetMode(EScannerMode.Continuous);

        // Assert
        Assert.That(_sut.CurrentMode, Is.EqualTo(EScannerMode.Continuous));
    }

    [Test]
    public async Task SetMode_ShouldChangeCurrentMode_ToTrigger()
    {
        // Act
        await _sut.SetMode(EScannerMode.Trigger);

        // Assert
        Assert.That(_sut.CurrentMode, Is.EqualTo(EScannerMode.Trigger));
    }

    [Test]
    public async Task OnGoodRead_InContinuousMode_ShouldBeInvoked()
    {
        await _sut.SetMode(EScannerMode.Continuous);

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
            _serialPort2.Write(ExpectedScanResult + "\r\n");

            var completed = await Task.WhenAny(tcs.Task, Task.Delay(5000));

            if (completed != tcs.Task)
            {
                Assert.Fail("OnGoodRead was not invoked in time");
            }

            string result = await tcs.Task;

            //Assert
            Assert.That(result, Is.EqualTo(ExpectedScanResult));
        }
        finally
        {
            _sut.OnGoodRead -= OnGoodReadTestHandler;
        }
    }

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
            _serialPort2.Write(ExpectedScanResult + "\r\n");

            var completed = await Task.WhenAny(tcs.Task, Task.Delay(500));

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
