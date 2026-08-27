// Copyright (c) Christoph Mettler and contributors.
// SPDX-License-Identifier: Apache-2.0
// See LICENSE in the project root for license information.

namespace Fabricator.Installer.Tests;

/// <summary>
/// A <see cref="FactAttribute"/> that reports as SKIPPED on non-Windows instead of running.
///
/// <para>The alternative — an <c>if (!OperatingSystem.IsWindows()) return;</c> at the top of the test —
/// counts as a PASS, so CI on Linux reports coverage of a Windows-only path it never executed. That is
/// the same falsely-green shape this project keeps running into, so it is worth a few lines to avoid.</para>
///
/// <para>Implemented as an attribute because xunit 2.9's <c>Assert</c> has no dynamic skip
/// (<c>Assert.Skip</c>/<c>SkipUnless</c> arrived in xunit v3); setting <c>Skip</c> from a derived
/// FactAttribute is the supported 2.x mechanism. The reason is per-test, so the skip line in the runner
/// output says WHY rather than just naming the platform.</para>
/// </summary>
public sealed class WindowsOnlyFactAttribute : FactAttribute
{
    public WindowsOnlyFactAttribute(string reason)
    {
        if (!OperatingSystem.IsWindows())
        {
            Skip = $"Windows-only: {reason}";
        }
    }
}
