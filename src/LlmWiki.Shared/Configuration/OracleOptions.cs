namespace LlmWiki.Shared.Configuration;

/// <summary>Strongly-typed Oracle settings. Bound from env vars; never logged.</summary>
public sealed class OracleOptions
{
    public const string SectionName = "Oracle";

    /// <summary>ODP.NET connection string (env: ORACLE_CONNECTION_STRING).</summary>
    public string ConnectionString { get; set; } = string.Empty;
}
