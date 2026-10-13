using System.Globalization;
using System.IO;
using ClosedXML.Excel;
using CsvHelper;
using CsvHelper.Configuration;
using Newtonsoft.Json;
using WPIntegrationTool.Models;

namespace WPIntegrationTool.Services
{
    /// <summary>
    /// Reads a CSV file, validates it against a parsed XML mapping config,
    /// maps columns using <see cref="ColumnMapper"/>, and builds the JSON payloads.
    /// </summary>
    public static class CsvToPayloadConverter
    {
        // ────────────────────────────────────────────────────────────────
        //  PUBLIC API
        // ────────────────────────────────────────────────────────────────

        /// <summary>
        /// Full conversion pipeline: validate → map → chunk → build payloads.
        /// Returns a <see cref="ConversionResult"/> with payloads and diagnostics.
        /// </summary>
        public static ConversionResult Convert(
            string csvFilePath,
            XmlMappingConfig xmlConfig,
            PublishType publishType,
            ConnectionSettings settings,
            Action<string>? log = null)
        {
            var result = new ConversionResult();

            // ── Step 1: Validate publish type vs XML ────────────────────
            log?.Invoke("Validating XML mapping against selected publish type…");

            var validationErrors = ValidateXmlVsPublishType(xmlConfig, publishType);
            if (validationErrors.Count > 0)
            {
                foreach (var err in validationErrors)
                    result.Errors.Add(err);
                return result;
            }
            log?.Invoke("  XML mapping is compatible with the selected publish type.");

            // ── Step 2: Read CSV headers ────────────────────────────────
            log?.Invoke($"Reading CSV file: {Path.GetFileName(csvFilePath)}");

            string[] csvHeaders;
            List<Dictionary<string, string>> csvRows;

            try
            {
                (csvHeaders, csvRows) = ReadDataFile(csvFilePath);
            }
            catch (Exception ex)
            {
                result.Errors.Add(
                    $"Failed to read data file '{Path.GetFileName(csvFilePath)}'.\n" +
                    $"  Reason: {ex.Message}\n" +
                    "  Check that the file is a valid UTF-8 CSV or Excel (.xlsx/.xls) with a header row.");
                return result;
            }

            if (csvRows.Count == 0)
            {
                result.Errors.Add(
                    $"CSV file '{Path.GetFileName(csvFilePath)}' contains a header row " +
                    "but no data rows. Nothing to publish.");
                return result;
            }

            log?.Invoke($"  Found {csvHeaders.Length} columns and {csvRows.Count} data rows.");
            log?.Invoke($"  CSV columns: {string.Join(", ", csvHeaders)}");

            // ── Step 3: Build column mapping ────────────────────────────
            log?.Invoke("Building column mapping (CSV headers → XML fromAttribute names)…");

            var mapping = ColumnMapper.BuildMapping(csvHeaders, xmlConfig, publishType);
            result.MappingResult = mapping;

            // Log all match details
            foreach (var detail in mapping.MatchDetails)
                log?.Invoke(detail);

            // Log warnings
            foreach (var warning in mapping.Warnings)
                log?.Invoke($"  WARNING: {warning}");

            if (!mapping.IsValid)
            {
                foreach (var err in mapping.Errors)
                    result.Errors.Add(err);
                return result;
            }

            log?.Invoke($"  Mapping complete: {mapping.MappedCount} columns mapped successfully.");

            // ── Step 4: Map rows using the column mapping ───────────────
            log?.Invoke("Mapping CSV rows to payload format…");

            var mappedRows = new List<Dictionary<string, string>>();
            int skippedDeleted = 0;
            int rowErrors = 0;

            for (int i = 0; i < csvRows.Count; i++)
            {
                var row = csvRows[i];

                // Skip deleted rows for MTO
                if (publishType == PublishType.ActualMTO)
                {
                    if (row.TryGetValue("is_deleted", out var isDeleted) &&
                        isDeleted.Equals("TRUE", StringComparison.OrdinalIgnoreCase))
                    {
                        skippedDeleted++;
                        continue;
                    }
                }

                try
                {
                    var mapped = MapRow(row, mapping.CsvToFromAttribute, publishType);
                    mappedRows.Add(mapped);
                }
                catch (Exception ex)
                {
                    rowErrors++;
                    result.Warnings.Add(
                        $"Row {i + 2} (line {i + 2}): Failed to map — {ex.Message}. Row skipped.");

                    if (rowErrors > 50)
                    {
                        result.Errors.Add(
                            $"Too many row-level errors ({rowErrors}). Aborting conversion. " +
                            "The CSV data may be malformed or the column mapping is incorrect.");
                        return result;
                    }
                }
            }

            if (skippedDeleted > 0)
                log?.Invoke($"  Skipped {skippedDeleted} rows where is_deleted=TRUE.");
            if (rowErrors > 0)
                log?.Invoke($"  WARNING: {rowErrors} rows had mapping errors and were skipped.");

            if (mappedRows.Count == 0)
            {
                result.Errors.Add(
                    "After filtering and mapping, there are no valid data rows to publish. " +
                    $"({skippedDeleted} deleted, {rowErrors} errors, {csvRows.Count} total)");
                return result;
            }

            log?.Invoke($"  {mappedRows.Count} rows mapped successfully.");

            // ── Step 5: Chunk and build payloads ────────────────────────
            // Matches objectName in known-good WORKPACKS reference payloads
            string tableName = publishType == PublishType.ActualMTO
                ? "ActualMTOPublish"
                : "CWPIWPPublish";

            string sentToApp = publishType == PublishType.ActualMTO
                ? "ERMRC"
                : "ERMRC2";

            int chunkSize = settings.ChunkSize > 0 ? settings.ChunkSize : mappedRows.Count;
            var chunks = Chunk(mappedRows, chunkSize);

            log?.Invoke($"Building payloads: {chunks.Count} chunk(s) of up to {chunkSize} rows each…");

            for (int i = 0; i < chunks.Count; i++)
            {
                var chunk = chunks[i];

                string contextJson = BuildContext(tableName, settings, chunk.Count, sentToApp);
                var metadata = new MetadataPayload
                {
                    IndexOfChunk = (i + 1).ToString(),
                    TotalNumberOfChunk = chunks.Count.ToString()
                };

                var payload = new PublishPayload
                {
                    ChunkId = (i + 1).ToString(),
                    Path = $"/tables('{tableName}')",
                    TableName = tableName,
                    Context = contextJson,
                    Metadata = JsonConvert.SerializeObject(metadata),
                    Data = JsonConvert.SerializeObject(chunk)
                };

                result.Payloads.Add(payload);
            }

            log?.Invoke($"Conversion complete: {result.Payloads.Count} payload(s) ready.");
            return result;
        }

        // ────────────────────────────────────────────────────────────────
        //  VALIDATION
        // ────────────────────────────────────────────────────────────────

        private static List<string> ValidateXmlVsPublishType(
            XmlMappingConfig xmlConfig, PublishType publishType)
        {
            var errors = new List<string>();
            string xmlFile = Path.GetFileName(xmlConfig.SourceFilePath);

            if (publishType == PublishType.ActualMTO)
            {
                if (xmlConfig.IsCwpIwp && !xmlConfig.IsMto)
                {
                    errors.Add(
                        $"MISMATCH: You selected 'Actual MTO Publish' but the XML mapping file " +
                        $"'{xmlFile}' is configured for CWP/IWP publishing.\n" +
                        $"  The XML contains integrationContext='{xmlConfig.PrimaryIntegrationContext}'.\n" +
                        $"  Expected integrationContext containing 'MTO' (e.g., 'ActualMTOPublish').\n" +
                        $"  → Please select the correct XML mapping file for MTO data, " +
                        $"typically named 'DataTableEiwmPipingMTOMap.xml' or similar.");
                }
            }
            else if (publishType == PublishType.CWPIWP)
            {
                if (xmlConfig.IsMto && !xmlConfig.IsCwpIwp)
                {
                    errors.Add(
                        $"MISMATCH: You selected 'CWP / IWP Publish' but the XML mapping file " +
                        $"'{xmlFile}' is configured for MTO publishing.\n" +
                        $"  The XML contains integrationContext='{xmlConfig.PrimaryIntegrationContext}'.\n" +
                        $"  Expected integrationContext containing 'CWP' or 'IWP' (e.g., 'CWPIWPPublish').\n" +
                        $"  → Please select the correct XML mapping file for CWP/IWP data, " +
                        $"typically named 'DataTableEiwmActivityMap.xml' or similar.");
                }
            }

            if (xmlConfig.IntegrationContexts.Count == 0)
            {
                string inferred = xmlConfig.PrimaryIntegrationContext;
                if (inferred == "Unknown")
                {
                    errors.Add(
                        $"Cannot determine the publish type from XML file '{xmlFile}'. " +
                        "No <class> element has an 'integrationContext' attribute, " +
                        "and the class IDs don't match any known pattern. " +
                        "Please verify this is a valid mapping configuration file.");
                }
            }

            return errors;
        }

        // ────────────────────────────────────────────────────────────────
        //  FILE READING DISPATCHER
        // ────────────────────────────────────────────────────────────────

        /// <summary>
        /// Dispatches to the correct reader based on file extension.
        /// Supports: .csv, .xlsx, .xls
        /// </summary>
        private static (string[] headers, List<Dictionary<string, string>> rows) ReadDataFile(string path)
        {
            string ext = Path.GetExtension(path).ToLowerInvariant();
            return ext switch
            {
                ".xlsx" or ".xls" => ReadExcel(path),
                ".csv" or ".txt" or "" => ReadCsv(path),
                _ => throw new InvalidOperationException(
                    $"Unsupported file extension '{ext}'. Supported: .csv, .xlsx, .xls")
            };
        }

        /// <summary>
        /// Reads the first worksheet of an Excel file. Assumes row 1 is headers.
        /// </summary>
        private static (string[] headers, List<Dictionary<string, string>> rows) ReadExcel(string path)
        {
            using var workbook = new XLWorkbook(path);
            var worksheet = workbook.Worksheets.FirstOrDefault()
                ?? throw new InvalidOperationException("The Excel file contains no worksheets.");

            var usedRange = worksheet.RangeUsed();
            if (usedRange == null)
                throw new InvalidOperationException("The first worksheet is empty.");

            int firstRow = usedRange.FirstRow().RowNumber();
            int lastRow = usedRange.LastRow().RowNumber();
            int firstCol = usedRange.FirstColumn().ColumnNumber();
            int lastCol = usedRange.LastColumn().ColumnNumber();

            // Read headers from first used row
            var headers = new List<string>();
            for (int c = firstCol; c <= lastCol; c++)
            {
                string header = worksheet.Cell(firstRow, c).GetString().Trim();
                headers.Add(header);
            }

            if (headers.Count == 0 || headers.All(string.IsNullOrWhiteSpace))
                throw new InvalidOperationException(
                    "The Excel file has no header row, or the header row is empty.");

            // Read data rows
            var rows = new List<Dictionary<string, string>>();
            for (int r = firstRow + 1; r <= lastRow; r++)
            {
                var row = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                bool hasAnyValue = false;

                for (int i = 0; i < headers.Count; i++)
                {
                    string header = headers[i];
                    if (string.IsNullOrWhiteSpace(header))
                        continue;

                    var cell = worksheet.Cell(r, firstCol + i);
                    string value = cell.IsEmpty() ? string.Empty : cell.GetString().Trim();
                    row[header] = value;

                    if (!string.IsNullOrWhiteSpace(value))
                        hasAnyValue = true;
                }

                // Skip entirely blank rows
                if (hasAnyValue)
                    rows.Add(row);
            }

            return (headers.ToArray(), rows);
        }

        // ────────────────────────────────────────────────────────────────
        //  CSV READING
        // ────────────────────────────────────────────────────────────────

        private static (string[] headers, List<Dictionary<string, string>> rows) ReadCsv(string path)
        {
            var config = new CsvConfiguration(CultureInfo.InvariantCulture)
            {
                HasHeaderRecord = true,
                MissingFieldFound = null,
                BadDataFound = null,
                TrimOptions = TrimOptions.Trim
            };

            using var reader = new StreamReader(path);
            using var csv = new CsvReader(reader, config);

            csv.Read();
            csv.ReadHeader();
            var headers = csv.HeaderRecord ?? Array.Empty<string>();

            if (headers.Length == 0)
                throw new InvalidOperationException(
                    "The CSV file has no header row, or the header row is empty.");

            var rows = new List<Dictionary<string, string>>();
            while (csv.Read())
            {
                var row = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (var h in headers)
                {
                    row[h] = csv.GetField(h) ?? string.Empty;
                }
                rows.Add(row);
            }

            return (headers, rows);
        }

        // ────────────────────────────────────────────────────────────────
        //  ROW MAPPING
        // ────────────────────────────────────────────────────────────────

        private static Dictionary<string, string> MapRow(
            Dictionary<string, string> csvRow,
            Dictionary<string, string> csvToFromAttribute,
            PublishType publishType)
        {
            var mapped = new Dictionary<string, string>();

            foreach (var kvp in csvToFromAttribute)
            {
                string csvColumn = kvp.Key;
                string fromAttribute = kvp.Value;

                string value = string.Empty;
                if (csvRow.TryGetValue(csvColumn, out var rawValue))
                    value = rawValue?.Trim() ?? string.Empty;

                // Normalise date fields for CWP/IWP
                if (publishType == PublishType.CWPIWP &&
                    fromAttribute.Contains("date", StringComparison.OrdinalIgnoreCase))
                {
                    value = NormaliseDateToIso(value);
                }

                mapped[fromAttribute] = value;
            }

            return mapped;
        }

        // ────────────────────────────────────────────────────────────────
        //  CONTEXT / MESSAGE BUILDING
        // ────────────────────────────────────────────────────────────────

        private static string BuildContext(
            string tableName,
            ConnectionSettings settings,
            int rowCount,
            string sentToApp)
        {
            string now = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ");
            string nowSpace = DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ssZ");

            var message = new MessagePayload
            {
                Count = rowCount.ToString(),
                MessageType = "Update",
                ServiceInfraStructureId = tableName,
                Project = settings.ProjectCode,
                SentFromApp = "WORKPACKS",
                SentFromUser = settings.SentFromUser,
                SentTime = now,
                SentToApp = sentToApp,
                MessageBody = "Activity published from workpacks"
            };
            string messageJson = JsonConvert.SerializeObject(message);

            var context = new ContextPayload
            {
                ObjectContext = settings.ProjectCode,
                ObjectName = tableName,
                MessageId = Guid.NewGuid().ToString(),
                SentFromId = settings.SentFromId,
                Message = messageJson,
                Topic = "update",
                SentFrom = "WORKPACKS",
                SentTo = "AvevaErm",
                Date = nowSpace
            };

            return JsonConvert.SerializeObject(context);
        }

        // ────────────────────────────────────────────────────────────────
        //  HELPERS
        // ────────────────────────────────────────────────────────────────

        private static string NormaliseDateToIso(string dateStr)
        {
            if (string.IsNullOrWhiteSpace(dateStr))
                return dateStr;

            string[] formats = { "dd-MM-yyyy", "dd/MM/yyyy", "MM-dd-yyyy", "MM/dd/yyyy",
                                 "yyyy-MM-dd", "yyyy/MM/dd" };

            if (DateTime.TryParseExact(dateStr, formats,
                    CultureInfo.InvariantCulture, DateTimeStyles.None, out var dt))
            {
                return dt.ToString("yyyy-MM-dd");
            }

            return dateStr;
        }

        private static List<List<T>> Chunk<T>(List<T> source, int chunkSize)
        {
            var chunks = new List<List<T>>();
            for (int i = 0; i < source.Count; i += chunkSize)
                chunks.Add(source.GetRange(i, Math.Min(chunkSize, source.Count - i)));
            return chunks;
        }
    }

    // ────────────────────────────────────────────────────────────────
    //  CONVERSION RESULT
    // ────────────────────────────────────────────────────────────────

    public class ConversionResult
    {
        public List<PublishPayload> Payloads { get; set; } = new();
        public ColumnMappingResult? MappingResult { get; set; }
        public List<string> Errors { get; set; } = new();
        public List<string> Warnings { get; set; } = new();

        public bool HasErrors => Errors.Count > 0;
        public bool HasWarnings => Warnings.Count > 0 || (MappingResult?.Warnings.Count ?? 0) > 0;
        public bool IsSuccess => !HasErrors && Payloads.Count > 0;

        public string GetFullReport()
        {
            var lines = new List<string>();

            if (Errors.Count > 0)
            {
                lines.Add($"=== ERRORS ({Errors.Count}) ===");
                foreach (var e in Errors)
                    lines.Add($"  ERROR: {e}");
                lines.Add("");
            }

            if (MappingResult != null)
            {
                lines.Add(MappingResult.GetSummary());
                lines.Add("");
            }

            if (Warnings.Count > 0)
            {
                lines.Add($"=== ROW WARNINGS ({Warnings.Count}) ===");
                foreach (var w in Warnings)
                    lines.Add($"  WARNING: {w}");
            }

            if (Payloads.Count > 0)
                lines.Add($"\nSUCCESS: {Payloads.Count} payload(s) ready for publishing.");

            return string.Join("\n", lines);
        }
    }
}
