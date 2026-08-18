namespace Fabricator.Bridge;

/// <summary>
/// A plugin backend that claims <c>sqlserver</c> — a name the first-party provider already owns.
/// </summary>
/// <remarks>
/// <para>Its ONLY purpose is to be refused. <c>BackendRegistry.Add</c> is a plain dictionary assignment, so
/// before 2026-08-18 this assembly would have SILENTLY REPLACED <c>Fabricator.SqlServer</c> in the registry
/// and the scan would have reported it as an ordinary <c>loaded</c> row: every later
/// <c>ATTACH … PROVIDER 'sqlserver'</c> would go somewhere the user never chose, with nothing anywhere saying
/// so.</para>
/// <para>⚠ It must stay a SEPARATE ASSEMBLY. A provider-name collision needs two assemblies claiming one
/// name, and nothing in an archive manifest, a plugin root or an install argument can manufacture that — the
/// name comes from the compiled type. Folding it into <c>Fabricator.SamplePlugin</c> would not work either:
/// the refusal is all-or-nothing per assembly, so that plugin would stop registering and
/// <c>verify_plugin</c> would fail.</para>
/// <para>It is deliberately NOT packaged as an installable archive. The scan refuses it wherever it is found,
/// so pointing a plugin root at it is enough, and an archive would only add a second thing to keep in step.</para>
/// </remarks>
public sealed class CollidingBackend : IBackend
{
    /// <summary>The first-party SQL Server provider's name.</summary>
    public string Name => "sqlserver";

    public string BuildConnectionString(string secretType, IReadOnlyDictionary<string, string> fields,
                                        string baseConnString) =>
        throw new NotSupportedException("colliding test fixture: never reached — registration is refused.");

    public IBackendCatalog OpenCatalog(string connectionString, string optionsJson) =>
        throw new NotSupportedException("colliding test fixture: never reached — registration is refused.");
}
