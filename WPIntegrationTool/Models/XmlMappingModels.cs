namespace WPIntegrationTool.Models
{
    /// <summary>
    /// Represents a fully parsed XML mapping configuration.
    /// </summary>
    public class XmlMappingConfig
    {
        /// <summary>
        /// The detected integration context(s) from the XML, e.g. "CWPIWPPublish" or "ActualMTOPublish".
        /// Derived from the integrationContext attribute on &lt;class&gt; elements.
        /// </summary>
        public List<string> IntegrationContexts { get; set; } = new();

        /// <summary>
        /// All parsed class definitions from the XML.
        /// </summary>
        public List<MappingClass> Classes { get; set; } = new();

        /// <summary>
        /// Every unique fromAttribute value found across all classes.
        /// These are the field names expected in the JSON Data payload.
        /// </summary>
        public HashSet<string> AllFromAttributes { get; set; } = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// The source file path (for error messages).
        /// </summary>
        public string SourceFilePath { get; set; } = string.Empty;

        /// <summary>
        /// Returns the primary integration context, or infers one from class IDs.
        /// </summary>
        public string PrimaryIntegrationContext
        {
            get
            {
                if (IntegrationContexts.Count > 0)
                    return IntegrationContexts[0];

                // Infer from class IDs if integrationContext attribute is missing
                var classIds = Classes.Select(c => c.Id.ToUpperInvariant()).ToList();
                if (classIds.Any(id => id.Contains("PIPELINE") || id.Contains("MTO") ||
                                       id.Contains("FAB") || id.Contains("DRAW")))
                    return "ActualMTOPublish";

                if (classIds.Any(id => id.Contains("ACTIVITY")))
                    return "CWPIWPPublish";

                return "Unknown";
            }
        }

        /// <summary>
        /// Whether this config is for MTO.
        /// </summary>
        public bool IsMto => PrimaryIntegrationContext.Contains("MTO", StringComparison.OrdinalIgnoreCase);

        /// <summary>
        /// Whether this config is for CWP/IWP.
        /// </summary>
        public bool IsCwpIwp => PrimaryIntegrationContext.Contains("CWP", StringComparison.OrdinalIgnoreCase) ||
                                PrimaryIntegrationContext.Contains("IWP", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// A single &lt;class&gt; element parsed from the XML.
    /// </summary>
    public class MappingClass
    {
        public string Id { get; set; } = string.Empty;
        public string? IntegrationContext { get; set; }
        public string? UniqueIdField { get; set; }
        public bool OnlyMappedAttributes { get; set; }

        /// <summary>
        /// All &lt;map&gt; entries in this class.
        /// </summary>
        public List<FieldMapping> Maps { get; set; } = new();

        /// <summary>
        /// All &lt;const&gt; entries in this class.
        /// </summary>
        public List<ConstMapping> Constants { get; set; } = new();
    }

    /// <summary>
    /// A single &lt;map fromAttribute="..." toField="..." /&gt; entry.
    /// </summary>
    public class FieldMapping
    {
        public string FromAttribute { get; set; } = string.Empty;
        public string? ToField { get; set; }
        public string? MapType { get; set; }       // "Id", "Context", "Lookup"
        public string? DefaultValue { get; set; }
        public bool ToUpper { get; set; }
        public bool Trim { get; set; }
    }

    /// <summary>
    /// A single &lt;const toField="..." value="..." /&gt; entry.
    /// </summary>
    public class ConstMapping
    {
        public string ToField { get; set; } = string.Empty;
        public string Value { get; set; } = string.Empty;
    }
}
