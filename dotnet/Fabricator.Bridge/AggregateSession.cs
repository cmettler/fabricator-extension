// Copyright (c) Christoph Mettler and contributors.
// SPDX-License-Identifier: Apache-2.0
// See LICENSE in the project root for license information.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using Apache.Arrow;
using Apache.Arrow.Ipc;
using Apache.Arrow.Types;

namespace Fabricator.Bridge;

/// <summary>
/// The provider-agnostic session for a custom aggregate (UDAF) — maps DuckDB's per-group <c>int64</c> state
/// ids to live C# accumulators (<see cref="IAggregateState"/>). The C++ aggregate callbacks marshal
/// <c>[id ++ args]</c> (update), <c>[target_id, source_id]</c> (combine), and <c>[id]</c> (finalize/destroy)
/// over the <c>agg_*</c> ABI; this routes them. A given id is touched by one thread at a time (DuckDB
/// partitions per thread; combine is partition-disjoint), so a <see cref="ConcurrentDictionary{TKey,TValue}"/>
/// suffices with no per-accumulator lock. An absent id =&gt; a fresh accumulator =&gt; the empty-group value (a
/// state DuckDB initialized but never updated). Used by both catalog-bound aggregates and connection-free
/// global aggregates (the function comes in as the base <see cref="IAggregateFunction"/>).
/// </summary>
public sealed class AggregateSession : IAggregateSession
{
    private readonly IAggregateFunction _fn;
    private readonly Schema _updateSchema;
    private readonly ConcurrentDictionary<long, IAggregateState> _states = new();

    public AggregateSession(IAggregateFunction fn)
    {
        _fn = fn;
        var fields = new List<Field>(fn.Parameters.FieldsList.Count + 1)
        {
            new Field("state_id", Int64Type.Default, nullable: false),
        };
        fields.AddRange(fn.Parameters.FieldsList);
        _updateSchema = new Schema(fields, null);
    }

    public Schema UpdateSchema => _updateSchema;

    private IAggregateState StateFor(long id) => _states.GetOrAdd(id, _ => _fn.CreateState());

    public void Update(RecordBatch idPlusArgs)
    {
        using (idPlusArgs)
        {
            GroupAndApply(idPlusArgs, StateFor);
        }
    }

    // Groups the [key ++ args] batch by column 0 and folds each group's rows into stateFor(key).Update(...).
    // Shared by the fast Update (key = state id) and UpdateSpill (key = group slot). Does NOT dispose the
    // batch (the caller owns it).
    private void GroupAndApply(RecordBatch keyPlusArgs, Func<long, IAggregateState> stateFor)
    {
        int rows = keyPlusArgs.Length;
        if (rows == 0)
        {
            return;
        }
        var keys = (Int64Array)keyPlusArgs.Column(0);
        var argCols = new IArrowArray[keyPlusArgs.ColumnCount - 1];
        for (int c = 0; c < argCols.Length; c++)
        {
            argCols[c] = keyPlusArgs.Column(c + 1);
        }

        // Fast path: the whole chunk is one group — always true for the ungrouped simple_update path, and
        // for GROUP BY chunks holding a single group — so the arg columns pass through with no gather. (The
        // wrapper batch is NOT disposed: its columns are owned by keyPlusArgs.)
        long first = keys.GetValue(0) ?? 0;
        bool single = true;
        for (int i = 1; i < rows; i++)
        {
            if ((keys.GetValue(i) ?? 0) != first)
            {
                single = false;
                break;
            }
        }
        if (single)
        {
            stateFor(first).Update(new RecordBatch(_fn.Parameters, argCols, rows));
            return;
        }

        // General path: group row indices by key, gather each group's arg rows, update once per group.
        var groups = new Dictionary<long, List<int>>();
        for (int i = 0; i < rows; i++)
        {
            long key = keys.GetValue(i) ?? 0;
            if (!groups.TryGetValue(key, out var list))
            {
                groups[key] = list = new List<int>();
            }
            list.Add(i);
        }
        foreach (var kv in groups)
        {
            var gathered = new IArrowArray[argCols.Length];
            for (int c = 0; c < argCols.Length; c++)
            {
                gathered[c] = GatherRows(argCols[c], kv.Value);
            }
            using var batch = new RecordBatch(_fn.Parameters, gathered, kv.Value.Count);
            stateFor(kv.Key).Update(batch);
        }
    }

    public void Combine(RecordBatch targetSource)
    {
        using (targetSource)
        {
            var target = (Int64Array)targetSource.Column(0);
            var source = (Int64Array)targetSource.Column(1);
            for (int i = 0; i < targetSource.Length; i++)
            {
                // A source state that was never updated is absent => empty, nothing to merge.
                if (_states.TryGetValue(source.GetValue(i) ?? 0, out var src))
                {
                    StateFor(target.GetValue(i) ?? 0).Combine(src);
                }
            }
        }
    }

    public IArrowArrayStream Finalize(RecordBatch ids)
    {
        var resultSchema = new Schema(new[] { _fn.Result }, null);
        using (ids)
        {
            var idCol = (Int64Array)ids.Column(0);
            int rows = ids.Length;
            var values = new object?[rows];
            for (int i = 0; i < rows; i++)
            {
                // Absent id => a fresh accumulator => the empty-group value. Do NOT insert it (finalize
                // must not grow the map), just finalize a throwaway.
                var state = _states.TryGetValue(idCol.GetValue(i) ?? 0, out var s) ? s : _fn.CreateState();
                values[i] = state.Finalize();
            }
            var col = BuildResultColumn(_fn.Result.DataType, values);
            return new InMemoryArrayStream(resultSchema, new[] { new RecordBatch(resultSchema, new[] { col }, rows) });
        }
    }

    public void Destroy(RecordBatch ids)
    {
        using (ids)
        {
            var idCol = (Int64Array)ids.Column(0);
            for (int i = 0; i < ids.Length; i++)
            {
                _states.TryRemove(idCol.GetValue(i) ?? 0, out _);
            }
        }
    }

    public void Close() => _states.Clear();

    // ---- Spillable mode: state lives as bytes in DuckDB's blob (not in _states); each call rehydrates a
    // transient accumulator, applies, and re-serializes. The serialized-state column is a single BLOB. ----
    private static readonly Schema StateSchema =
        new(new[] { new Field("state", BinaryType.Default, nullable: true) }, null);

    private IAggregateState LoadOrFresh(BinaryArray states, int i)
    {
        var s = _fn.CreateState();
        if (!states.IsNull(i))
        {
            s.Load(states.GetBytes(i));
        }
        return s;
    }

    private static IArrowArrayStream StateStream(IReadOnlyList<IAggregateState> states)
    {
        var b = new BinaryArray.Builder().Reserve(states.Count);
        foreach (var s in states)
        {
            b.Append((ReadOnlySpan<byte>)s.Serialize());
        }
        return new InMemoryArrayStream(StateSchema,
                                       new[] { new RecordBatch(StateSchema, new IArrowArray[] { b.Build() }, states.Count) });
    }

    public IArrowArrayStream UpdateSpill(RecordBatch groupStates, RecordBatch slotPlusArgs)
    {
        using (groupStates)
        using (slotPlusArgs)
        {
            var statesArr = (BinaryArray)groupStates.Column(0);
            int g = groupStates.Length;
            var states = new IAggregateState[g];
            for (int i = 0; i < g; i++)
            {
                states[i] = LoadOrFresh(statesArr, i);
            }
            // Reuse the fast-path grouping: column 0 is the dense group slot (0..g-1) instead of a state id.
            GroupAndApply(slotPlusArgs, slot => states[(int)slot]);
            return StateStream(states);
        }
    }

    public IArrowArrayStream CombineSpill(RecordBatch targetStates, RecordBatch batch)
    {
        using (targetStates)
        using (batch)
        {
            var tArr = (BinaryArray)targetStates.Column(0);
            int g = targetStates.Length;
            var targets = new IAggregateState[g];
            for (int i = 0; i < g; i++)
            {
                targets[i] = LoadOrFresh(tArr, i);
            }
            // [int64 slot, BLOB source] — merge each source into targets[slot]; a target may repeat (the
            // window segment-tree combines several nodes into one frame state), so we accumulate in place.
            var slots = (Int64Array)batch.Column(0);
            var srcArr = (BinaryArray)batch.Column(1);
            for (int i = 0; i < batch.Length; i++)
            {
                if (!srcArr.IsNull(i))
                {
                    targets[(int)(slots.GetValue(i) ?? 0)].Combine(LoadOrFresh(srcArr, i));
                }
            }
            return StateStream(targets);
        }
    }

    public IArrowArrayStream FinalizeSpill(RecordBatch states)
    {
        var resultSchema = new Schema(new[] { _fn.Result }, null);
        using (states)
        {
            var arr = (BinaryArray)states.Column(0);
            int n = states.Length;
            var values = new object?[n];
            for (int i = 0; i < n; i++)
            {
                values[i] = LoadOrFresh(arr, i).Finalize(); // NULL row => fresh => empty-group value
            }
            var col = BuildResultColumn(_fn.Result.DataType, values);
            return new InMemoryArrayStream(resultSchema, new[] { new RecordBatch(resultSchema, new[] { col }, n) });
        }
    }

    // Gathers the given row indices from one Arrow column into a fresh array (Apache.Arrow C# has no
    // take/gather). Supports the common aggregate-argument types; an exotic type throws clearly.
    private static IArrowArray GatherRows(IArrowArray src, List<int> rows)
    {
        switch (src)
        {
            case Int16Array a:
            {
                var b = new Int16Array.Builder().Reserve(rows.Count);
                foreach (var r in rows) { if (a.IsNull(r)) b.AppendNull(); else b.Append(a.Values[r]); }
                return b.Build();
            }
            case Int32Array a:
            {
                var b = new Int32Array.Builder().Reserve(rows.Count);
                foreach (var r in rows) { if (a.IsNull(r)) b.AppendNull(); else b.Append(a.Values[r]); }
                return b.Build();
            }
            case Int64Array a:
            {
                var b = new Int64Array.Builder().Reserve(rows.Count);
                foreach (var r in rows) { if (a.IsNull(r)) b.AppendNull(); else b.Append(a.Values[r]); }
                return b.Build();
            }
            case FloatArray a:
            {
                var b = new FloatArray.Builder().Reserve(rows.Count);
                foreach (var r in rows) { if (a.IsNull(r)) b.AppendNull(); else b.Append(a.Values[r]); }
                return b.Build();
            }
            case DoubleArray a:
            {
                var b = new DoubleArray.Builder().Reserve(rows.Count);
                foreach (var r in rows) { if (a.IsNull(r)) b.AppendNull(); else b.Append(a.Values[r]); }
                return b.Build();
            }
            case BooleanArray a:
            {
                var b = new BooleanArray.Builder();
                foreach (var r in rows) { var v = a.GetValue(r); if (v is null) b.AppendNull(); else b.Append(v.Value); }
                return b.Build();
            }
            case StringArray a:
            {
                var b = new StringArray.Builder();
                foreach (var r in rows) { if (a.IsNull(r)) b.AppendNull(); else b.Append(a.GetString(r)); }
                return b.Build();
            }
            case Decimal128Array a:
            {
                var b = new Decimal128Array.Builder((Decimal128Type)a.Data.DataType).Reserve(rows.Count);
                foreach (var r in rows) { var v = a.GetValue(r); if (v is null) b.AppendNull(); else b.Append(v.Value); }
                return b.Build();
            }
            default:
                throw new NotSupportedException(
                    $"fabricator: custom aggregate argument type {src.Data.DataType} is not supported");
        }
    }

    // Builds the one-column result array (typed as the aggregate's Result) from per-group boxed values
    // (null => SQL NULL). Convert.* tolerates the author returning any compatible boxed numeric type.
    private static IArrowArray BuildResultColumn(IArrowType type, IReadOnlyList<object?> values)
    {
        int n = values.Count;
        switch (type)
        {
            case Int16Type:
            {
                var b = new Int16Array.Builder().Reserve(n);
                foreach (var v in values) { if (v is null) b.AppendNull(); else b.Append(Convert.ToInt16(v)); }
                return b.Build();
            }
            case Int32Type:
            {
                var b = new Int32Array.Builder().Reserve(n);
                foreach (var v in values) { if (v is null) b.AppendNull(); else b.Append(Convert.ToInt32(v)); }
                return b.Build();
            }
            case Int64Type:
            {
                var b = new Int64Array.Builder().Reserve(n);
                foreach (var v in values) { if (v is null) b.AppendNull(); else b.Append(Convert.ToInt64(v)); }
                return b.Build();
            }
            case FloatType:
            {
                var b = new FloatArray.Builder().Reserve(n);
                foreach (var v in values) { if (v is null) b.AppendNull(); else b.Append(Convert.ToSingle(v)); }
                return b.Build();
            }
            case DoubleType:
            {
                var b = new DoubleArray.Builder().Reserve(n);
                foreach (var v in values) { if (v is null) b.AppendNull(); else b.Append(Convert.ToDouble(v)); }
                return b.Build();
            }
            case BooleanType:
            {
                var b = new BooleanArray.Builder();
                foreach (var v in values) { if (v is null) b.AppendNull(); else b.Append(Convert.ToBoolean(v)); }
                return b.Build();
            }
            case StringType:
            {
                var b = new StringArray.Builder();
                foreach (var v in values) { if (v is null) b.AppendNull(); else b.Append((string)v); }
                return b.Build();
            }
            case Decimal128Type dt:
            {
                var b = new Decimal128Array.Builder(dt).Reserve(n);
                foreach (var v in values) { if (v is null) b.AppendNull(); else b.Append(Convert.ToDecimal(v)); }
                return b.Build();
            }
            default:
                throw new NotSupportedException($"fabricator: custom aggregate result type {type} is not supported");
        }
    }
}
