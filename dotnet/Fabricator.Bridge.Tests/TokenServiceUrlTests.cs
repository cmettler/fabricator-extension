// Copyright (c) Christoph Mettler and contributors.
// SPDX-License-Identifier: Apache-2.0
// See LICENSE in the project root for license information.

using Fabricator.Bridge;

namespace Fabricator.Bridge.Tests;

/// <summary>
/// Offline tests for <see cref="FabricTokenServiceUrl"/> — composing the Fabric token-service mint URL on
/// SPARK compute, where the environment variable that carries it on Python compute does not exist.
/// </summary>
/// <remarks>
/// <para>The anchor is MEASURED on one capacity, both kernels, the same day: a PYTHON notebook's
/// <c>AZURE_FABRIC_TOKEN_SERVICE_URL</c> versus the two files a PYSPARK notebook has instead. The composed
/// string is byte-identical to the env var, and minting with it returned 200 for the storage, Fabric and
/// SQL audiences.</para>
/// <para>⚠ The DOUBLED SLASH in <c>.../automatic//token</c> is the reason this is sliced rather than parsed,
/// and the reason it is tested offline at all: it looks like a formatting artefact, a <c>Uri</c> round trip
/// may normalise it away, and the service 404s without it. A first attempt that used the config file's bare
/// origin as the endpoint produced 404 on every request INCLUDING its control — which reads like a rejected
/// token and is really a wrong route, i.e. a void experiment rather than a finding.</para>
/// <para>Malformed input must yield NULL, never a guess: a wrong URL here would send a session token to a
/// host nobody intended. Null degrades to "no ambient credential", which is the pre-existing behaviour.</para>
/// </remarks>
public class TokenServiceUrlTests
{
    // tokenservice.config.json -> tokenServiceEndpoint. The bare ORIGIN: no path, and NOT the endpoint.
    private const string Origin = "https://tokenservice1.westeurope.trident.azuresynapse.net:443";

    // .trident-context -> trident.lakehouse.tokenservice.endpoint.
    private const string Workload =
        "https://763ce0dd36ba451abeb25d22aa9d5efa.pbidedicated.windows.net"
        + "/webapi/capacities/763ce0dd-36ba-451a-beb2-5d22aa9d5efa/workloads/SparkCore"
        + "/SparkCoreService/automatic//token";

    // What a PYTHON kernel on that same capacity carries in AZURE_FABRIC_TOKEN_SERVICE_URL.
    private const string MeasuredEnvVar =
        "https://tokenservice1.westeurope.trident.azuresynapse.net:443/api/v1/proxy"
        + "/webapi/capacities/763ce0dd-36ba-451a-beb2-5d22aa9d5efa/workloads/SparkCore"
        + "/SparkCoreService/automatic//token/access";

    [Fact]
    public void Composes_the_url_a_python_kernel_is_handed()
    {
        Assert.Equal(MeasuredEnvVar, FabricTokenServiceUrl.Compose(Origin, Workload));
    }

    [Fact]
    public void Preserves_the_doubled_slash()
    {
        // Load-bearing on its own: the assertion above would still pass if BOTH sides were normalised by a
        // future refactor that ran the result through Uri. This one fails the moment the bytes change.
        Assert.Contains("automatic//token", FabricTokenServiceUrl.Compose(Origin, Workload));
    }

    [Fact]
    public void Tolerates_a_trailing_slash_on_the_origin()
    {
        Assert.Equal(MeasuredEnvVar, FabricTokenServiceUrl.Compose(Origin + "/", Workload));
    }

    [Fact]
    public void Ignores_a_query_or_fragment_on_the_workload_url()
    {
        // The query would belong to the workload endpoint, not to the route being built — and appending it
        // before "/access" would produce a path that cannot resolve.
        Assert.Equal(MeasuredEnvVar, FabricTokenServiceUrl.Compose(Origin, Workload + "?x=1"));
        Assert.Equal(MeasuredEnvVar, FabricTokenServiceUrl.Compose(Origin, Workload + "#frag"));
    }

    [Theory]
    [InlineData(null, Workload)]
    [InlineData(Origin, null)]
    [InlineData("", Workload)]
    [InlineData(Origin, "   ")]
    // No scheme, so no authority to skip past — slicing on the first '/' would otherwise take the whole thing.
    [InlineData(Origin, "763ce0dd.pbidedicated.windows.net/webapi/token")]
    // Authority only: there is no path to borrow.
    [InlineData(Origin, "https://763ce0dd.pbidedicated.windows.net")]
    [InlineData(Origin, "https://763ce0dd.pbidedicated.windows.net/")]
    public void Returns_null_rather_than_guessing(string? origin, string? workload)
    {
        Assert.Null(FabricTokenServiceUrl.Compose(origin, workload));
    }

    [Fact]
    public void PathOf_slices_without_normalising()
    {
        Assert.Equal("/a//b", FabricTokenServiceUrl.PathOf("https://h/a//b"));
        Assert.Equal("/a/./b", FabricTokenServiceUrl.PathOf("https://h/a/./b"));
        Assert.Equal("/a", FabricTokenServiceUrl.PathOf("https://h:443/a?q=1"));
        Assert.Null(FabricTokenServiceUrl.PathOf("https://h"));
        Assert.Null(FabricTokenServiceUrl.PathOf("not-a-url"));
    }
}
