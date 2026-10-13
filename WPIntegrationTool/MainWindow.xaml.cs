using System.IO;
using System.Windows;
using System.Windows.Media;
using Microsoft.Win32;
using Newtonsoft.Json;
using WPIntegrationTool.Models;
using WPIntegrationTool.Services;

namespace WPIntegrationTool
{
    public partial class MainWindow : Window
    {
        private string? _selectedCsvPath;
        private string? _selectedXmlPath;
        private XmlMappingConfig? _xmlConfig;

        public MainWindow()
        {
            InitializeComponent();
            Log("Application started.");
            Log("Steps: 1) Configure connection  2) Select XML mapping file  3) Select CSV file  4) Publish");
        }

        // ──────────────────────────────────────────────────────────────
        //  HELPERS
        // ──────────────────────────────────────────────────────────────

        private ConnectionSettings BuildSettings()
        {
            string username = TxtUsername.Text.Trim();
            string domain = TxtDomain.Text.Trim();

            if (username.Contains('\\'))
            {
                var parts = username.Split('\\', 2);
                domain = parts[0];
                username = parts[1];
            }

            int chunkSize = int.TryParse(TxtChunkSize.Text.Trim(), out var cs) && cs > 0 ? cs : 500;
            var authType = RbAuthOidc.IsChecked == true ? AuthType.OIDC : AuthType.NTLM;

            return new ConnectionSettings
            {
                AuthType = authType,
                BaseUrl = TxtBaseUrl.Text.Trim(),
                DatasourceName = TxtDatasource.Text.Trim(),
                SentFromUser = TxtSentFromUser.Text.Trim(),
                SentFromId = TxtSentFromId.Text.Trim(),
                ProjectCode = TxtProjectCode.Text.Trim(),
                ChunkSize = chunkSize,
                IgnoreSslErrors = ChkIgnoreSsl.IsChecked == true,

                // NTLM
                Username = username,
                Password = TxtPassword.Password,
                Domain = domain,

                // OIDC
                OidcAuthority = TxtOidcAuthority.Text.Trim(),
                OidcAudience = TxtOidcAudience.Text.Trim(),
                OidcClientId = TxtOidcClientId.Text.Trim(),
                OidcClientSecret = TxtOidcClientSecret.Password,
                OidcScopes = TxtOidcScopes.Text.Trim()
            };
        }

        private PublishType SelectedPublishType =>
            RbMto.IsChecked == true ? PublishType.ActualMTO : PublishType.CWPIWP;

        private string SelectedPublishTypeName =>
            SelectedPublishType == PublishType.ActualMTO ? "Actual MTO" : "CWP / IWP";

        private string SelectedAuthTypeName =>
            RbAuthOidc.IsChecked == true ? "OIDC" : "NTLM";

        // ──────────────────────────────────────────────────────────────
        //  AUTH TYPE SWITCHING
        // ──────────────────────────────────────────────────────────────

        private void AuthType_Changed(object sender, RoutedEventArgs e)
        {
            // Guard: controls may not be initialised yet during XAML loading
            if (PanelNtlm == null || PanelOidc == null)
                return;

            bool isOidc = RbAuthOidc.IsChecked == true;

            PanelNtlm.Visibility = isOidc ? Visibility.Collapsed : Visibility.Visible;
            PanelOidc.Visibility = isOidc ? Visibility.Visible : Visibility.Collapsed;

            if (isOidc)
            {
                // Cloud AIS serves the data API under a /data prefix; on-prem does not.
                TxtBaseUrl.Text = "https://eu.ais.test-connect.aveva.com/data";
                Log("Switched to OIDC — Base URL updated.");
            }
            else
            {
                TxtBaseUrl.Text = "https://INHYDL22274:60930";
                Log("Switched to NTLM — Base URL updated.");
            }
        }

        private void Log(string message)
        {
            string timestamp = DateTime.Now.ToString("HH:mm:ss");
            TxtLog.AppendText($"[{timestamp}] {message}\n");
            TxtLog.ScrollToEnd();
        }

        private void LogError(string message) => Log($"ERROR: {message}");
        private void LogWarning(string message) => Log($"WARNING: {message}");

        private void SetBusy(bool busy)
        {
            BtnPublish.IsEnabled = !busy;
            BtnPreview.IsEnabled = !busy;
            BtnValidate.IsEnabled = !busy;
            BtnBrowseCsv.IsEnabled = !busy;
            BtnBrowseXml.IsEnabled = !busy;
            BtnTestConnection.IsEnabled = !busy;
            ProgressPanel.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        }

        private void SetFileStatus(System.Windows.Controls.TextBlock statusBlock, string text, bool isError)
        {
            statusBlock.Text = text;
            statusBlock.Foreground = isError
                ? new SolidColorBrush(Color.FromRgb(200, 50, 50))
                : new SolidColorBrush(Color.FromRgb(50, 150, 50));
        }

        /// <summary>
        /// Validates that both files are selected and ready. Returns true if OK.
        /// </summary>
        private bool ValidateFilesSelected(bool showMessageBox = true)
        {
            var issues = new List<string>();

            if (string.IsNullOrWhiteSpace(_selectedXmlPath) || !File.Exists(_selectedXmlPath))
                issues.Add("No XML mapping file selected, or the file no longer exists at the specified path.");

            if (_xmlConfig == null)
                issues.Add("XML mapping file has not been parsed successfully. Please re-select the XML file.");

            if (string.IsNullOrWhiteSpace(_selectedCsvPath) || !File.Exists(_selectedCsvPath))
                issues.Add("No CSV data file selected, or the file no longer exists at the specified path.");

            if (issues.Count > 0)
            {
                string msg = "Please fix the following before proceeding:\n\n" +
                             string.Join("\n\n", issues.Select((iss, i) => $"{i + 1}. {iss}"));

                if (showMessageBox)
                    MessageBox.Show(msg, "Files Required", MessageBoxButton.OK, MessageBoxImage.Warning);

                foreach (var iss in issues)
                    LogError(iss);

                return false;
            }
            return true;
        }

        // ──────────────────────────────────────────────────────────────
        //  BROWSE XML MAPPING FILE
        // ──────────────────────────────────────────────────────────────

        private void BtnBrowseXml_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new OpenFileDialog
            {
                Filter = "XML files (*.xml)|*.xml|All files (*.*)|*.*",
                Title = "Select XML Mapping Configuration File"
            };

            if (dlg.ShowDialog() != true)
                return;

            _selectedXmlPath = dlg.FileName;
            TxtXmlPath.Text = dlg.FileName;
            TxtXmlPath.Foreground = Brushes.Black;

            Log($"XML file selected: {Path.GetFileName(dlg.FileName)}");
            Log("Parsing XML mapping configuration…");

            // Parse immediately so we can show feedback
            try
            {
                _xmlConfig = XmlMappingParser.Parse(dlg.FileName);

                string contexts = _xmlConfig.IntegrationContexts.Count > 0
                    ? string.Join(", ", _xmlConfig.IntegrationContexts)
                    : $"(inferred: {_xmlConfig.PrimaryIntegrationContext})";

                Log($"  XML parsed successfully.");
                Log($"  Integration context(s): {contexts}");
                Log($"  Classes found: {string.Join(", ", _xmlConfig.Classes.Select(c => c.Id))}");
                Log($"  Total unique fromAttribute fields: {_xmlConfig.AllFromAttributes.Count}");
                Log($"  Fields: {string.Join(", ", _xmlConfig.AllFromAttributes.OrderBy(x => x))}");

                SetFileStatus(TxtXmlStatus,
                    $"Parsed OK — {_xmlConfig.Classes.Count} class(es), " +
                    $"{_xmlConfig.AllFromAttributes.Count} fields, " +
                    $"context: {_xmlConfig.PrimaryIntegrationContext}", false);

                // Auto-select publish type based on XML
                if (_xmlConfig.IsMto)
                {
                    RbMto.IsChecked = true;
                    Log("  Auto-selected publish type: Actual MTO (based on XML integrationContext).");
                }
                else if (_xmlConfig.IsCwpIwp)
                {
                    RbCwpIwp.IsChecked = true;
                    Log("  Auto-selected publish type: CWP / IWP (based on XML integrationContext).");
                }
            }
            catch (Exception ex)
            {
                _xmlConfig = null;
                LogError($"Failed to parse XML: {ex.Message}");

                if (ex.InnerException != null)
                    LogError($"  Inner exception: {ex.InnerException.Message}");

                SetFileStatus(TxtXmlStatus, $"Parse FAILED: {ex.Message}", true);

                MessageBox.Show(
                    $"Failed to parse the XML mapping file.\n\n{ex.Message}",
                    "XML Parse Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        // ──────────────────────────────────────────────────────────────
        //  BROWSE CSV DATA FILE
        // ──────────────────────────────────────────────────────────────

        private void BtnBrowseCsv_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new OpenFileDialog
            {
                Filter = "All supported (*.csv;*.xlsx;*.xls)|*.csv;*.xlsx;*.xls|" +
                         "CSV files (*.csv)|*.csv|" +
                         "Excel files (*.xlsx;*.xls)|*.xlsx;*.xls|" +
                         "All files (*.*)|*.*",
                Title = "Select a Data File (CSV or Excel)"
            };

            if (dlg.ShowDialog() != true)
                return;

            _selectedCsvPath = dlg.FileName;
            TxtCsvPath.Text = dlg.FileName;
            TxtCsvPath.Foreground = Brushes.Black;

            string ext = Path.GetExtension(dlg.FileName).ToLowerInvariant();
            bool isExcel = ext == ".xlsx" || ext == ".xls";
            string fileTypeName = isExcel ? "Excel" : "CSV";
            Log($"{fileTypeName} file selected: {Path.GetFileName(dlg.FileName)}");

            // Quick header check (skip for Excel — too expensive to partial-read)
            try
            {
                if (isExcel)
                {
                    SetFileStatus(TxtCsvStatus,
                        "Excel file loaded — headers will be read during validation/publish.", false);
                    Log("  (Headers and row count will be reported when validation runs.)");
                }
                else
                {
                    var lines = File.ReadLines(dlg.FileName).Take(2).ToList();
                    if (lines.Count == 0)
                    {
                        SetFileStatus(TxtCsvStatus, "ERROR: File is empty.", true);
                        LogError("CSV file is completely empty (0 bytes or no lines).");
                        return;
                    }

                    var headers = lines[0].Split(',').Select(h => h.Trim()).ToArray();
                    int dataLines = File.ReadLines(dlg.FileName).Count() - 1;

                    Log($"  CSV headers ({headers.Length}): {string.Join(", ", headers)}");
                    Log($"  Data rows: {dataLines}");

                    if (dataLines == 0)
                        LogWarning("CSV file has a header row but no data rows.");

                    SetFileStatus(TxtCsvStatus,
                        $"Loaded — {headers.Length} columns, {dataLines} data rows", false);
                }

                // Auto-detect publish type from filename if no XML loaded yet
                string lower = Path.GetFileName(dlg.FileName).ToLowerInvariant();
                if (_xmlConfig == null)
                {
                    if (lower.Contains("mto"))
                    {
                        RbMto.IsChecked = true;
                        Log("  Auto-detected publish type from filename: Actual MTO");
                    }
                    else if (lower.Contains("iwp") || lower.Contains("cwp"))
                    {
                        RbCwpIwp.IsChecked = true;
                        Log("  Auto-detected publish type from filename: CWP / IWP");
                    }
                }
            }
            catch (Exception ex)
            {
                LogError($"Failed to preview CSV: {ex.Message}");
                SetFileStatus(TxtCsvStatus, $"Preview failed: {ex.Message}", true);
            }
        }

        // ──────────────────────────────────────────────────────────────
        //  TEST CONNECTION
        // ──────────────────────────────────────────────────────────────

        private async void BtnTestConnection_Click(object sender, RoutedEventArgs e)
        {
            var settings = BuildSettings();

            if (string.IsNullOrWhiteSpace(settings.BaseUrl))
            {
                MessageBox.Show(
                    "Please enter a Server Base URL.\n\n" +
                    "Example: https://inhydvmw509:60930",
                    "Validation", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            if (settings.AuthType == AuthType.OIDC)
            {
                var oidcIssues = new List<string>();
                if (string.IsNullOrWhiteSpace(settings.OidcAuthority))
                    oidcIssues.Add("Authority URL is empty.");
                if (string.IsNullOrWhiteSpace(settings.OidcClientId))
                    oidcIssues.Add("Client ID is empty.");
                if (string.IsNullOrWhiteSpace(settings.OidcClientSecret))
                    oidcIssues.Add("Client Secret is empty.");
                if (string.IsNullOrWhiteSpace(settings.OidcAudience))
                    oidcIssues.Add("Audience is empty.");

                if (oidcIssues.Count > 0)
                {
                    MessageBox.Show(
                        "OIDC settings are incomplete:\n\n" +
                        string.Join("\n", oidcIssues),
                        "Validation", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }
            }

            if (!settings.BaseUrl.StartsWith("http://") && !settings.BaseUrl.StartsWith("https://"))
            {
                LogWarning("Base URL does not start with http:// or https://. This may cause connection failures.");
            }

            Log("Testing connection…");
            Log($"  URL: {settings.BaseUrl}/api/v1.2/datasources/Generic/{settings.DatasourceName}/acknowledgements");

            if (settings.AuthType == AuthType.OIDC)
            {
                Log($"  Auth: OIDC (Client Credentials)");
                Log($"  Authority: {settings.OidcAuthority}");
                Log($"  Client ID: {settings.OidcClientId}");
                Log($"  Audience: {settings.OidcAudience}");
            }
            else
            {
                Log($"  Auth: {(string.IsNullOrWhiteSpace(settings.Username) ? "Default Windows credentials" : $"NTLM ({settings.Username})")}");
            }
            try
            {
                using var api = new ApiService(settings);
                var result = await api.TestConnectionAsync();

                if (result.IsSuccess)
                {
                    Log($"Connection successful (HTTP {result.StatusCode}).");

                    if (!string.IsNullOrWhiteSpace(result.RawResponse))
                    {
                        string body = result.RawResponse.Length > 4000
                            ? result.RawResponse[..4000] + "… (truncated)"
                            : result.RawResponse;
                        Log($"  Acknowledgements for '{settings.DatasourceName}': {body}");
                    }

                    MessageBox.Show("Connection successful!", "Test Connection",
                        MessageBoxButton.OK, MessageBoxImage.Information);
                }
                else
                {
                    LogError($"Connection failed: {result.ErrorMessage}");

                    string details = $"Connection failed.\n\n" +
                                     $"Status: {result.ErrorMessage}\n\n";

                    if (result.StatusCode == 401)
                        details += settings.AuthType == AuthType.OIDC
                            ? "HTTP 401 Unauthorized — The OIDC token was rejected by the API.\n\n" +
                              "Tips:\n" +
                              "• Verify the Client ID and Client Secret\n" +
                              "• Check the Audience URL matches the API's expected audience\n" +
                              "• Ensure the token endpoint is correct ({Authority}/oauth/token)"
                            : "HTTP 401 Unauthorized — Your username or password is incorrect, " +
                              "or the account does not have permission to access this endpoint.\n\n" +
                              "Tips:\n" +
                              "• Check username format (DOMAIN\\user or just user)\n" +
                              "• Verify the password is correct\n" +
                              "• Ensure the account has API access";
                    else if (result.StatusCode == 403)
                        details += "HTTP 403 Forbidden — The credentials are valid but the account " +
                                   "does not have permission for this datasource.";
                    else if (result.StatusCode == 404)
                        details += "HTTP 404 Not Found — the host and credentials are fine but the " +
                                   "request path does not exist.\n\n" +
                                   "Tips:\n" +
                                   $"• Check the datasource name (currently '{settings.DatasourceName}')\n" +
                                   $"• Check the Base URL path prefix (currently '{settings.BaseUrl}'). " +
                                   "Cloud AIS serves this API under a '/data' prefix; on-prem AIS does not.";
                    else if (result.StatusCode == 0)
                        details += "No HTTP response received. Possible causes:\n" +
                                   "• Server is unreachable (wrong hostname or port)\n" +
                                   "• Firewall is blocking the connection\n" +
                                   "• SSL certificate error (try enabling 'Ignore SSL certificate errors')";

                    if (!string.IsNullOrWhiteSpace(result.RawResponse))
                        Log($"  Raw response: {result.RawResponse}");

                    MessageBox.Show(details, "Test Connection", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
            catch (Exception ex)
            {
                LogError($"Connection error: {ex.Message}");
                if (ex.InnerException != null)
                    LogError($"  Inner: {ex.InnerException.Message}");

                MessageBox.Show($"Connection error:\n\n{ex.Message}", "Test Connection",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        // ──────────────────────────────────────────────────────────────
        //  VALIDATE MAPPING (without publishing)
        // ──────────────────────────────────────────────────────────────

        private void BtnValidate_Click(object sender, RoutedEventArgs e)
        {
            if (!ValidateFilesSelected())
                return;

            Log("─── Running validation ───");

            try
            {
                var settings = BuildSettings();
                var convResult = CsvToPayloadConverter.Convert(
                    _selectedCsvPath!, _xmlConfig!, SelectedPublishType, settings, Log);

                string report = convResult.GetFullReport();
                Log(report);

                if (convResult.IsSuccess)
                {
                    MessageBox.Show(
                        $"Validation passed.\n\n" +
                        $"{convResult.Payloads.Count} chunk(s) would be sent.\n" +
                        $"Mapped columns: {convResult.MappingResult?.MappedCount ?? 0}\n" +
                        $"Warnings: {(convResult.MappingResult?.Warnings.Count ?? 0) + convResult.Warnings.Count}\n\n" +
                        "Check the log for full details.",
                        "Validation Result", MessageBoxButton.OK, MessageBoxImage.Information);
                }
                else
                {
                    MessageBox.Show(
                        $"Validation FAILED.\n\n" +
                        $"Errors:\n{string.Join("\n", convResult.Errors)}\n\n" +
                        "Check the log for full details.",
                        "Validation Result", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
            catch (Exception ex)
            {
                LogError($"Validation error: {ex.Message}");
                if (ex.InnerException != null)
                    LogError($"  Inner: {ex.InnerException.Message}");

                MessageBox.Show($"Unexpected error during validation:\n\n{ex.Message}",
                    "Validation Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        // ──────────────────────────────────────────────────────────────
        //  PREVIEW PAYLOAD
        // ──────────────────────────────────────────────────────────────

        private void BtnPreview_Click(object sender, RoutedEventArgs e)
        {
            if (!ValidateFilesSelected())
                return;

            Log("─── Generating preview ───");

            try
            {
                var settings = BuildSettings();
                var convResult = CsvToPayloadConverter.Convert(
                    _selectedCsvPath!, _xmlConfig!, SelectedPublishType, settings, Log);

                if (!convResult.IsSuccess)
                {
                    string report = convResult.GetFullReport();
                    Log(report);

                    MessageBox.Show(
                        $"Cannot generate preview — conversion failed.\n\n" +
                        $"{string.Join("\n", convResult.Errors)}\n\n" +
                        "Check the log for details.",
                        "Preview Error", MessageBoxButton.OK, MessageBoxImage.Error);
                    return;
                }

                Log($"Preview generated: {convResult.Payloads.Count} chunk(s).");

                // Show first payload pretty-printed
                string preview = JsonConvert.SerializeObject(convResult.Payloads[0], Formatting.Indented);

                var previewWindow = new Window
                {
                    Title = $"Payload Preview — Chunk 1 of {convResult.Payloads.Count} " +
                            $"({SelectedPublishTypeName})",
                    Width = 780, Height = 580,
                    WindowStartupLocation = WindowStartupLocation.CenterOwner,
                    Owner = this,
                    Background = Brushes.White
                };

                var tb = new System.Windows.Controls.TextBox
                {
                    Text = preview,
                    IsReadOnly = true,
                    TextWrapping = TextWrapping.Wrap,
                    VerticalScrollBarVisibility = System.Windows.Controls.ScrollBarVisibility.Auto,
                    HorizontalScrollBarVisibility = System.Windows.Controls.ScrollBarVisibility.Auto,
                    FontFamily = new FontFamily("Consolas"),
                    FontSize = 12,
                    Margin = new Thickness(12),
                    BorderThickness = new Thickness(0)
                };

                previewWindow.Content = tb;
                previewWindow.ShowDialog();
            }
            catch (Exception ex)
            {
                LogError($"Preview error: {ex.Message}");
                if (ex.InnerException != null)
                    LogError($"  Inner: {ex.InnerException.Message}");

                MessageBox.Show($"Error generating preview:\n\n{ex.Message}",
                    "Preview Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        // ──────────────────────────────────────────────────────────────
        //  PUBLISH
        // ──────────────────────────────────────────────────────────────

        private async void BtnPublish_Click(object sender, RoutedEventArgs e)
        {
            if (!ValidateFilesSelected())
                return;

            var settings = BuildSettings();

            // Validate connection settings
            var connIssues = new List<string>();
            if (string.IsNullOrWhiteSpace(settings.BaseUrl))
                connIssues.Add("Server Base URL is empty.");
            if (string.IsNullOrWhiteSpace(settings.SentFromUser))
                connIssues.Add("Sent From User (email) is empty.");
            if (string.IsNullOrWhiteSpace(settings.SentFromId))
                connIssues.Add("Sent From ID is empty.");
            if (string.IsNullOrWhiteSpace(settings.ProjectCode))
                connIssues.Add("Project Code is empty.");

            if (connIssues.Count > 0)
            {
                string msg = "Please fix the following connection settings:\n\n" +
                             string.Join("\n", connIssues.Select((iss, i) => $"{i + 1}. {iss}"));
                MessageBox.Show(msg, "Settings Required", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            // Convert CSV first
            Log("═══ Starting publish pipeline ═══");

            ConversionResult convResult;
            try
            {
                var publishType = SelectedPublishType; // read on UI thread before Task.Run
                convResult = await Task.Run(() =>
                    CsvToPayloadConverter.Convert(
                        _selectedCsvPath!, _xmlConfig!, publishType, settings,
                        msg => Dispatcher.Invoke(() => Log(msg))));
            }
            catch (Exception ex)
            {
                LogError($"Conversion failed: {ex.Message}");
                MessageBox.Show($"Failed to convert CSV to payloads:\n\n{ex.Message}",
                    "Conversion Error", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            if (!convResult.IsSuccess)
            {
                string report = convResult.GetFullReport();
                Log(report);
                MessageBox.Show(
                    $"Cannot publish — conversion failed.\n\n" +
                    $"{string.Join("\n", convResult.Errors)}\n\n" +
                    "Check the log for details.",
                    "Publish Error", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            // Warn about mapping warnings before publishing
            int totalWarnings = convResult.Warnings.Count +
                                (convResult.MappingResult?.Warnings.Count ?? 0);

            if (totalWarnings > 0)
            {
                var warnResult = MessageBox.Show(
                    $"Conversion produced {totalWarnings} warning(s).\n" +
                    "Some columns may be unmapped or have missing data.\n\n" +
                    "Do you want to continue publishing anyway?\n" +
                    "(Check the log for warning details)",
                    "Warnings Found", MessageBoxButton.YesNo, MessageBoxImage.Warning);

                if (warnResult != MessageBoxResult.Yes)
                {
                    Log("Publish cancelled by user due to warnings.");
                    return;
                }
            }

            // Confirm
            var confirm = MessageBox.Show(
                $"Ready to publish {SelectedPublishTypeName} data.\n\n" +
                $"File: {Path.GetFileName(_selectedCsvPath)}\n" +
                $"XML: {Path.GetFileName(_selectedXmlPath)}\n" +
                $"Server: {settings.BaseUrl}\n" +
                $"Datasource: {settings.DatasourceName}\n" +
                $"Auth: {(settings.AuthType == AuthType.OIDC ? "OIDC (Client Credentials)" : "NTLM")}\n" +
                $"Chunks: {convResult.Payloads.Count}\n\n" +
                "Continue?",
                "Confirm Publish", MessageBoxButton.YesNo, MessageBoxImage.Question);

            if (confirm != MessageBoxResult.Yes)
            {
                Log("Publish cancelled by user.");
                return;
            }

            // Send
            SetBusy(true);
            Log($"Sending {convResult.Payloads.Count} chunk(s) to API…");
            Log($"  Routing: datasource='{settings.DatasourceName}', " +
                $"table='{convResult.Payloads[0].TableName}', " +
                $"externalProjectId='{settings.ProjectCode}'");

            try
            {
                using var api = new ApiService(settings);
                int success = 0, failed = 0;
                var ackIds = new List<string>();

                for (int i = 0; i < convResult.Payloads.Count; i++)
                {
                    TxtProgress.Text = $"Sending chunk {i + 1} of {convResult.Payloads.Count}…";
                    PbarProgress.Value = (double)i / convResult.Payloads.Count * 100;

                    var result = await api.PublishAsync(convResult.Payloads[i]);

                    if (result.IsSuccess)
                    {
                        success++;
                        ackIds.Add(result.AcknowledgementId);
                        Log($"  Chunk {i + 1}/{convResult.Payloads.Count}: OK (HTTP {result.StatusCode}) " +
                            $"— AcknowledgementId = {result.AcknowledgementId}");
                    }
                    else
                    {
                        failed++;
                        LogError($"  Chunk {i + 1}/{convResult.Payloads.Count}: FAILED — {result.ErrorMessage}");

                        if (result.StatusCode == 401)
                            LogError("    → HTTP 401: Authentication failed. Check credentials.");
                        else if (result.StatusCode == 400)
                            LogError("    → HTTP 400: Bad request. The payload format may be incorrect.");
                        else if (result.StatusCode == 500)
                            LogError("    → HTTP 500: Internal server error. The server encountered an issue processing the data.");

                        if (!string.IsNullOrWhiteSpace(result.RawResponse))
                        {
                            string truncated = result.RawResponse.Length > 500
                                ? result.RawResponse[..500] + "…"
                                : result.RawResponse;
                            Log($"    Raw response: {truncated}");
                        }
                    }
                }

                PbarProgress.Value = 100;
                TxtProgress.Text = "Done.";

                if (ackIds.Count > 0)
                {
                    Log("Checking downstream acknowledgement status…");
                    Log("  (HTTP 200 above only means AIS stored the chunk, not that ERM consumed it.)");

                    foreach (var ackId in ackIds)
                    {
                        var ack = await api.GetAcknowledgementAsync(ackId);
                        Log(ack.IsSuccess
                            ? $"  {ackId}: {ack.RawResponse}"
                            : $"  {ackId}: status unavailable — {ack.ErrorMessage} {ack.RawResponse}");
                    }
                }

                string summary = $"Publish complete: {success} succeeded, {failed} failed " +
                                 $"(out of {convResult.Payloads.Count} total).";
                Log(summary);

                MessageBox.Show(summary, "Publish Complete",
                    MessageBoxButton.OK,
                    failed > 0 ? MessageBoxImage.Warning : MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                LogError($"Publish error: {ex.Message}");
                if (ex.InnerException != null)
                    LogError($"  Inner: {ex.InnerException.Message}");

                MessageBox.Show($"Error during publish:\n\n{ex.Message}",
                    "Publish Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                SetBusy(false);
            }
        }

        // ──────────────────────────────────────────────────────────────
        //  CLEAR LOG
        // ──────────────────────────────────────────────────────────────

        private void BtnClearLog_Click(object sender, RoutedEventArgs e)
        {
            TxtLog.Clear();
        }
    }
}
