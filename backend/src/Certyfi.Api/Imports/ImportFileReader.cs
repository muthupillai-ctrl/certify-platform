using System.Globalization;
using System.Text;
using ExcelDataReader;

namespace Certyfi.Api.Imports;

// Reads an uploaded spreadsheet of paper certificates (CSV or .xlsx) into
// plain text cells: the first sheet, the first non-empty row as headers.
// Excel dates become yyyy-MM-dd; whole numbers lose their ".0". Everything
// is then checked by ImportRowValidator, never trusted as-is.
public static class ImportFileReader
{
    public const int MaxRows = 10_000;
    public const long MaxBytes = 5 * 1024 * 1024;

    // ExcelDataReader needs the legacy code pages (e.g. Windows-1252 for
    // old CSV exports), which .NET Core only provides once registered.
    static ImportFileReader() => Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

    public record Sheet(IReadOnlyList<string> Headers, IReadOnlyList<IReadOnlyList<string>> Rows, bool Truncated);

    public static Sheet Read(Stream stream, string fileName)
    {
        var extension = Path.GetExtension(fileName).ToLowerInvariant();
        using var reader = extension switch
        {
            ".xlsx" => ExcelReaderFactory.CreateOpenXmlReader(stream),
            ".csv" or ".txt" => ExcelReaderFactory.CreateCsvReader(stream, new ExcelReaderConfiguration
            {
                FallbackEncoding = Encoding.UTF8,
                AutodetectSeparators = [',', ';', '\t'],
            }),
            _ => throw new InvalidDataException("Upload a .csv or .xlsx file."),
        };

        List<string>? headers = null;
        var rows = new List<IReadOnlyList<string>>();
        var truncated = false;
        while (reader.Read())
        {
            var cells = new List<string>(reader.FieldCount);
            for (var i = 0; i < reader.FieldCount; i++) cells.Add(Cell(reader.GetValue(i)));
            if (cells.All(string.IsNullOrWhiteSpace)) continue;

            if (headers is null)
            {
                headers = cells;
                continue;
            }
            if (rows.Count == MaxRows)
            {
                truncated = true;
                break;
            }
            rows.Add(cells);
        }
        if (headers is null) throw new InvalidDataException("The file is empty.");

        // Trim trailing empty header columns (common in Excel exports).
        var width = headers.FindLastIndex(h => !string.IsNullOrWhiteSpace(h)) + 1;
        return new Sheet(
            headers.Take(width).Select(h => h.Trim()).ToList(),
            rows.Select(r => (IReadOnlyList<string>)Enumerable.Range(0, width).Select(i => i < r.Count ? r[i] : "").ToList()).ToList(),
            truncated);
    }

    private static string Cell(object? value) => value switch
    {
        null => "",
        DateTime d => d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        double n when n == Math.Floor(n) && Math.Abs(n) < 1e15 => ((long)n).ToString(CultureInfo.InvariantCulture),
        double n => n.ToString(CultureInfo.InvariantCulture),
        bool b => b ? "TRUE" : "FALSE",
        _ => Convert.ToString(value, CultureInfo.InvariantCulture)?.Trim() ?? "",
    };
}
