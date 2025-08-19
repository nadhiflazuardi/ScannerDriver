using Microsoft.Extensions.Logging;
using N4313;
using N4313.Enums;

namespace ScannerClient;

class Program
{
  private static N4313.N4313? _scanner;
  private static bool _isRunning = true;

  static async Task Main(string[] args)
  {
    Console.WriteLine("=== N4313 Barcode Scanner Client ===");
    Console.WriteLine("Press Ctrl+C to exit at any time");

    using var loggerFactory = LoggerFactory.Create(builder =>
        {
          builder.AddConsole();
          builder.SetMinimumLevel(LogLevel.Debug);
        });

    var logger = loggerFactory.CreateLogger<N4313.N4313>();
    _scanner = new N4313.N4313(logger);

    // Subscribe to continuous mode events
    _scanner.OnGoodRead += (sender, barcode) =>
    {
      Console.WriteLine($"[CONTINUOUS] Scanned: {barcode}");
    };

    try
    {
      // Connect to scanner
      Console.WriteLine("Connecting to scanner...");
      _scanner.Connect();
      Console.WriteLine("✓ Connected successfully!");

      await ShowMainMenu();
    }
    catch (Exception ex)
    {
      Console.WriteLine($"❌ Error: {ex.Message}");
    }
    finally
    {
      _scanner?.Disconnect();
      Console.WriteLine("Scanner disconnected. Press any key to exit.");
      Console.ReadKey();
    }
  }

  static async Task ShowMainMenu()
  {
    while (_isRunning)
    {
      Console.WriteLine("\n--- Scanner Menu ---");
      Console.WriteLine("1. Single Scan (Trigger Mode)");
      Console.WriteLine("2. Enable Continuous Scanning");
      Console.WriteLine("3. Switch to Trigger Mode");
      Console.WriteLine("4. Exit");
      Console.Write("Select option (1-4): ");

      var key = Console.ReadKey();
      Console.WriteLine();

      try
      {
        switch (key.KeyChar)
        {
          case '1':
            await PerformSingleScan();
            break;
          case '2':
            await EnableContinuousMode();
            break;
          case '3':
            await EnableTriggerMode();
            break;
          case '4':
            _isRunning = false;
            break;
          default:
            Console.WriteLine("Invalid option. Please select 1-4.");
            break;
        }
      }
      catch (Exception ex)
      {
        Console.WriteLine($"❌ Operation failed: {ex.Message}");
      }
    }
  }

  static async Task PerformSingleScan()
  {
    Console.WriteLine("\n🔍 Trigger scan mode - Present barcode to scanner...");
    Console.WriteLine("Press ESC to cancel scan");

    using var cts = new CancellationTokenSource();

    // Listen for ESC key to cancel
    var keyTask = Task.Run(() =>
    {
      while (!cts.Token.IsCancellationRequested)
      {
        if (Console.KeyAvailable)
        {
          var keyInfo = Console.ReadKey(true);
          if (keyInfo.Key == ConsoleKey.Escape)
          {
            cts.Cancel();
            break;
          }
        }
        Thread.Sleep(50);
      }
    });

    try
    {
      // Set timeout for scan operation
      cts.CancelAfter(TimeSpan.FromSeconds(10));

      string barcode = await _scanner!.Scan(cts.Token);
      Console.WriteLine($"✓ Scanned: {barcode}");
    }
    catch (OperationCanceledException)
    {
      Console.WriteLine("⚠️ Scan cancelled or timed out");
    }
    catch (Exception ex)
    {
      Console.WriteLine($"❌ Scan failed: {ex.Message}");
    }
  }

  static async Task EnableContinuousMode()
  {
    Console.WriteLine("\n🔄 Switching to Continuous Mode...");

    try
    {
      await _scanner!.SetMode(EScannerMode.Continuous);
      Console.WriteLine("✓ Continuous mode enabled!");
      Console.WriteLine("Scanner will now read barcodes automatically.");
      Console.WriteLine("Scanned barcodes will appear above.");
      Console.WriteLine("Press any key to return to menu...");

      // Wait for user input while continuous scanning happens
      await Task.Run(() => Console.ReadKey(true));
    }
    catch (Exception ex)
    {
      Console.WriteLine($"❌ Failed to enable continuous mode: {ex.Message}");
    }
  }

  static async Task EnableTriggerMode()
  {
    Console.WriteLine("\n🎯 Switching to Trigger Mode...");

    try
    {
      await _scanner!.SetMode(EScannerMode.Trigger);
      Console.WriteLine("✓ Trigger mode enabled!");
      Console.WriteLine("Use option 1 to perform single scans.");
    }
    catch (Exception ex)
    {
      Console.WriteLine($"❌ Failed to enable trigger mode: {ex.Message}");
    }
  }
}