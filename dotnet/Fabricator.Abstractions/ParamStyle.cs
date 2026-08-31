// Copyright (c) Christoph Mettler and contributors.
// SPDX-License-Identifier: Apache-2.0
// See LICENSE in the project root for license information.

using System;
using System.Collections.Generic;
using System.Linq;
using Apache.Arrow;
using Apache.Arrow.Types;

namespace Fabricator.Bridge;

/// <summary>How a declared parameter is passed at the call site.</summary>
public enum ParamStyle
{
    /// <summary>An ordinary positional argument — <c>f(1, 'x')</c>. The default when unflagged.</summary>
    Positional,

    /// <summary>A named argument — <c>f(recreate := true)</c>. Must follow every positional/table field.</summary>
    Named,

    /// <summary>
    /// A bind-time CONSTANT parameter of a LATERAL function: it occupies a positional slot (so the correlated
    /// call shape can spell it — DuckDB has no named-parameter syntax there), but its value is delivered to
    /// <see cref="ILateralFunction.Bind"/> in <c>args</c> under the parameter's name rather than arriving as a
    /// per-row input column. A bare constant works in BOTH call shapes: the literal shape folds it, and the
    /// correlated shape recovers it from the synthesized column's rendered expression text (parsed, bound as
    /// a constant, folded, type-checked — columns and volatiles refuse). Lateral functions only.
    /// </summary>
    Constant,

    /// <summary>The input TABLE of a table-in-out function — <c>f((SELECT …))</c>. At most one, positional.</summary>
    TableInput,

    /// <summary>
    /// A VARIADIC TAIL: the caller may pass any number of FURTHER arguments after the declared ones, each of
    /// which must implicitly cast to this field's type (the Arrow null type ⇒ DuckDB <c>ANY</c>, i.e.
    /// anything). It registers as DuckDB's <c>SimpleFunction::varargs</c>, so the fields BEFORE it are the
    /// MINIMUM arity and there is no maximum.
    /// </summary>
    /// <remarks>
    /// <para>At most one, and it must be the LAST POSITIONAL field — named parameters may follow, since they
    /// are a separate namespace. A declaration breaking either rule is refused where the signature is built,
    /// loudly, rather than silently reinterpreted.</para>
    /// <para>⚠ It changes what ARRIVES: the args batch of a variadic call is WIDER than
    /// <c>Parameters</c>. The fixed prefix keeps its declared positions; each further argument follows in
    /// call order, named <c>&lt;varargs-name&gt;_0</c>, <c>_1</c>, … Read the count from the batch, never
    /// from the declaration.</para>
    /// <para>⚠ Scalar, table and sqlgen functions only. Lateral and in-out functions are DEFERRED — for them
    /// the positional slots are the per-row INPUT COLUMNS, so a variadic tail is a variable-width wire and
    /// interacts with <see cref="Constant"/>'s trailing slots; neither question is answered yet.</para>
    /// <para>⚠ A LIST parameter covers the HOMOGENEOUS case already (<c>f(['a','b'])</c>) and needs none of
    /// this. What a variadic tail buys is HETEROGENEOUS, individually-typed arguments — DuckDB's own
    /// variadics are exactly that shape (<c>printf</c>, <c>concat_ws</c>, <c>struct_pack</c>).</para>
    /// </remarks>
    VarArgs,
}

/// <summary>
/// The parameter protocol: ONE schema per function, with each field's STYLE carried in its Arrow field
/// metadata. Absent metadata means positional, so an unflagged schema behaves exactly as before.
/// </summary>
/// <remarks>
/// <para>This replaced a split <c>Parameters</c> + <c>NamedParameters</c> pair (plus a third,
/// <c>InputSchema</c>, on in-out functions). The split forced every consumer to reconstruct one ordering
/// rule — "positions are Parameters ++ NamedParameters in declared order" — and a host that got the NULL
/// substitution off by one would corrupt a POSITIONAL value rather than raise an error. With one schema,
/// position IS declaration order and that class of bug cannot be written.</para>
/// <para><b>Ordering rules, and where they come from.</b> Named parameters must come last. That is not our
/// invention: DuckDB's binder enforces exactly it at CALL time — <i>"Unnamed parameters cannot come after
/// named parameters"</i> (<c>bind_table_function.cpp</c>) — so declaring otherwise would produce a function
/// nobody can call. We check it at DECLARATION time so the author learns immediately.</para>
/// <para><b>Table input.</b> At most one, and DuckDB enforces that too (<i>"Table function can have at most
/// one subquery parameter"</i>). It is POSITIONAL-only: the binder's named-parameter path sets the argument
/// name and then the subquery branch ignores it, so <c>f(t := (SELECT …))</c> silently binds as the
/// positional table argument — a name there would be a lie. It may sit BETWEEN positionals: DuckDB pushes a
/// placeholder value for the subquery slot (<c>parameters.emplace_back()</c>), so following positions keep
/// their natural index.</para>
/// <para><b>Type of a table-input field.</b> Ignored today — DuckDB only ever accepts
/// <c>LogicalType::TABLE</c> there, so validating the input's COLUMNS would be a bind-time check of our own.
/// Declare the expected columns anyway: they cost nothing now and are the only shape that leaves that check
/// reachable without changing the protocol again. Do NOT use <see cref="NullType"/> — that is already the
/// "accept any value" sentinel for scalar arguments and the two would be indistinguishable.</para>
/// </remarks>
public static class Params
{
    /// <summary>Field-metadata key carrying the style. Absent ⇒ <see cref="ParamStyle.Positional"/>.</summary>
    public const string StyleKey = "fabricator.param_style";

    private const string NamedValue = "named";
    private const string TableValue = "table";
    private const string ConstantValue = "constant";
    private const string VarArgsValue = "varargs";

    /// <summary>An ordinary positional parameter.</summary>
    public static Field Positional(string name, IArrowType type, bool nullable = true) =>
        new(name, type, nullable);

    /// <summary>A named parameter — <c>name := value</c>. Always nullable: omitted ⇒ a typed NULL.</summary>
    public static Field Named(string name, IArrowType type) =>
        new(name, type, nullable: true, Meta(NamedValue));

    /// <summary>
    /// The input table of a table-in-out function. <paramref name="columns"/> declares the expected input
    /// columns; it is not enforced today, only recorded (see the type note on <see cref="Params"/>).
    /// </summary>
    /// <param name="name">Documentation only. DuckDB never sees it: the table argument is positional and the
    /// binder discards any name written at the call site, so <c>input := (SELECT …)</c> is NOT a way to pass
    /// it. The name exists for diagnostics and error messages. "input" is the convention.</param>
    /// <remarks>
    /// ⚠ Declaring NO columns yields a scalar placeholder type, not an empty struct — Apache.Arrow cannot
    /// build one (see <c>TableInputType</c>). So "columns not declared" and "declared as empty" are
    /// INDISTINGUISHABLE. Harmless while the type is ignored; if call-site validation is ever turned on,
    /// absent columns must be read as "unvalidated", never as "must have no columns".
    /// </remarks>
    public static Field TableInput(string name, Schema? columns = null) =>
        new(name, TableInputType(columns?.FieldsList), nullable: true, Meta(TableValue));

    /// <summary>
    /// The input table of a table-in-out function, declaring its columns inline — the shape to prefer when
    /// authoring a new function (the <see cref="Schema"/> overload exists because in-out functions already
    /// held their input columns as one).
    /// </summary>
    public static Field TableInput(string name, params Field[] columns) =>
        new(name, TableInputType(columns), nullable: true, Meta(TableValue));

    /// <summary>Re-flags an existing field as a named parameter, preserving its name and type.</summary>
    public static Field AsNamed(Field field) => Named(field.Name, field.DataType);

    /// <summary>
    /// A bind-time CONSTANT parameter of a LATERAL function (see <see cref="ParamStyle.Constant"/>): the
    /// caller passes a bare constant of ANY type — in EITHER call shape — and its typed value arrives in
    /// <see cref="ILateralFunction.Bind"/>'s <c>args</c> under <paramref name="name"/>. It is NOT an input
    /// column: the host strips it from the bind's input schema and from every
    /// <see cref="ILateralSession.Call"/> chunk. The Arrow null type registers the slot as DuckDB <c>ANY</c>,
    /// which is what lets any constant's own type bind there.
    /// </summary>
    public static Field Constant(string name) =>
        new(name, NullType.Default, nullable: true, Meta(ConstantValue));

    /// <summary>
    /// A VARIADIC TAIL of <paramref name="type"/> (see <see cref="ParamStyle.VarArgs"/>): any number of
    /// further arguments after the declared ones, each implicitly cast to it. Must be the last positional
    /// field.
    /// </summary>
    public static Field VarArgs(string name, IArrowType type) =>
        new(name, type, nullable: true, Meta(VarArgsValue));

    /// <summary>
    /// A VARIADIC TAIL accepting arguments of ANY type — the <see cref="NullType"/> sentinel, which is how
    /// this protocol has always spelled DuckDB <c>ANY</c> (the host maps it to <c>LogicalType::ANY</c>, so
    /// DuckDB inserts no cast and each argument arrives as its own runtime type).
    /// </summary>
    public static Field VarArgs(string name) => VarArgs(name, NullType.Default);

    /// <summary>
    /// Composes a canonical signature from a provider's local "positional here, named there" pair. The
    /// resulting order — positional/table fields, then named — is the one DuckDB requires, so this is also
    /// the only legal composition.
    /// </summary>
    /// <remarks>
    /// A convenience for provider base classes that already held the two apart, NOT a second protocol: what
    /// crosses the boundary is always the single flagged schema this returns. Prefer declaring one schema
    /// directly in new code.
    /// </remarks>
    public static Schema Combine(Schema? positional, Schema? named)
    {
        var fields = new List<Field>();
        if (positional is not null)
        {
            fields.AddRange(positional.FieldsList);
        }
        if (named is not null)
        {
            foreach (var f in named.FieldsList)
            {
                fields.Add(StyleOf(f) == ParamStyle.Named ? f : AsNamed(f));
            }
        }
        return new Schema(fields, metadata: null);
    }

    /// <summary>
    /// The carrier type for a table-input field: a struct of the declared columns, or — when none are
    /// declared — a scalar PLACEHOLDER.
    /// </summary>
    /// <remarks>
    /// ⚠ Apache.Arrow (23.0.0) cannot even CONSTRUCT <c>new StructType(empty)</c>: it raises
    /// <c>ArgumentNullException(Parameter 'fields')</c> on a perfectly non-null EMPTY list, so the message
    /// names the wrong problem. That is the same zero-field hostility as the schema export/import limit, one
    /// step earlier than documented — it fires in a static field initializer, taking the whole type down and
    /// (via ListGlobalFunctions) silently dropping every global function registered after it. Hence the
    /// placeholder. The type is ignored either way; the STYLE flag is what is authoritative.
    /// </remarks>
    private static IArrowType TableInputType(IReadOnlyList<Field>? columns) =>
        columns is { Count: > 0 } ? new StructType(columns) : BooleanType.Default;

    /// <summary>The style of a declared field.</summary>
    public static ParamStyle StyleOf(Field field)
    {
        if (field.HasMetadata && field.Metadata.TryGetValue(StyleKey, out var v))
        {
            if (v == NamedValue) return ParamStyle.Named;
            if (v == TableValue) return ParamStyle.TableInput;
            if (v == ConstantValue) return ParamStyle.Constant;
            if (v == VarArgsValue) return ParamStyle.VarArgs;
        }
        return ParamStyle.Positional;
    }

    /// <summary>The declared input-table columns of a table-input field (empty when none were declared).</summary>
    public static IReadOnlyList<Field> TableColumns(Field field) =>
        field.DataType is StructType s ? s.Fields : System.Array.Empty<Field>();

    /// <summary>
    /// The variadic tail of a declaration, or null. The host reads this to fill DuckDB's
    /// <c>SimpleFunction::varargs</c>; everything before it is the function's MINIMUM arity.
    /// </summary>
    public static Field? VarArgsField(Schema parameters)
    {
        foreach (var f in parameters.FieldsList)
        {
            if (StyleOf(f) == ParamStyle.VarArgs)
            {
                return f;
            }
        }
        return null;
    }

    /// <summary>
    /// The two STRUCTURAL rules a variadic tail must obey, independent of function kind: at most one, and it
    /// must be the last POSITIONAL field (named parameters may follow — they are a separate namespace).
    /// </summary>
    /// <remarks>
    /// ⚠ Both are refused rather than reinterpreted. A second tail has no meaning DuckDB can express (there
    /// is one <c>varargs</c> type), and a tail with a positional field after it would silently swallow that
    /// field's slot — the caller would pass an argument the function never receives at the position it
    /// declared.
    /// </remarks>
    public static void ValidateVarArgs(string function, Schema parameters)
    {
        bool seenVarArgs = false;
        foreach (var f in parameters.FieldsList)
        {
            var style = StyleOf(f);
            if (style == ParamStyle.VarArgs)
            {
                if (seenVarArgs)
                {
                    throw new ArgumentException(
                        $"fabricator: '{function}' declares more than one variadic tail ('{f.Name}'); DuckDB "
                        + "carries exactly one varargs type per function.");
                }
                seenVarArgs = true;
                continue;
            }
            if (seenVarArgs && style != ParamStyle.Named)
            {
                throw new ArgumentException(
                    $"fabricator: '{function}' declares '{f.Name}' after its variadic tail. The tail must be "
                    + "the LAST positional parameter — everything the caller writes after the declared "
                    + "arguments belongs to it, so a positional parameter following it can never be filled.");
            }
        }
    }

    /// <summary>
    /// Validates a declaration against the rules above and the kind's own limits. Throws
    /// <see cref="ArgumentException"/> naming the function and the offending parameter.
    /// </summary>
    /// <param name="allowNamed">False for a DuckDB <c>ScalarFunction</c>, which has no named-parameter
    /// concept at all — declaring one would produce a function whose documented call syntax is a binder
    /// error, so it is refused here rather than silently ignored.</param>
    /// <param name="allowTableInput">True only for the in-out kinds.</param>
    /// <param name="allowConstant">True only for lateral functions — the one kind whose positional slots are
    /// per-row input columns, which is what a bind-time constant needs an escape from. On any other kind a
    /// named parameter already does the job.</param>
    /// <param name="allowVarArgs">False only for AGGREGATES, whose state/update/combine marshal was never
    /// examined for a per-call-site width. Refused BY NAME rather than silently registered as an ordinary
    /// positional parameter, which is what it would otherwise become.
    /// ⚠ Every other kind takes one, by one of two mechanisms: for scalar, table, sqlgen and in-out the tail
    /// widens the ARGS BATCH (its values are bind-time constants); for LATERAL it widens the per-row WIRE,
    /// because there the positional slots really are the input columns.</param>
    public static void Validate(string function, Schema parameters, bool allowNamed, bool allowTableInput,
                                bool allowConstant = false, bool allowVarArgs = false)
    {
        bool seenNamed = false;
        bool seenTable = false;
        ValidateVarArgs(function, parameters);
        foreach (var f in parameters.FieldsList)
        {
            var style = StyleOf(f);
            if (style == ParamStyle.Named && !allowNamed)
            {
                throw new ArgumentException(
                    $"fabricator: '{function}' declares the named parameter '{f.Name}', but this function kind "
                    + "has no named-parameter support in DuckDB — declare it positionally.");
            }
            if (style == ParamStyle.Constant && !allowConstant)
            {
                throw new ArgumentException(
                    $"fabricator: '{function}' declares the bind-time constant parameter '{f.Name}', which only "
                    + "a LATERAL function may take — declare a named parameter instead.");
            }
            if (style == ParamStyle.VarArgs && !allowVarArgs)
            {
                throw new ArgumentException(
                    $"fabricator: '{function}' declares the variadic tail '{f.Name}', which this function kind "
                    + "does not support (scalar, table and sqlgen functions only — it is deferred for lateral "
                    + "and in-out, whose positional slots are per-row input columns).");
            }
            if (style == ParamStyle.TableInput && !allowTableInput)
            {
                throw new ArgumentException(
                    $"fabricator: '{function}' declares the table input '{f.Name}', which only a table-in-out "
                    + "function may take.");
            }
            if (style == ParamStyle.TableInput)
            {
                if (seenTable)
                {
                    throw new ArgumentException(
                        $"fabricator: '{function}' declares more than one table input ('{f.Name}'); DuckDB "
                        + "allows at most one subquery parameter.");
                }
                seenTable = true;
            }
            if (style == ParamStyle.Named)
            {
                seenNamed = true;
            }
            else if (seenNamed)
            {
                throw new ArgumentException(
                    $"fabricator: '{function}' declares '{f.Name}' after a named parameter. Named parameters "
                    + "must come last — DuckDB rejects an unnamed argument following a named one, so such a "
                    + "function could not be called.");
            }
        }
    }

    /// <summary>The positional ++ table fields, in declared order (what DuckDB registers as arguments).</summary>
    public static IEnumerable<Field> PositionalFields(Schema parameters) =>
        parameters.FieldsList.Where(f => StyleOf(f) != ParamStyle.Named);

    /// <summary>The named fields, in declared order.</summary>
    public static IEnumerable<Field> NamedFields(Schema parameters) =>
        parameters.FieldsList.Where(f => StyleOf(f) == ParamStyle.Named);

    /// <summary>
    /// The parameter count reported by <c>fabricator_functions()</c>: positional + named, EXCLUDING the table
    /// input, so the number keeps meaning "arguments you pass a value for". A table-in-out function taking
    /// only its input table therefore reports 0, not 1.
    /// </summary>
    /// <remarks>Derived, never stored — a hand-maintained count is exactly the kind of number that drifts
    /// from the schema it describes.</remarks>
    public static int DeclaredCount(Schema parameters) =>
        parameters.FieldsList.Count(f => StyleOf(f) != ParamStyle.TableInput);

    private static IReadOnlyDictionary<string, string> Meta(string style) =>
        new Dictionary<string, string> { [StyleKey] = style };
}
