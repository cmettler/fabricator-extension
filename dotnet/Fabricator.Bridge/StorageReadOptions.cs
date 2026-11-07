// Copyright (c) Christoph Mettler and contributors.
// SPDX-License-Identifier: Apache-2.0
// See LICENSE in the project root for license information.

using System;
using Azure.Storage.Files.DataLake;

namespace Fabricator.Bridge;

/// <summary>
/// Client options for READING from ADLS Gen2 / OneLake: a short timeout per try and more retries, instead of
/// the SDK's 100 s per try.
/// </summary>
/// <remarks>
/// <para>A request that gets no answer used to wait the SDK default of 100 s before its retry. On OneLake that
/// is what a stall looks like: TPC-H queries taking 104 s or 205 s (one or two expired tries) in otherwise
/// 1-3 s runs. The timeout covers the time to the response headers and each read of a streamed body, not the
/// whole download, so a slow but progressing read of a large file is not cut off.</para>
/// <para>⚠ Reads only. An upload sends its whole body inside one try, so a short timeout would abort a large
/// append on a slow link; writes keep the default.</para>
/// <para><c>FABRICATOR_STORAGE_READ_TIMEOUT_S</c> overrides the per-try timeout (seconds).</para>
/// </remarks>
internal static class StorageReadOptions
{
    internal static readonly TimeSpan TryTimeout = ReadTimeout();

    private static TimeSpan ReadTimeout()
        => double.TryParse(Environment.GetEnvironmentVariable("FABRICATOR_STORAGE_READ_TIMEOUT_S"),
                           System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture,
                           out double s) && s > 0
            ? TimeSpan.FromSeconds(s)
            : TimeSpan.FromSeconds(15);

    internal static DataLakeClientOptions Create()
    {
        var options = new DataLakeClientOptions();
        options.Retry.NetworkTimeout = TryTimeout;
        options.Retry.MaxRetries = 6;
        options.Retry.Delay = TimeSpan.FromMilliseconds(200);
        options.Retry.MaxDelay = TimeSpan.FromSeconds(5);
        return options;
    }
}
