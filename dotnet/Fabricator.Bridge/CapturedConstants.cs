// Copyright (c) Christoph Mettler and contributors.
// SPDX-License-Identifier: Apache-2.0
// See LICENSE in the project root for license information.

using System.IO;
using Apache.Arrow;
using Apache.Arrow.Ipc;
using Apache.Arrow.Types;

namespace Fabricator.Bridge;

/// <summary>
/// The BIND-TIME CONSTANT channel for lateral functions — the host half of <see cref="Params.Constant"/>.
/// DuckDB's binder rewrites EVERY argument of a table-in-out call into the synthesized input relation
/// (bind_table_function.cpp), so a lateral function's bind receives no argument VALUES at all — only the
/// input relation's schema, plus (in the all-constant literal shape) the folded parameter values. The one
/// channel that survives that rewrite in BOTH call shapes is the column TYPE, and that is what
/// <c>const_arg(…)</c> rides: its per-call-site bind (ABI v80) reads the constant, parks it here, and
/// resolves its RESULT type to a single-member struct whose member NAME is the key
/// (<c>__fab_const_&lt;md5&gt;</c>). The host's lateral bind then recovers the VALUE from this registry and
/// hands it to <see cref="ILateralFunction.Bind"/> in <c>args</c>, under the parameter's declared name — so
/// an author declares <c>Params.Constant("fields")</c> and reads a typed value, knowing nothing of the
/// smuggling.
/// </summary>
/// <remarks>
/// <para><b>The caller needs the wrapper ONLY in the correlated shape.</b> In the literal shape (every
/// argument constant) the folded value itself reaches the lateral bind through the C++ side
/// (<c>input.inputs</c>), so <c>SELECT * FROM f(7, 'x,y')</c> works bare; in the correlated shape
/// (<c>FROM t, f(t.a, const_arg('x,y'))</c>) only the type crosses, which is what the wrapper exists for.
/// A bare constant in a correlated call is refused with a message naming the wrapper.</para>
/// <para><b>LIFETIME: an entry is removable only when it is CONSUMED *and* UNREFERENCED — neither signal
/// alone is correct, and that is MEASURED (the sample-plugin prototype of this mechanism), not taste.</b>
/// The two call shapes bracket the capture scalar's binding differently: correlated keeps the bound scalar
/// in the synthesized subquery until PLAN teardown, so its Dispose fires AFTER the consumer's bind read the
/// entry; the literal shape FOLDS the argument into a Value and DISCARDS the bound expression, so Dispose
/// fires BEFORE the consumer's bind. Hence: <see cref="Store"/> takes a reference, the binding's Dispose
/// releases it, the consumer's <see cref="TryConsume"/> marks it consumed — removal happens at whichever
/// event sees (consumed &amp;&amp; refcount == 0). Every re-bind (view, EXPLAIN, prepared re-execute — DuckDB
/// re-binds each EXECUTE) re-runs the scalar bind and repopulates before the consumer looks, so eviction can
/// never be observed by a healthy statement; an entry no consumer ever reads lingers zero-referenced until
/// the &gt;128 backstop purge, which can only ever hit zero-referenced entries.</para>
/// <para><b>The stored value is an OWNED copy.</b> The constant arrives as an Arrow array backed by native
/// memory the host releases after the bind, so retaining it would be a use-after-free waiting for a GC race.
/// One Arrow IPC round-trip both produces fully managed buffers AND yields the canonical bytes the key is
/// hashed from — same value, same construction path ⇒ same key, which keeps the CONSISTENT contract (equal
/// inputs, equal result type) and makes a double Store idempotent via the refcount.</para>
/// </remarks>
internal static class CapturedConstants
{
    /// <summary>The member-name prefix that makes a capture struct unambiguous — a user's own single-member
    /// struct constant can never be mistaken for one. Matches the <c>__fab_</c> convention of
    /// <see cref="LateralSessionRunner.OriginColumn"/>.</summary>
    internal const string MemberPrefix = "__fab_const_";

    private sealed class Entry
    {
        public required Field Field { get; init; }
        public required RecordBatch Owned { get; init; }
        public int RefCount;
        public bool Consumed;
    }

    private static readonly Dictionary<string, Entry> Values = new();
    private static readonly object Gate = new();

    /// <summary>Live entry count — a test observable, surfaced by no function today (assert it from managed
    /// tests if ever needed; the SQL-visible probe lived in the sample-plugin prototype).</summary>
    internal static int Count
    {
        get { lock (Gate) { return Values.Count; } }
    }

    /// <summary>Parks one captured constant (a 1-row array) and takes a reference; returns the key that
    /// becomes the capture struct's member name. Pair with <see cref="Release"/> from the binding's
    /// Dispose.</summary>
    internal static string Store(IArrowArray constant)
    {
        var field = new Field("value", constant.Data.DataType, nullable: true);
        var schema = new Schema(new[] { field }, metadata: null);
        using var ms = new MemoryStream();
        using (var writer = new ArrowStreamWriter(ms, schema, leaveOpen: true))
        {
            writer.WriteRecordBatch(new RecordBatch(schema, new[] { constant }, constant.Length));
            writer.WriteEnd();
        }
        var key = MemberPrefix + Convert.ToHexString(
            System.Security.Cryptography.MD5.HashData(ms.GetBuffer().AsSpan(0, (int)ms.Length))).ToLowerInvariant();
        ms.Position = 0;
        using var reader = new ArrowStreamReader(ms);
        var owned = reader.ReadNextRecordBatch()
            ?? throw new InvalidOperationException("const_arg: IPC round-trip of the captured constant failed");
        lock (Gate)
        {
            if (Values.Count > 128)
            {
                // Backstop for captures no consumer ever reads. Zero-referenced entries only, so a live
                // correlated bind (which holds a reference until plan teardown) can never lose its entry here.
                foreach (var stale in Values.Where(kv => kv.Value.RefCount <= 0).Select(kv => kv.Key).ToList())
                {
                    Values.Remove(stale);
                }
            }
            if (Values.TryGetValue(key, out var e))
            {
                e.RefCount++;
            }
            else
            {
                Values[key] = new Entry { Field = field, Owned = owned, RefCount = 1 };
            }
        }
        return key;
    }

    /// <summary>Drops one reference; the entry leaves the map once it is ALSO consumed (the literal shape
    /// disposes before its consumer binds, so an unconsumed entry must survive its release — see the
    /// lifecycle box). Tolerates an unknown key: a Dispose path must not throw.</summary>
    internal static void Release(string key)
    {
        lock (Gate)
        {
            if (Values.TryGetValue(key, out var e))
            {
                e.RefCount--;
                if (e.RefCount <= 0 && e.Consumed)
                {
                    Values.Remove(key);
                }
            }
        }
    }

    private static bool TryConsume(string key, out IArrowArray value)
    {
        lock (Gate)
        {
            if (Values.TryGetValue(key, out var e))
            {
                e.Consumed = true;
                value = e.Owned.Column(0);
                if (e.RefCount <= 0)
                {
                    Values.Remove(key); // the batch itself stays alive through the reference just handed out
                }
                return true;
            }
        }
        value = null!;
        return false;
    }

    /// <summary>
    /// Resolves the <see cref="Params.Constant"/> arguments of one lateral call: for each declared constant
    /// parameter, the args column the C++ bind marshaled — a capture struct (correlated, or a wrapped
    /// literal) or the folded bare value (literal shape) — is replaced by the actual captured value, typed
    /// as the caller passed it. What the author's <see cref="ILateralFunction.Bind"/> then reads under the
    /// parameter's name is a plain 1-row typed column, whichever route it took.
    /// </summary>
    internal static RecordBatch? ResolveLateralArgs(string func, Schema parameters, RecordBatch? args)
    {
        var constants = new HashSet<string>(
            parameters.FieldsList.Where(f => Params.StyleOf(f) == ParamStyle.Constant).Select(f => f.Name),
            StringComparer.OrdinalIgnoreCase);
        if (constants.Count == 0)
        {
            return args;
        }
        if (args is null)
        {
            // The C++ bind marshals a column per declared constant, so this is a contract break, not user error.
            throw new InvalidOperationException(
                $"fabricator: lateral function '{func}' declares constant parameters but the bind received no "
                + "argument batch");
        }
        var fields = new List<Field>(args.ColumnCount);
        var columns = new List<IArrowArray>(args.ColumnCount);
        for (int i = 0; i < args.ColumnCount; i++)
        {
            var field = args.Schema.FieldsList[i];
            var col = args.Column(i);
            if (!constants.Contains(field.Name))
            {
                fields.Add(field);
                columns.Add(col);
                continue;
            }
            if (col.Data.DataType is StructType st && st.Fields.Count == 1 &&
                st.Fields[0].Name.StartsWith(MemberPrefix, StringComparison.Ordinal))
            {
                if (!TryConsume(st.Fields[0].Name, out var stored))
                {
                    // Unreachable for a healthy statement (see the lifecycle box); reachable only if a purge
                    // raced this exact window, so the message says re-run rather than blaming the SQL.
                    throw new InvalidOperationException(
                        $"fabricator: the constant captured for parameter '{field.Name}' of '{func}' expired "
                        + "before this bind read it — re-run the statement.");
                }
                fields.Add(new Field(field.Name, stored.Data.DataType, nullable: true));
                columns.Add(stored);
                continue;
            }
            if (col.Length < 1 || col.IsNull(0))
            {
                throw new NotSupportedException(
                    $"fabricator: parameter '{field.Name}' of '{func}' is a bind-time CONSTANT. Pass a "
                    + "non-NULL literal, or — in a correlated call, where a bare constant cannot reach the "
                    + $"bind — wrap it: const_arg(<value>).");
            }
            fields.Add(field); // a bare constant from the literal shape: already the typed value
            columns.Add(col);
        }
        return new RecordBatch(new Schema(fields, metadata: null), columns, 1);
    }
}

/// <summary>
/// <c>const_arg(&lt;constant of ANY type&gt;)</c> — the caller-side wrapper that carries a bind-time constant
/// into a lateral function's <see cref="Params.Constant"/> parameter through the correlated call shape:
/// <c>SELECT t.id, f.* FROM t, my_fn(t.a, const_arg('x,y'))</c>. See <see cref="CapturedConstants"/> for the
/// mechanism; in the literal (all-constant) shape the wrapper is unnecessary, though harmless.
/// </summary>
/// <remarks>
/// CONSISTENT on purpose (<see cref="IsVolatile"/> false): same constant ⇒ same result type and no side
/// effect an optimizer may observe, so DuckDB may fold the call — which only makes the execute side cheaper;
/// the TYPE, which is the channel, is fixed at bind either way. A NON-constant argument is refused at bind:
/// a runtime expression has no bind-time value to capture, and capturing its NULL placeholder would hand the
/// consumer a null that looks like a captured value. The runtime VALUE is an all-NULL struct — deliberately
/// worthless, because the payload travels through the registry, never the rows.
/// </remarks>
internal sealed class ConstArgFunction : IScalarFunction
{
    public string Name => "const_arg";

    public Schema Parameters =>
        new(new[] { Params.Positional("value", NullType.Default) }, metadata: null); // NullType => ANY

    public Field? Result => null;      // resolved per call site by Bind
    public bool IsVolatile => false;   // pure: same constant => same (type, value)

    // Unreachable: Bind never returns the default binding, and the host executes the BINDING's Invoke.
    public IArrowArray Invoke(RecordBatch args) =>
        throw new InvalidOperationException("const_arg: executed without a call-site binding");

    public IScalarFunctionBinding Bind(ScalarBindArgs args)
    {
        var arr = args.ConstantArray(0);
        if (arr is null || arr.Length < 1 || arr.IsNull(0))
        {
            throw new NotSupportedException(
                "const_arg: the argument must be a non-NULL constant — it is captured at BIND, so a runtime "
                + "expression has no value here to capture.");
        }
        var key = CapturedConstants.Store(arr);
        var structType = new StructType(new[] { new Field(key, StringType.Default, nullable: true) });
        return new Binding(key, new Field("captured", structType, nullable: true));
    }

    private sealed class Binding : IScalarFunctionBinding
    {
        private readonly Field _result;
        private string? _key;

        public Binding(string key, Field result)
        {
            _key = key;
            _result = result;
        }

        public Field? Result => _result;

        public IArrowArray Invoke(RecordBatch args)
        {
            // All-NULL struct of the batch's length: the value channel carries nothing on purpose.
            var n = args.Length;
            var child = new StringArray.Builder().Reserve(n);
            for (int i = 0; i < n; i++) { child.AppendNull(); }
            var validity = new ArrowBuffer.BitmapBuilder(n);
            validity.AppendRange(false, n);
            return new StructArray(_result.DataType, n, new IArrowArray[] { child.Build() },
                                   validity.Build(), nullCount: n);
        }

        /// <summary>THE RELEASE POINT: runs exactly once per call site, at plan teardown (correlated) or at
        /// the binder's fold-discard (literal shape) — see the lifecycle box on
        /// <see cref="CapturedConstants"/>.</summary>
        public void Dispose()
        {
            var key = System.Threading.Interlocked.Exchange(ref _key, null);
            if (key is not null)
            {
                CapturedConstants.Release(key);
            }
        }
    }
}
