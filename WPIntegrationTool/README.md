# WP Integration Tool

A WPF desktop application that reads CSV files, dynamically parses XML mapping configuration files to map columns, validates compatibility, and POSTs JSON payloads to the WORKPACKS integration API using NTLM authentication.

## Features

- **Runtime XML mapping**: Parses `DataTableEiwm*Map.xml` files at runtime — no hardcoded column mappings
- **Automatic validation**: Detects mismatches between XML mapping files and CSV data files
- **Smart column matching**: Direct name match + built-in alias dictionary for known CSV header variants
- **Two publish modes**: CWP/IWP Publish and Actual MTO Publish
- **NTLM authentication**: Explicit credentials or Windows default identity
- **Auto-detection**: Infers publish type from XML `integrationContext` attribute
- **Chunking**: Splits large CSVs into configurable chunk sizes (default 500 rows)
- **Validate Mapping**: Check column mapping and data compatibility before publishing
- **Payload preview**: Inspect the generated JSON before sending
- **Connection test**: Verifies server connectivity and credentials with descriptive error messages
- **Verbose logging**: Every step is logged with timestamps, including detailed error diagnostics

## Prerequisites

- Windows 10/11
- [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0) (or later)
- Visual Studio 2022+ (recommended) or `dotnet` CLI

## Build & Run

```bash
cd WPIntegrationTool
dotnet restore
dotnet build
dotnet run
```

Or open `WPIntegrationTool.sln` in Visual Studio and press F5.

## Usage

1. **Connection** – Enter server URL, datasource name, and NTLM credentials. Click **Test Connection**.
2. **Sender & Project** – Set sender email, sender ID, project code, and chunk size.
3. **Select XML mapping file** – Browse for the XML config (e.g., `DataTableEiwmActivityMap.xml` for CWP/IWP, or `DataTableEiwmPipingMTOMap.xml` for MTO). The tool parses it immediately and auto-selects the publish type.
4. **Select CSV file** – Browse for the CSV data file.
5. **Validate Mapping** – (Optional) Click to run a dry-run that checks column compatibility and reports any warnings.
6. **Preview** – Click **Preview Payload** to inspect the first chunk's JSON.
7. **Publish** – Click **Publish to API** to send all chunks. Progress and acknowledgement IDs appear in the log.

## Error Handling

The tool provides verbose error messages for every failure scenario:

- **Wrong XML for the selected publish type** – e.g., loading a CWP/IWP XML when MTO is selected, with exact details of what was expected vs found
- **CSV column mismatches** – Reports which CSV columns could not be mapped and which XML fields have no corresponding CSV column
- **Empty files / missing headers** – Detected and reported before any processing
- **Row-level errors** – Individual row failures are logged with line numbers; processing continues for remaining rows
- **HTTP errors** – Status-code-specific guidance (401 → credentials, 404 → datasource name, 500 → server issue)
- **Connection failures** – Distinguishes between unreachable servers, SSL issues, and auth failures

## How XML Mapping Works

The XML files define `<class>` elements with `<map fromAttribute="...">` entries. The tool:

1. Parses all `<class>` elements and collects every `fromAttribute` value
2. Uses the `integrationContext` attribute to determine if the XML is for MTO or CWP/IWP
3. Matches CSV column headers to `fromAttribute` names (case-insensitive direct match first, then alias dictionary)
4. Reports any unmatched columns as warnings
5. Builds JSON payload using `fromAttribute` names as the field names in the `Data` array

## Project Structure

```
WPIntegrationTool/
├── WPIntegrationTool.csproj
├── App.xaml / App.xaml.cs
├── MainWindow.xaml / MainWindow.xaml.cs
├── Models/
│   ├── PayloadModels.cs          # Payload, context, metadata data classes
│   └── XmlMappingModels.cs       # Parsed XML mapping structures
└── Services/
    ├── XmlMappingParser.cs        # Parses XML mapping files at runtime
    ├── ColumnMapper.cs            # CSV header → fromAttribute matching + aliases
    ├── CsvToPayloadConverter.cs   # Orchestrates: validate → map → chunk → build
    └── ApiService.cs              # HTTP client with NTLM auth
```
