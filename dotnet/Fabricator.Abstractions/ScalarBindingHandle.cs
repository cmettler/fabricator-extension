using Apache.Arrow;

namespace Fabricator.Bridge;

/// <summary>
/// A bound scalar call site as the host holds it: the <see cref="IScalarFunctionBinding"/> plus the
/// <see cref="IScalarFunction"/> it came from. One of these is what <c>scalarfn_bind</c> hands back as an
/// opaque handle, and <c>scalarfn_close</c> disposes.
/// </summary>
/// <remarks>
/// The DEFINITION is carried alongside the binding for one reason: a binding may legitimately report
/// <c>Result = null</c> ("the declared type stands"), and the zero-input fallback that has to type an empty
/// result stream then needs the declaration to fall back to. Everything on the hot path uses the binding
/// alone.
/// </remarks>
public sealed class ScalarBindingHandle : System.IDisposable
{
    public ScalarBindingHandle(IScalarFunction definition, IScalarFunctionBinding binding)
    {
        Definition = definition;
        Binding = binding;
    }

    /// <summary>The function definition — see the remarks for why it is kept.</summary>
    public IScalarFunction Definition { get; }

    /// <summary>The per-call-site binding.</summary>
    public IScalarFunctionBinding Binding { get; }

    /// <summary>
    /// The result field to report to the host, or null for the UNRESOLVED sentinel (= use the declared type,
    /// which the host already holds).
    /// </summary>
    public Field? ResolvedResult => Binding.Result;

    public void Dispose() => Binding.Dispose();
}
