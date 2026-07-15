using Apache.Arrow;
using Apache.Arrow.Types;
using Fabricator.Bridge;

namespace Fabricator.SamplePlugin;

/// <summary>
/// A sample third-party plugin backend. It exposes no catalog (ATTACH throws) — it exists purely to contribute
/// a connection-free GLOBAL scalar function, demonstrating that a plugin dropped into an <c>FABRICATOR_PLUGIN_DIR</c>
/// folder is discovered, its <see cref="IBackend"/> registered, and its global functions surfaced — with no
/// change to the bridge or any ABI. See docs/plugin-system.md.
/// </summary>
public sealed class SamplePluginBackend : IBackend
{
    public string Name => "sampleplugin";

    public IEnumerable<IScalarFunction> GlobalScalarFunctions => new IScalarFunction[] { new PlugGreetFunction() };

    public string BuildConnectionString(string secretType, IReadOnlyDictionary<string, string> fields,
                                        string baseConnString) =>
        throw new NotSupportedException("sampleplugin: global functions only (no catalog).");

    public IBackendCatalog OpenCatalog(string connectionString, string optionsJson) =>
        throw new NotSupportedException("sampleplugin: global functions only (no catalog).");
}

/// <summary>A connection-free global scalar: <c>plug_greet(name) -&gt; 'Hello, &lt;name&gt; (from plugin)'</c>.
/// Authored against the shared <see cref="IScalarFunction"/> contract; runs over Apache.Arrow like any other.</summary>
internal sealed class PlugGreetFunction : IScalarFunction
{
    public string Name => "plug_greet";
    public Schema Parameters => new(new[] { new Field("name", StringType.Default, nullable: true) }, metadata: null);
    public Field Result => new("greeting", StringType.Default, nullable: true);

    public IArrowArray Invoke(RecordBatch args)
    {
        var names = (StringArray)args.Column(0);
        var b = new StringArray.Builder().Reserve(args.Length);
        for (int i = 0; i < args.Length; i++)
        {
            b.Append(names.IsNull(i) ? "Hello, stranger (from plugin)" : $"Hello, {names.GetString(i)} (from plugin)");
        }
        return b.Build();
    }
}
