using Newtonsoft.Json;

namespace WPIntegrationTool.Models
{
    /// <summary>
    /// The top-level payload sent to the data API endpoint.
    /// </summary>
    public class PublishPayload
    {
        public string ChunkId { get; set; } = "1";
        public string Path { get; set; } = string.Empty;
        public string TableName { get; set; } = string.Empty;
        public string Context { get; set; } = string.Empty;   // JSON-encoded string
        public string Metadata { get; set; } = string.Empty;   // JSON-encoded string
        public string Data { get; set; } = string.Empty;       // JSON-encoded array string
    }

    /// <summary>
    /// The nested "Context" object serialised as an escaped JSON string inside the payload.
    /// </summary>
    public class ContextPayload
    {
        [JsonProperty("objectContext")]
        public string ObjectContext { get; set; } = "WP";

        [JsonProperty("objectName")]
        public string ObjectName { get; set; } = string.Empty;

        [JsonProperty("MessageId")]
        public string MessageId { get; set; } = string.Empty;

        [JsonProperty("sentFromId")]
        public string SentFromId { get; set; } = string.Empty;

        [JsonProperty("message")]
        public string Message { get; set; } = string.Empty;     // another nested JSON string

        [JsonProperty("topic")]
        public string Topic { get; set; } = "update";

        [JsonProperty("sentFrom")]
        public string SentFrom { get; set; } = "WORKPACKS";

        [JsonProperty("sentTo")]
        public string SentTo { get; set; } = "AvevaErm";

        [JsonProperty("date")]
        public string Date { get; set; } = string.Empty;
    }

    /// <summary>
    /// The "message" field inside ContextPayload – itself a JSON-encoded string.
    /// </summary>
    public class MessagePayload
    {
        public string Count { get; set; } = "0";
        public string MessageType { get; set; } = "Update";
        public string ServiceInfraStructureId { get; set; } = string.Empty;
        public string Project { get; set; } = "WP";
        public string SentFromApp { get; set; } = "WORKPACKS";
        public string SentFromUser { get; set; } = string.Empty;
        public string SentTime { get; set; } = string.Empty;
        public string SentToApp { get; set; } = string.Empty;
        public string MessageBody { get; set; } = "Activity published from workpacks";
    }

    /// <summary>
    /// Metadata payload.
    /// </summary>
    public class MetadataPayload
    {
        public string IndexOfChunk { get; set; } = "1";
        public string TotalNumberOfChunk { get; set; } = "1";
    }

    /// <summary>
    /// Defines which publish type was selected.
    /// </summary>
    public enum PublishType
    {
        CWPIWP,
        ActualMTO
    }

    /// <summary>
    /// Authentication mode.
    /// </summary>
    public enum AuthType
    {
        NTLM,
        OIDC
    }

    /// <summary>
    /// Holds user-entered connection / identity settings.
    /// </summary>
    public class ConnectionSettings
    {
        // ── Common ──────────────────────────────────────────────────
        public AuthType AuthType { get; set; } = AuthType.NTLM;
        public string BaseUrl { get; set; } = string.Empty;          // e.g. https://inhydvmw509:60930
        public string DatasourceName { get; set; } = "ERM";
        public string SentFromUser { get; set; } = string.Empty;     // e.g. deepika.akula@aveva.com
        public string SentFromId { get; set; } = string.Empty;       // e.g. deepika.akula
        public string ProjectCode { get; set; } = "WP";
        public int ChunkSize { get; set; } = 500;                    // rows per chunk
        public bool IgnoreSslErrors { get; set; } = true;

        // ── NTLM-specific ──────────────────────────────────────────
        public string Username { get; set; } = string.Empty;
        public string Password { get; set; } = string.Empty;
        public string Domain { get; set; } = string.Empty;

        // ── OIDC-specific (Client Credentials flow) ────────────────
        public string OidcAuthority { get; set; } = "https://signin.test-connect.aveva.com/";
        public string OidcAudience { get; set; } = "https://serviceapplications.test-connect.aveva.com";
        public string OidcClientId { get; set; } = string.Empty;
        public string OidcClientSecret { get; set; } = string.Empty;
        public string OidcScopes { get; set; } = "connect";
    }
}
