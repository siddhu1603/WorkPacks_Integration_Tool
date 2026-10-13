using WPIntegrationTool.Models;

namespace WPIntegrationTool.Services
{
    /// <summary>
    /// Maps CSV column headers to the XML fromAttribute field names.
    /// Uses direct matching first, then falls back to a built-in alias dictionary.
    /// Produces detailed validation reports.
    /// </summary>
    public class ColumnMapper
    {
        // ────────────────────────────────────────────────────────────────
        //  BUILT-IN ALIASES: CSV Header → fromAttribute name
        //  These cover the known naming differences between CSV exports
        //  and the XML mapping files.
        // ────────────────────────────────────────────────────────────────

        private static readonly Dictionary<string, string[]> MtoAliases = new(StringComparer.OrdinalIgnoreCase)
        {
            // CSV Header               → candidate XML fromAttribute names
            ["DesignProjectId"]          = new[] { "project_number" },
            ["DisciplineCode"]           = new[] { "discipline_code" },
            ["DeliveryDesignation"]      = new[] { "delivery_designation" },
            ["Fpline"]                   = new[] { "process_line_number" },
            ["Farea"]                    = new[] { "area" },
            ["Fdrawing"]                 = new[] { "drawing_number" },
            ["Frevision"]                = new[] { "drawing_revision_number" },
            ["DrawingSheetnumber"]       = new[] { "drawing_sheet_number" },
            ["Quantity"]                 = new[] { "quantity" },
            ["Unit"]                     = new[] { "unit" },
            ["MaterialId"]               = new[] { "material_id" },
            ["ModelTagId"]               = new[] { "model_tag_id" },
            ["SpoolNumber"]              = new[] { "spool_number" },
            ["DesignUniqueId"]           = new[] { "refno" },
            ["TagNo"]                    = new[] { "tag_no" },
            ["TagDescription"]           = new[] { "tag_description" },
            ["MtoType"]                  = new[] { "mto_type" },
            ["MtoNo"]                    = new[] { "mto_no" },
            ["Type"]                     = new[] { "type" },
            ["System"]                   = new[] { "system" },
            ["Diameter"]                 = new[] { "diameter" },
            ["NormsQuan"]                = new[] { "norms_quan" },
            ["NormsUnit"]                = new[] { "normsunit" },
            ["Sequence"]                 = new[] { "sequence" },
            ["FabQty"]                   = new[] { "fabqty" },
            ["Activity"]                 = new[] { "activity" },
            ["Area"]                     = new[] { "area" },
        };

        // CWP/IWP mapping XMLs exist in two vocabularies (iwp_* and cwp_*), and the
        // CSV may use either. Each CSV header therefore lists every candidate
        // fromAttribute; the first candidate present in the loaded XML wins.
        private static readonly Dictionary<string, string[]> CwpIwpAliases = new(StringComparer.OrdinalIgnoreCase)
        {
            // Identity
            ["cwp_id"]                  = new[] { "iwp_id", "cwp_id" },
            ["iwp_id"]                  = new[] { "cwp_id", "iwp_id" },
            ["IwpId"]                   = new[] { "iwp_id", "cwp_id" },
            ["CwpId"]                   = new[] { "cwp_id", "iwp_id" },
            ["IWP_ID"]                  = new[] { "iwp_id", "cwp_id" },
            ["CWP_ID"]                  = new[] { "cwp_id", "iwp_id" },

            // Description
            ["cwp_description"]         = new[] { "iwp_description", "cwp_description" },
            ["iwp_description"]         = new[] { "cwp_description", "iwp_description" },
            ["IwpDescription"]          = new[] { "iwp_description", "cwp_description" },
            ["CwpDescription"]          = new[] { "cwp_description", "iwp_description" },
            ["Description"]             = new[] { "iwp_description", "cwp_description" },

            // Construction work area
            ["cwa_id"]                  = new[] { "cwa", "cwa_id" },
            ["cwa"]                     = new[] { "cwa_id", "cwa" },
            ["ConstructionArea"]        = new[] { "cwa", "cwa_id" },

            // Dates
            ["planned_start"]           = new[] { "planned_start_date", "planned_start" },
            ["planned_start_date"]      = new[] { "planned_start", "planned_start_date" },
            ["PlannedStartDate"]        = new[] { "planned_start_date", "planned_start" },
            ["planned_finish"]          = new[] { "planned_finished_date", "planned_finish_date", "planned_finish" },
            ["planned_finished_date"]   = new[] { "planned_finish", "planned_finished_date" },
            ["PlannedFinishedDate"]     = new[] { "planned_finished_date", "planned_finish" },
            ["PlannedFinishDate"]       = new[] { "planned_finished_date", "planned_finish" },

            // Project version
            ["context"]                 = new[] { "Context" },
            ["ProjectVersion"]          = new[] { "Context" },
        };

        // ────────────────────────────────────────────────────────────────
        //  PUBLIC API
        // ────────────────────────────────────────────────────────────────

        /// <summary>
        /// Builds a column mapping from CSV headers to XML fromAttribute names.
        /// Returns a <see cref="ColumnMappingResult"/> with the mapping plus detailed diagnostics.
        /// </summary>
        public static ColumnMappingResult BuildMapping(
            string[] csvHeaders,
            XmlMappingConfig xmlConfig,
            PublishType publishType)
        {
            var result = new ColumnMappingResult();
            var aliases = publishType == PublishType.ActualMTO ? MtoAliases : CwpIwpAliases;
            var expectedFields = xmlConfig.AllFromAttributes;

            // Track which CSV headers got mapped
            var unmappedCsvHeaders = new HashSet<string>(csvHeaders, StringComparer.OrdinalIgnoreCase);

            // Build CSV header → fromAttribute mapping
            foreach (var csvHeader in csvHeaders)
            {
                string trimmed = csvHeader.Trim();

                // 1) Direct match: CSV header IS a fromAttribute (case-insensitive)
                var directMatch = expectedFields
                    .FirstOrDefault(f => f.Equals(trimmed, StringComparison.OrdinalIgnoreCase));

                if (directMatch != null)
                {
                    result.CsvToFromAttribute[trimmed] = directMatch;
                    unmappedCsvHeaders.Remove(trimmed);
                    result.MatchDetails.Add($"  Column '{trimmed}' → '{directMatch}' (direct match)");
                    continue;
                }

                // 2) Normalised match: ignore case, underscores, spaces and hyphens
                var normalisedMatch = expectedFields
                    .FirstOrDefault(f => Normalise(f) == Normalise(trimmed));

                if (normalisedMatch != null)
                {
                    result.CsvToFromAttribute[trimmed] = normalisedMatch;
                    unmappedCsvHeaders.Remove(trimmed);
                    result.MatchDetails.Add($"  Column '{trimmed}' → '{normalisedMatch}' (normalised match)");
                    continue;
                }

                // 3) Alias match: CSV header has known candidate fromAttribute names
                if (aliases.TryGetValue(trimmed, out var aliasTargets))
                {
                    // Prefer the first candidate actually declared in the XML
                    var xmlMatch = aliasTargets
                        .Select(t => expectedFields.FirstOrDefault(f => f.Equals(t, StringComparison.OrdinalIgnoreCase)))
                        .FirstOrDefault(m => m != null);

                    if (xmlMatch != null)
                    {
                        result.CsvToFromAttribute[trimmed] = xmlMatch;
                        unmappedCsvHeaders.Remove(trimmed);
                        result.MatchDetails.Add($"  Column '{trimmed}' → '{xmlMatch}' (alias match)");
                        continue;
                    }

                    // No candidate is in the XML — still map it but warn
                    result.CsvToFromAttribute[trimmed] = aliasTargets[0];
                    unmappedCsvHeaders.Remove(trimmed);
                    result.Warnings.Add(
                        $"Column '{trimmed}' has alias '{aliasTargets[0]}' but this field " +
                        $"is not defined in the XML mapping file. It will be included in the payload anyway.");
                    continue;
                }

                // 4) No match at all
                // Don't add to mapping — this CSV column won't be in the payload
            }

            // Report unmapped CSV headers (informational, not errors)
            foreach (var header in unmappedCsvHeaders)
            {
                string trimmed = header.Trim();
                if (string.IsNullOrWhiteSpace(trimmed))
                    continue;

                // Special columns that are expected to not map (e.g., is_deleted)
                if (trimmed.Equals("is_deleted", StringComparison.OrdinalIgnoreCase))
                {
                    result.MatchDetails.Add($"  Column '{trimmed}' → (used as filter, not in payload)");
                    continue;
                }

                result.UnmappedCsvColumns.Add(trimmed);
                result.Warnings.Add(
                    $"CSV column '{trimmed}' could not be matched to any XML fromAttribute. " +
                    "This column will be ignored.");
            }

            // Check which XML fromAttributes have NO corresponding CSV column
            foreach (var expected in expectedFields)
            {
                bool isMapped = result.CsvToFromAttribute.Values
                    .Any(v => v.Equals(expected, StringComparison.OrdinalIgnoreCase));

                if (!isMapped)
                {
                    result.UnmappedXmlFields.Add(expected);
                    result.Warnings.Add(
                        $"XML expects fromAttribute '{expected}' but no CSV column maps to it. " +
                        "This field will be empty in the payload.");
                }
            }

            // Without a mapType="Id" value the receiving parser cannot construct the
            // object, so every row is silently discarded after the API returns 200.
            var missingIdFields = xmlConfig.Classes
                .SelectMany(c => c.Maps)
                .Where(m => string.Equals(m.MapType, "Id", StringComparison.OrdinalIgnoreCase))
                .Select(m => m.FromAttribute)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Where(id => !result.CsvToFromAttribute.Values
                    .Any(v => v.Equals(id, StringComparison.OrdinalIgnoreCase)))
                .ToList();

            // Determine overall validity
            result.IsValid = result.CsvToFromAttribute.Count > 0 && missingIdFields.Count == 0;

            if (result.CsvToFromAttribute.Count == 0)
            {
                result.Errors.Add(
                    "No CSV columns could be mapped to XML fromAttribute fields. " +
                    "The CSV file does not match this XML mapping configuration.\n" +
                    $"CSV columns found: {string.Join(", ", csvHeaders)}\n" +
                    $"XML expects: {string.Join(", ", expectedFields)}");
            }
            else if (missingIdFields.Count > 0)
            {
                result.Errors.Add(
                    $"No CSV column maps to the mandatory identity field(s) " +
                    $"'{string.Join("', '", missingIdFields)}' (mapType=\"Id\" in the XML mapping).\n" +
                    "  Rows without an identity value are accepted by the API but discarded " +
                    "by the receiving import, so nothing would appear in ERM.\n" +
                    $"  CSV columns found: {string.Join(", ", csvHeaders)}\n" +
                    $"  XML expects: {string.Join(", ", expectedFields)}");
            }

            return result;
        }

        /// <summary>
        /// Strips case, underscores, spaces and hyphens so 'planned_start' and
        /// 'PlannedStart' compare equal.
        /// </summary>
        private static string Normalise(string value) =>
            new string(value.Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant();
    }

    /// <summary>
    /// Result of the column mapping operation with full diagnostics.
    /// </summary>
    public class ColumnMappingResult
    {
        /// <summary>
        /// The actual mapping: CSV column header → fromAttribute name.
        /// </summary>
        public Dictionary<string, string> CsvToFromAttribute { get; set; } = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// Whether the mapping is usable (at least some columns mapped).
        /// </summary>
        public bool IsValid { get; set; }

        /// <summary>
        /// Fatal errors that prevent processing.
        /// </summary>
        public List<string> Errors { get; set; } = new();

        /// <summary>
        /// Non-fatal issues (missing columns, unmapped fields).
        /// </summary>
        public List<string> Warnings { get; set; } = new();

        /// <summary>
        /// Detailed per-column match log for the UI.
        /// </summary>
        public List<string> MatchDetails { get; set; } = new();

        /// <summary>
        /// CSV columns that had no match in XML or aliases.
        /// </summary>
        public List<string> UnmappedCsvColumns { get; set; } = new();

        /// <summary>
        /// XML fromAttribute fields that have no corresponding CSV column.
        /// </summary>
        public List<string> UnmappedXmlFields { get; set; } = new();

        /// <summary>
        /// Number of successfully mapped columns.
        /// </summary>
        public int MappedCount => CsvToFromAttribute.Count;

        /// <summary>
        /// Builds a human-readable summary of the mapping.
        /// </summary>
        public string GetSummary()
        {
            var lines = new List<string>
            {
                $"Column Mapping Summary: {MappedCount} mapped, " +
                $"{UnmappedCsvColumns.Count} CSV columns ignored, " +
                $"{UnmappedXmlFields.Count} XML fields unmapped"
            };

            if (MatchDetails.Count > 0)
            {
                lines.Add("Mapped columns:");
                lines.AddRange(MatchDetails);
            }

            if (Warnings.Count > 0)
            {
                lines.Add("");
                lines.Add($"Warnings ({Warnings.Count}):");
                lines.AddRange(Warnings.Select(w => $"  ⚠ {w}"));
            }

            if (Errors.Count > 0)
            {
                lines.Add("");
                lines.Add($"Errors ({Errors.Count}):");
                lines.AddRange(Errors.Select(e => $"  ✗ {e}"));
            }

            return string.Join("\n", lines);
        }
    }
}
