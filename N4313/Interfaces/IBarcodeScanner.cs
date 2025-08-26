using N4313.Enums;

namespace N4313.Interfaces
{
    public interface IBarcodeScanner
    {
        Task<string> Scan(CancellationToken cancellationToken);
        Task SetMode(EScannerMode scannerMode, CancellationToken cancellationToken);
        event EventHandler<string> OnGoodRead;
    }
}