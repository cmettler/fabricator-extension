// Copyright (c) Christoph Mettler and contributors.
// SPDX-License-Identifier: Apache-2.0
// See LICENSE in the project root for license information.

using System;

namespace Fabricator.Bridge;

/// <summary>
/// Generic resolution over <see cref="System.IServiceProvider"/> — <c>provider.GetService&lt;T&gt;()</c> —
/// so a plugin author can hold the BCL interface and still resolve by type.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="FabricatorServices.Get{T}"/> is the primary API and needs none of this; these exist for the
/// case the locator was shaped around — passing <see cref="FabricatorServices.Provider"/> to code that wants
/// an <c>IServiceProvider</c> and expects the familiar generic call on it.
/// </para>
/// <para>
/// ⚠ <b>They work on ANY <c>IServiceProvider</c>, not only ours</b>, which is why
/// <see cref="GetRequiredService{T}"/>'s message says nothing about the bridge —
/// <see cref="FabricatorServices.GetRequired{T}"/> is the one that can, and does.
/// </para>
/// <para>
/// ⚠⚠ <b>THE NAMES ARE DELIBERATELY <c>Microsoft.Extensions.DependencyInjection</c>'s, AND THAT MEANS A
/// PLUGIN REFERENCING MEDI TOO WILL SEE AN AMBIGUITY (CS0121) if it imports both namespaces.</b> That is the
/// right trade and the right failure: familiarity is the ONLY thing MEDI was wanted for here
/// (docs/plugin-services.md §3.4), a plugin that references MEDI already HAS these methods so ours are
/// redundant for it, and the compiler saying so — with a one-line fix, dropping one <c>using</c> — beats
/// inventing a second vocabulary nobody knows. It is a COMPILE error, never a silent wrong resolution.
/// </para>
/// </remarks>
public static class FabricatorServiceProviderExtensions
{
    /// <summary>
    /// Resolves <typeparamref name="T"/>, or <see langword="null"/> when the provider has no implementation.
    /// </summary>
    public static T? GetService<T>(this IServiceProvider provider) where T : class
    {
        ArgumentNullException.ThrowIfNull(provider);
        return provider.GetService(typeof(T)) as T;
    }

    /// <summary>
    /// Resolves <typeparamref name="T"/> or throws, naming the interface.
    /// </summary>
    /// <exception cref="InvalidOperationException">The provider has no implementation of it.</exception>
    public static T GetRequiredService<T>(this IServiceProvider provider) where T : class =>
        provider.GetService<T>()
        ?? throw new InvalidOperationException($"No implementation of {typeof(T).FullName} is registered.");
}
