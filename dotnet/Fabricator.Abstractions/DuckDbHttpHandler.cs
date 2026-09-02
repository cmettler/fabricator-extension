// Copyright (c) Christoph Mettler and contributors.
// SPDX-License-Identifier: Apache-2.0
// See LICENSE in the project root for license information.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Fabricator.Bridge;

/// <summary>
/// An <see cref="HttpMessageHandler"/> whose transport is DuckDB's OWN HTTP stack. A component built on it —
/// above all a PLUGIN calling a REST API — stops carrying its own TLS trust, proxy configuration and retry
/// policy, and for a static-credential API stops carrying credentials at all: the user writes
/// <c>CREATE SECRET (TYPE http, BEARER_TOKEN '…', SCOPE 'https://api.example.com')</c> and the host applies
/// it to every request whose URL the scope covers.
/// </summary>
/// <remarks>
/// <para>
/// It lives in the CONTRACT assembly so a plugin needs no reference to <c>Fabricator.Bridge</c>; the bridge
/// publishes <see cref="IHostHttp"/> at boot.
/// </para>
/// <para>
/// This is the TERMINAL handler, not a <c>DelegatingHandler</c> — there is nothing below it. Everything above
/// composes normally (<see cref="HttpClient"/>, <c>IHttpClientFactory</c>, auth and retry handlers), because
/// to .NET this is just a handler.
/// </para>
/// <para>
/// ⚠ <b>It holds no state and no opener</b>, so one instance may back any number of clients and it is safe to
/// keep for a catalog's lifetime. That is not a convenience: the ClientContext it ultimately uses is resolved
/// PER REQUEST from the ambient, because a catalog is database-scoped and outlives the connection that
/// attached it — see <see cref="FabricatorServices"/> for why capturing one would be a dangling pointer.
/// </para>
/// <para>
/// ⚠ WHAT IT DOES NOT INHERIT FROM <c>HttpClientHandler</c>, because DuckDB is doing the transport: automatic
/// decompression (all three DuckDB clients explicitly disable it, so do NOT advertise <c>Accept-Encoding</c>
/// unless you decompress yourself), the cookie container, and client certificates. Redirects ARE followed
/// (DuckDB's <c>follow_location</c> defaults to true).
/// </para>
/// <para>
/// ⚠ ONE VALUE PER HEADER NAME, in both directions — DuckDB's <c>HTTPHeaders</c> is a case-insensitive MAP,
/// so a repeated header cannot cross. Requests join repeats with ", " (correct for every list-valued header
/// and WRONG for <c>Set-Cookie</c>, which is why cookies are not supported here).
/// </para>
/// <para>
/// ⚠ Only GET / PUT / HEAD / DELETE / POST exist in DuckDB's request model. A PATCH is refused by the host
/// rather than mapped onto POST.
/// </para>
/// </remarks>
public sealed class DuckDbHttpHandler : HttpMessageHandler
{
    /// <summary>True once the host has published <see cref="IHostHttp"/> (needs a host at ABI v76 or later).</summary>
    public static bool IsAvailable => FabricatorServices.IsAvailable<IHostHttp>();

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        if (request.RequestUri is null)
        {
            throw new InvalidOperationException("DuckDbHttpHandler: the request has no URI");
        }
        var http = FabricatorServices.Get<IHostHttp>()
            ?? throw new NotSupportedException(
                "DuckDbHttpHandler: the host has published no IHostHttp (it predates ABI v76).");

        byte[]? body = request.Content is null
            ? null
            : await request.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);

        // Content headers are part of the wire request, so they must be merged in: Content-Type above all.
        var headers = RenderHeaders(request.Headers.Concat(
            request.Content?.Headers ?? Enumerable.Empty<KeyValuePair<string, IEnumerable<string>>>()));

        var method = request.Method.Method;
        var url = request.RequestUri.AbsoluteUri;

        string responseJson;
        byte[]? responseBody;
        try
        {
            // ONE blocking point, off the caller's thread. DuckDB's HTTP layer is synchronous and there is no
            // way to make a blocking C call asynchronous, so the only question is WHICH thread blocks —
            // blocking the caller's inside SendAsync would deadlock a constrained context and make
            // HttpClient.Timeout inert. Mirrors the bridge's own convention in reverse: exactly one blocking
            // point, at the boundary, never per-await.
            //
            // ⚠ The ambient ClientContext the transport reads is an AsyncLocal, and ExecutionContext FLOWS
            // into Task.Run — captured here, on the caller's context, which is why this still resolves the
            // right connection despite running on a pool thread.
            (responseJson, responseBody) = await Task
                .Run(() => http.Send(method, url, headers, body), ct)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // ⚠ HttpClient.Timeout IS cancellation — it cancels the token handed to SendAsync — so this must
            // propagate rather than become an HttpRequestException. Note the blocking core does NOT abort: the
            // request runs to completion on its pool thread and its result is discarded. Bounding it needs
            // http_timeout on the DuckDB side, not this token.
            throw;
        }
        catch (Exception ex)
        {
            throw new HttpRequestException(ex.Message, ex);
        }

        return BuildResponse(request, responseJson, responseBody);
    }

    /// <summary>Renders the request headers as the JSON object the ABI carries, joining repeated values with
    /// ", " (the standard list-header form).</summary>
    private static string? RenderHeaders(IEnumerable<KeyValuePair<string, IEnumerable<string>>> headers)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var header in headers)
        {
            map[header.Key] = string.Join(", ", header.Value);
        }
        if (map.Count == 0)
        {
            return null;
        }
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            foreach (var entry in map)
            {
                writer.WriteString(entry.Key, entry.Value);
            }
            writer.WriteEndObject();
        }
        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    private static HttpResponseMessage BuildResponse(HttpRequestMessage request, string responseJson, byte[]? body)
    {
        using var doc = JsonDocument.Parse(responseJson);
        var root = doc.RootElement;

        // A non-empty "error" is a TRANSPORT failure (DNS, connect, TLS) — the host reports it inside the
        // envelope rather than as a failed call, so that a 404 and an unreachable host stay distinguishable.
        // .NET's contract is that only the latter is an exception.
        var error = root.TryGetProperty("error", out var e) ? e.GetString() : null;
        if (!string.IsNullOrEmpty(error))
        {
            throw new HttpRequestException(error);
        }

        var status = root.TryGetProperty("status", out var s) ? s.GetInt32() : 0;
        var response = new HttpResponseMessage((HttpStatusCode)status)
        {
            // Skipping this breaks redirect handling and a lot of diagnostic code.
            RequestMessage = request,
            Version = request.Version,
            // ⚠ NEVER null: a HEAD or a 204 must still get an empty content, because a great deal of calling
            // code does ReadAsStringAsync() unconditionally and would NullReferenceException instead.
            Content = new ByteArrayContent(body ?? System.Array.Empty<byte>()),
        };
        if (root.TryGetProperty("reason", out var reason) && reason.GetString() is { Length: > 0 } r)
        {
            response.ReasonPhrase = r;
        }

        if (root.TryGetProperty("headers", out var headers) && headers.ValueKind == JsonValueKind.Object)
        {
            foreach (var header in headers.EnumerateObject())
            {
                var value = header.Value.GetString() ?? string.Empty;
                // The header bag SPLITS IN TWO: Content-Type, Content-Length, Content-Encoding,
                // Content-Language, Expires and Last-Modified belong on Content.Headers, everything else on
                // the response. Adding to the wrong one throws with the validating Add, so try the response
                // bag first and fall back — that fallback is what makes this generic instead of a name list.
                if (!response.Headers.TryAddWithoutValidation(header.Name, value))
                {
                    response.Content.Headers.TryAddWithoutValidation(header.Name, value);
                }
            }
        }
        return response;
    }

    /// <summary>
    /// Builds an <see cref="HttpClient"/> over this transport. The caller owns and disposes it; the handler
    /// holds nothing, so disposing it is harmless either way.
    /// </summary>
    public static HttpClient CreateClient(Uri? baseAddress = null)
    {
        var client = new HttpClient(new DuckDbHttpHandler(), disposeHandler: true);
        if (baseAddress is not null)
        {
            client.BaseAddress = baseAddress;
        }
        return client;
    }
}
