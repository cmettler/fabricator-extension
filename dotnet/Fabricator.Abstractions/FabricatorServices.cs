// Copyright (c) Christoph Mettler and contributors.
// SPDX-License-Identifier: Apache-2.0
// See LICENSE in the project root for license information.

using System;
using System.Collections.Concurrent;

namespace Fabricator.Bridge;

/// <summary>
/// The host service locator: how a plugin reaches a capability that needs the RUNNING HOST — DuckDB's
/// filesystem (<see cref="IHostFileSystem"/>), its HTTP stack (<see cref="IHostHttp"/>), SQL on the
/// hosting instance (<see cref="IHostQuery"/>), and its logging (<see cref="IHostLog"/>, the route into
/// <c>duckdb_logs</c>). Declared HERE, in the contract assembly, and FILLED IN by the bridge at boot, so a
/// plugin resolves them with a reference to <c>Fabricator.Abstractions</c> alone.
/// </summary>
/// <remarks>
/// <para>
/// <b>THE LINE THIS SURFACE DRAWS.</b> A capability belongs here only if it needs host state — the ambient
/// <c>ClientContext</c>, for secrets, for the HTTP stack, for a connection. Anything that needs nothing is
/// a LIBRARY, not a service: it should be code a plugin REFERENCES (that is what <c>Fabricator.Common</c>
/// is for), because resolving it at runtime buys nothing and costs an interface. See
/// docs/plugin-services.md §3.1.
/// </para>
/// <para>
/// ⚠ <b>It is a LOCATOR, deliberately, not constructor injection.</b> A plugin is discovered by reflection
/// and instantiated with a parameterless <c>Activator.CreateInstance</c>, so injecting through constructors
/// would change the discovery contract and every out-of-tree plugin's constructor. Injection can be added
/// on top later; it cannot be un-added.
/// </para>
/// <para>
/// ⚠ <b>The registry is MUTABLE and must stay so.</b> <c>BackendRegistry.Invalidate()</c> re-scans after
/// <c>fabricator_install_plugin</c>, which is what makes install-and-use work in the session that installs.
/// A built, immutable container (the <c>Microsoft.Extensions.DependencyInjection</c> shape) would have to be
/// rebuilt on every invalidate, and anything holding the previous provider would be silently stale.
/// </para>
/// <para>
/// ⚠ <b>Resolve LAZILY — at USE, not at load.</b> The plugin scan is ordered by path, so a plugin that
/// resolves a service in its constructor or in <c>IBackend.Name</c> may run before whatever registers it.
/// Every service here is registered by the bridge before any plugin is scanned, so the host services are
/// safe either way; the rule matters the day one plugin publishes a service another consumes.
/// </para>
/// <para>
/// ⚠ <b>A service instance MUST NOT capture the ambient <c>ClientContext</c>.</b> Every implementation reads
/// it PER CALL. A catalog is DATABASE-scoped and outlives the connection that attached it, so a held
/// <c>ClientContext*</c> dangles the day that connection closes — the <c>table_stats</c> SIGSEGV class. It
/// follows that a service is usable only from INSIDE an ABI crossing (a scan, a function's execute, a
/// provider's <c>OpenCatalog</c>) or anywhere the ambient still flows from one; the ambient is an
/// <c>AsyncLocal</c>, so it survives <c>await</c> and <c>Task.Run</c> but not a thread parked before the
/// crossing began.
/// <para>⚠ <see cref="IHostLog"/> is the EXCEPTION and shows where the boundary really is: it needs the
/// running host (the <c>host_log</c> callback) but no per-call context, so it works anywhere.</para>
/// </para>
/// <para>
/// <b>EVERY SERVICE HERE IS A SINGLETON, and one that needs a narrower scope supplies its own factory
/// method rather than asking the registry for one.</b> <see cref="IHostLog"/> is the worked example:
/// resolving it gives one object for the process, and <c>GetLogger(category)</c> is what produces the
/// per-category one. So the registry has no notion of scope and does not need one — which is why it can
/// stay a dictionary. docs/plugin-services.md §5 Q5.
/// </para>
/// </remarks>
public static class FabricatorServices
{
    private static readonly ConcurrentDictionary<Type, object> Services = new();

    /// <summary>
    /// Publishes <paramref name="instance"/> as the implementation of <typeparamref name="T"/>, REPLACING any
    /// previous one. The bridge calls this at boot for the host services; a plugin may call it to publish a
    /// singleton of its own (see the remarks on <see cref="FabricatorServices"/> about resolving lazily).
    /// </summary>
    /// <remarks>
    /// ⚠⚠ <b>NAME <typeparamref name="T"/> EXPLICITLY.</b> <c>Register(new FooService())</c> infers
    /// <typeparamref name="T"/> as the CONCRETE type and registers under that key, so every
    /// <c>Get&lt;IFoo&gt;()</c> then misses — a silent failure, because the registration itself succeeded.
    /// Write <c>Register&lt;IFoo&gt;(new FooService())</c>. C# cannot constrain a type parameter to be an
    /// interface, so this is a convention rather than a compiler check.
    /// <para>
    /// ⚠ Last registration WINS, deliberately — the same rule <c>BackendRegistry.Add</c> follows for built-in
    /// providers. There is no collision refusal here because there is no name to collide on: the key is the
    /// interface TYPE, and two assemblies can only agree on it by both referencing the assembly that declares
    /// it. That is also why a cross-plugin contract needs a shared assembly the plugins BOTH reference and not
    /// merely a matching interface shape — a structurally identical type from a second copy is a DIFFERENT
    /// key and resolution simply misses (docs/plugin-services.md §3.3).
    /// </para>
    /// </remarks>
    public static void Register<T>(T instance) where T : class
    {
        ArgumentNullException.ThrowIfNull(instance);
        Services[typeof(T)] = instance;
    }

    /// <summary>
    /// Resolves <typeparamref name="T"/>, or <see langword="null"/> when the host has not published one.
    /// </summary>
    /// <remarks>
    /// Use this when absence is a legitimate state a caller can degrade through. When it is not — and for a
    /// host service it usually is not — prefer <see cref="GetRequired{T}"/>, whose message names the interface:
    /// a <see langword="null"/> dereferenced three frames later reports the wrong thing.
    /// </remarks>
    public static T? Get<T>() where T : class =>
        Services.TryGetValue(typeof(T), out var svc) ? (T)svc : null;

    /// <summary>
    /// Resolves <typeparamref name="T"/> or throws, naming the interface and why it may be missing.
    /// </summary>
    /// <exception cref="InvalidOperationException">No implementation has been registered.</exception>
    public static T GetRequired<T>() where T : class =>
        Get<T>() ?? throw new InvalidOperationException(
            $"No implementation of {typeof(T).FullName} is registered. Host services are published by the " +
            "bridge at boot, so this means either the host does not support the capability, or the call was " +
            "made before the bridge initialised.");

    /// <summary>True when an implementation of <typeparamref name="T"/> is available.</summary>
    public static bool IsAvailable<T>() where T : class => Services.ContainsKey(typeof(T));

    /// <summary>
    /// The same registry behind <see cref="System.IServiceProvider"/> — the BCL contract, so a plugin author
    /// can hold a familiar type and pass it around. <c>GetService</c> returns <see langword="null"/> for an
    /// unregistered type, per that interface's contract.
    /// </summary>
    /// <remarks>
    /// ⚠ <c>IServiceProvider</c> is in the BCL (<c>System.Runtime</c>), so exposing it costs no package in
    /// anyone's closure — which is the whole reason it is the contract rather than
    /// <c>Microsoft.Extensions.DependencyInjection.Abstractions</c>. It also keeps the door open: a real
    /// container can be swapped in BEHIND this property later without touching the contract.
    /// </remarks>
    public static IServiceProvider Provider { get; } = new LocatorProvider();

    private sealed class LocatorProvider : IServiceProvider
    {
        public object? GetService(Type serviceType)
        {
            ArgumentNullException.ThrowIfNull(serviceType);
            return Services.TryGetValue(serviceType, out var svc) ? svc : null;
        }
    }
}
