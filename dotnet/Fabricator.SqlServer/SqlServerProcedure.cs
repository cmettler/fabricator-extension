using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Apache.Arrow;
using Apache.Arrow.Ipc;
using Fabricator.Bridge;
using Microsoft.Data.SqlClient;

namespace Fabricator.SqlServer;

// A discovered stored procedure exposed as a custom table function (ICatalogTableFunction): resolved as
// `SELECT * FROM db.schema.proc(name := val, ...)`. Bind resolves the output schema (OUTPUT params + the
// integer RETURN value as flat columns, else the first result set via sp_describe_first_result_set) and
// Execute runs the EXEC over the supplied NAMED parameters (the args batch's field names are the proc's
// parameter names; omitted optionals fall back to the proc's own DEFAULT). No pushdown — a proc's EXEC isn't
// inline-wrappable, so DuckDB projects + filters above the scan. A top-level internal class (like
// SqlServerScalarFunction / SqlServerTvfEach) holding a SqlServerCatalog and reaching its internal helpers.
internal sealed class SqlServerProcedure : ICatalogTableFunction
{
    private readonly SqlServerCatalog _owner;
    private readonly string _schema;
    private readonly string _func;

    public SqlServerProcedure(SqlServerCatalog owner, string schemaName, string functionName)
    {
        _owner = owner;
        _schema = schemaName;
        _func = functionName;
    }

    public string SchemaName => _schema;
    public string Name => _func;

    // The proc's input parameters (shared param-schema query; field names are the de-@'d parameter names).
    public Schema Parameters
    {
        get
        {
            using var s = _owner.RoutineParamSchemaQuery(_schema, _func);
            return s.Schema;
        }
    }

    public IArrowTableFunctionBinding Bind(RecordBatch args) => new Binding(_owner, _schema, _func, args);

    private sealed class Binding : IArrowTableFunctionBinding
    {
        private readonly SqlServerCatalog _owner;
        private readonly string _schema;
        private readonly string _func;
        private readonly RecordBatch? _args; // null only for the schema-probe path (output schema is arg-independent)
        private readonly List<(string name, string sqlType)> _outputs; // OUTPUT params (empty => result-set proc)

        public Binding(SqlServerCatalog owner, string schemaName, string functionName, RecordBatch args)
        {
            _owner = owner;
            _schema = schemaName;
            _func = functionName;
            _args = args;
            // Output = OUTPUT params (+ the integer RETURN value) as flat columns; else the first result set.
            _outputs = owner.ProcOutputParams(schemaName, functionName);
            List<(string name, string sqlType)> cols;
            if (_outputs.Count > 0)
            {
                cols = new List<(string name, string sqlType)>(_outputs) { ("return_value", "int") };
            }
            else
            {
                cols = owner.ProcResultColumns(schemaName, functionName);
            }
            if (cols.Count == 0)
            {
                throw new ArgumentException(
                    $"fabricator: '{schemaName}.{functionName}' has no describable result set");
            }
            var sb = new StringBuilder("SELECT ");
            for (int i = 0; i < cols.Count; i++)
            {
                sb.Append(i > 0 ? ", " : "").Append("CAST(NULL AS ").Append(cols[i].sqlType).Append(") AS ")
                  .Append(SqlServerCatalog.Quote(cols[i].name));
            }
            sb.Append(" WHERE 1 = 0");
            using var schemaStream = owner.ExecuteQuery(sb.ToString());
            OutputSchema = schemaStream.Schema;
        }

        public Schema OutputSchema { get; }

        // A proc's EXEC isn't inline-wrappable, so DuckDB re-applies projection (by name) + filters above the scan.
        public bool SupportsFilterPushdown => false;
        public bool SupportsProjectionPushdown => false;

        // Dispose eagerly in a plain method, then delegate — see the lifetime note in StaticTableFunction.Execute.
        public IAsyncEnumerable<RecordBatch> Execute(TableFunctionScan scan, CancellationToken ct = default)
        {
            scan.FilterValues?.Dispose(); // no pushdown
            return Rows(ct);
        }

        private async IAsyncEnumerable<RecordBatch> Rows([EnumeratorCancellation] CancellationToken ct)
        {
            // Supplied named args: the 1-row args batch's field names are the proc's parameter names.
            var argParams = new List<SqlParameter>();
            var assignments = new List<string>(); // @<paramName> = @p<c>
            var fields = _args?.Schema.FieldsList ?? (IReadOnlyList<Field>)System.Array.Empty<Field>();
            for (int c = 0; c < fields.Count; c++)
            {
                var pn = $"@p{c}";
                argParams.Add(new SqlParameter(pn, ArrowValueReader.ReadScalar(_args!.Column(c), 0) ?? (object)DBNull.Value));
                assignments.Add($"@{fields[c].Name} = {pn}");
            }

            var qualified = SqlServerCatalog.Quote(_schema) + "." + SqlServerCatalog.Quote(_func);
            string sql;
            if (_outputs.Count == 0)
            {
                // No OUTPUT params: stream the proc's first result set.
                sql = assignments.Count > 0 ? $"EXEC {qualified} {string.Join(", ", assignments)}" : $"EXEC {qualified}";
            }
            else
            {
                // OUTPUT params: capture them + the integer RETURN value via T-SQL locals and SELECT them as a
                // flat 1-row result set (the proc's own result set is ignored; no Direction=Output timing caveat).
                var decls = _outputs.Select(o => $"@{o.name} {o.sqlType}").ToList();
                decls.Add("@_rv int");
                foreach (var o in _outputs)
                {
                    assignments.Add($"@{o.name} = @{o.name} OUTPUT");
                }
                var selects = _outputs.Select(o => $"@{o.name} AS {SqlServerCatalog.Quote(o.name)}").ToList();
                selects.Add("@_rv AS [return_value]");
                sql = $"DECLARE {string.Join(", ", decls)}; EXEC @_rv = {qualified} {string.Join(", ", assignments)}; " +
                      $"SELECT {string.Join(", ", selects)};";
            }

            using var stream = _owner.ExecuteQuery(sql, argParams);
            while (true)
            {
                var b = await stream.ReadNextRecordBatchAsync();
                if (b is null)
                {
                    break;
                }
                yield return b;
            }
        }

        public void Dispose() => _args?.Dispose();
    }
}
