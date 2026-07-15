namespace Fabricator.Bridge;

/// <summary>DuckDB-side type of a secret field (maps to VARCHAR / INTEGER / BOOLEAN).</summary>
public enum SecretFieldType
{
    Varchar,
    Integer,
    Boolean,
}

/// <summary>
/// A field a provider declares for its secret type (see <see cref="IBackend.SecretType"/> /
/// <see cref="IBackend.SecretFields"/>). The host registers these generically as the <c>CREATE SECRET</c>
/// named parameters at extension load (via the <c>list_secret_fields</c> ABI) and stores the supplied values
/// (redacting the marked ones); the provider reads them when it assembles the connection string
/// (<see cref="IBackend.BuildConnectionString"/>). The provider-agnostic core thus names no secret field. See
/// docs/provider-extensibility.md §2.
/// </summary>
/// <param name="Name">The field name = <c>CREATE SECRET</c> parameter, e.g. <c>host</c>.</param>
/// <param name="Type">DuckDB type of the value.</param>
/// <param name="Redact">Whether the stored value is redacted in <c>duckdb_secrets()</c> (e.g. password, token).</param>
public sealed record SecretField(
    string Name,
    SecretFieldType Type = SecretFieldType.Varchar,
    bool Redact = false);
