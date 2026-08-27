// Copyright (c) Christoph Mettler and contributors.
// SPDX-License-Identifier: Apache-2.0
// See LICENSE in the project root for license information.

namespace Fabricator.Installer;

/// <summary>
/// A user-facing installer failure. Every message is written to be actionable as-is, because the
/// AOT shell forwards it verbatim to DuckDB's <c>set_error</c> — it is the only diagnostic the user
/// sees, and there is no log to consult.
/// </summary>
public sealed class InstallerException : Exception
{
    public InstallerException(string message)
        : base(message)
    {
    }

    public InstallerException(string message, Exception? innerException)
        : base(message, innerException)
    {
    }
}
