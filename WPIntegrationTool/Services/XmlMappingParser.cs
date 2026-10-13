using System.IO;
using System.Xml.Linq;
using WPIntegrationTool.Models;

namespace WPIntegrationTool.Services
{
    /// <summary>
    /// Parses the DataTable XML mapping files into a structured <see cref="XmlMappingConfig"/>.
    /// </summary>
    public static class XmlMappingParser
    {
        /// <summary>
        /// Parses an XML mapping file and returns the structured config.
        /// Throws descriptive exceptions on any parse failure.
        /// </summary>
        public static XmlMappingConfig Parse(string xmlFilePath)
        {
            // ── File-level validation ──────────────────────────────────
            if (string.IsNullOrWhiteSpace(xmlFilePath))
                throw new ArgumentException("XML mapping file path is empty.");

            if (!File.Exists(xmlFilePath))
                throw new FileNotFoundException(
                    $"XML mapping file not found at: {xmlFilePath}");

            string extension = Path.GetExtension(xmlFilePath).ToLowerInvariant();
            if (extension != ".xml")
                throw new InvalidOperationException(
                    $"Expected an .xml file but got '{extension}'. " +
                    $"File: {Path.GetFileName(xmlFilePath)}");

            // ── Parse XML ──────────────────────────────────────────────
            XDocument doc;
            try
            {
                doc = XDocument.Load(xmlFilePath);
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException(
                    $"Failed to parse XML file '{Path.GetFileName(xmlFilePath)}'. " +
                    $"The file may be malformed or contain invalid XML.\n" +
                    $"Details: {ex.Message}", ex);
            }

            // ── Validate root element ──────────────────────────────────
            var root = doc.Root;
            if (root == null)
                throw new InvalidOperationException(
                    $"XML file '{Path.GetFileName(xmlFilePath)}' has no root element.");

            if (root.Name.LocalName != "config")
                throw new InvalidOperationException(
                    $"Expected root element <config> but found <{root.Name.LocalName}> " +
                    $"in '{Path.GetFileName(xmlFilePath)}'. " +
                    "This does not appear to be a valid mapping configuration file.");

            string? configType = root.Attribute("configType")?.Value;
            var validConfigTypes = new[] { "CsvMultiClassToEiwmObjParser", "DataTableToEiwmObjParser" };
            if (configType != null && !validConfigTypes.Contains(configType))
                throw new InvalidOperationException(
                    $"Unexpected configType='{configType}' in '{Path.GetFileName(xmlFilePath)}'. " +
                    $"Expected one of: {string.Join(", ", validConfigTypes)}.");

            // ── Find <mappings> ────────────────────────────────────────
            var mappingsElement = root.Element("mappings");
            if (mappingsElement == null)
                throw new InvalidOperationException(
                    $"No <mappings> element found in '{Path.GetFileName(xmlFilePath)}'. " +
                    "The XML structure is missing the required mappings section.");

            // ── Parse each <class> ─────────────────────────────────────
            var classElements = mappingsElement.Elements("class").ToList();
            if (classElements.Count == 0)
                throw new InvalidOperationException(
                    $"No <class> elements found inside <mappings> in '{Path.GetFileName(xmlFilePath)}'. " +
                    "At least one class mapping is required.");

            var config = new XmlMappingConfig
            {
                SourceFilePath = xmlFilePath
            };

            foreach (var classEl in classElements)
            {
                var mappingClass = ParseClass(classEl, xmlFilePath);
                config.Classes.Add(mappingClass);

                // Collect integration context
                if (!string.IsNullOrWhiteSpace(mappingClass.IntegrationContext))
                    config.IntegrationContexts.Add(mappingClass.IntegrationContext);

                // Collect all fromAttribute values
                foreach (var map in mappingClass.Maps)
                {
                    if (!string.IsNullOrWhiteSpace(map.FromAttribute))
                        config.AllFromAttributes.Add(map.FromAttribute);
                }
            }

            if (config.AllFromAttributes.Count == 0)
                throw new InvalidOperationException(
                    $"No <map> elements with fromAttribute found in '{Path.GetFileName(xmlFilePath)}'. " +
                    "The XML defines no column mappings.");

            return config;
        }

        /// <summary>
        /// Parses a single &lt;class&gt; element.
        /// </summary>
        private static MappingClass ParseClass(XElement classEl, string sourceFile)
        {
            string? id = classEl.Attribute("id")?.Value;
            if (string.IsNullOrWhiteSpace(id))
                throw new InvalidOperationException(
                    $"Found a <class> element without an 'id' attribute in '{Path.GetFileName(sourceFile)}'. " +
                    "Every <class> must have an id.");

            var mappingClass = new MappingClass
            {
                Id = id,
                IntegrationContext = classEl.Attribute("integrationContext")?.Value,
                UniqueIdField = classEl.Attribute("uniqueIdField")?.Value,
                OnlyMappedAttributes = classEl.Attribute("onlyMappedAttributes")?.Value?
                    .Equals("true", StringComparison.OrdinalIgnoreCase) ?? false
            };

            // Parse <map> elements
            foreach (var mapEl in classEl.Elements("map"))
            {
                string? fromAttr = mapEl.Attribute("fromAttribute")?.Value;
                if (string.IsNullOrWhiteSpace(fromAttr))
                    continue; // skip maps without fromAttribute

                mappingClass.Maps.Add(new FieldMapping
                {
                    FromAttribute = fromAttr,
                    ToField = mapEl.Attribute("toField")?.Value,
                    MapType = mapEl.Attribute("mapType")?.Value,
                    DefaultValue = mapEl.Attribute("defaultValue")?.Value,
                    ToUpper = mapEl.Attribute("toUpper")?.Value?
                        .Equals("true", StringComparison.OrdinalIgnoreCase) ?? false,
                    Trim = mapEl.Attribute("trim")?.Value?
                        .Equals("true", StringComparison.OrdinalIgnoreCase) ?? false
                });
            }

            // Parse <const> elements
            foreach (var constEl in classEl.Elements("const"))
            {
                string? toField = constEl.Attribute("toField")?.Value;
                string? value = constEl.Attribute("value")?.Value;
                if (!string.IsNullOrWhiteSpace(toField))
                {
                    mappingClass.Constants.Add(new ConstMapping
                    {
                        ToField = toField,
                        Value = value ?? string.Empty
                    });
                }
            }

            return mappingClass;
        }
    }
}
