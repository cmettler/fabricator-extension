# Routing managed HTTP through DuckDB's httpfs — design, NOT built

> **Status: ANALYSIS ONLY (2026-08-18). Nothing is implemented.** The prize is that a REST-backed provider
> would stop needing its own credentials, proxy, TLS and retry configuration: it would inherit DuckDB's, which
> the user has already set up for `httpfs`. The obstacle is that DuckDB's HTTP surface is **synchronous** and
> .NET's is **asynchronous**, so the whole question is which side blocks and where.

## 1. Why this is worth wanting

Every managed provider that talks to a REST API today carries its own copy of a stack the user has already
configured once for DuckDB:

| concern | DuckDB `httpfs` / secrets | what our providers do today |
|---|---|---|
| credentials | `CREATE SECRET (TYPE http, …)`, `CREATE SECRET (TYPE azure, …)`, scoped by URL prefix | each provider resolves its own (`FabricCredentialResolver`, `AdlsCredential`, the SqlClient access-token marker, …) |
| TLS trust | `ca_cert_file`, `enable_curl_server_cert_verification` | ⚠ **does not reach the .NET SDKs at all** — the MinIO self-signed saga is the recorded case |
| proxy | `http_proxy`, `http_proxy_username/password` | `HttpClient.DefaultProxy`, unconfigured by us |
| retry | `http_retries`, `http_retry_backoff`, `http_timeout` | per-provider, or none |
| logging | one place | per-provider |

A provider on this transport gets all of it for free, and the settings surface stays DuckDB's. That is the
same argument that made the **host filesystem bridge** (`docs/filesystem-bridge.md`) worth building, and it
already paid there: `DuckDbTableFileSystem` reads through DuckDB, which is why an `abfss://` Delta log
inherits DuckDB's Azure configuration.

⚠ **It is NOT free of a real cost, stated up front:** DuckDB's `ExternalFileCache` is a VFS-layer cache, so an
HTTP shim does **not** inherit range caching; and `HTTPUtil` is C++-internal, so this needs new ABI entries
plus header/stream marshaling. Both are recorded in CLAUDE.md's "expose DuckDB's HTTP stack to C#" note, along
with the instruction to **measure the cheaper alternatives first** — see §6.

## 2. The shape: a terminal `HttpMessageHandler`

The right seam is **`HttpMessageHandler`, subclassed directly** — not `DelegatingHandler`. We are the terminal
handler; there is nothing below us. Everything above (`HttpClient`, `IHttpClientFactory`, Polly, auth
handlers) then composes normally, because to .NET we are just a handler.

```csharp
public sealed class DuckDbHttpHandler : HttpMessageHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        // marshal request -> ABI -> C++ HTTPUtil (SYNCHRONOUS) -> ABI -> HttpResponseMessage
    }
}
```

Used as:

```csharp
using var client = new HttpClient(new DuckDbHttpHandler(opener)) { BaseAddress = new Uri("https://api.example.com") };
```

### 2.1 The sync/async inversion — the one real design decision

DuckDB's HTTP call blocks. `SendAsync` must return a `Task`. There is no way to make a blocking C call
asynchronous, so the only question is **which thread blocks**, and the options are not equal:

| option | verdict |
|---|---|
| `Task.FromResult(BlockingSend(...))` — block the CALLER's thread inside `SendAsync` | ⚠ **Wrong.** `SendAsync` is contractually async; blocking in it deadlocks any caller that is itself on a constrained context and makes `HttpClient.Timeout` useless (see §3.3). |
| `Task.Run(() => BlockingSend(...))` — a pool thread per request | **The pragmatic answer.** One pool thread per in-flight request, which is exactly the cost the sync stack imposes; it keeps `SendAsync` honest for every caller above it. |
| a dedicated `TaskScheduler` / bounded worker pool | Worth it only once concurrency is high enough that pool starvation is measurable. Start with `Task.Run` and **measure** before adding a scheduler. |

⚠ **This is the same trade the bridge already makes in the other direction** — CLAUDE.md's "sync ABI wrapper
blocks ONCE on an async core". Here the polarity is reversed: an ASYNC .NET surface over a SYNC core. The
convention to keep is the same: **exactly one blocking point, at the boundary, never per-await.**

### 2.2 Ownership and lifetime

- The handler holds an **opener** (the `ClientContext *`) so the C++ side can resolve secrets and settings for
  the right connection. ⚠ It must be **captured where the ambient is valid** — the same rule the plugin
  installer was bitten by: `AmbientOpener` is `AsyncLocal` per ABI crossing, and `SendAsync` runs on a pool
  thread at an arbitrary later moment. Capture at construction, not inside `SendAsync`.
- `HttpClient` fans many concurrent requests into ONE handler instance, so `SendAsync` **must be re-entrant**:
  no per-request state on the handler.
- `Dispose(bool)` should release whatever native handle the handler owns, and must be idempotent.

## 3. What bites — the checklist to work through when this is built

These are the details that make a handler correct rather than merely working, in the order they usually bite:

1. **The header bag splits in two.** `Content-Type`, `Content-Length`, `Content-Encoding`, `Content-Language`,
   `Expires`, `Last-Modified` belong on `Content.Headers`; everything else on `HttpResponseMessage.Headers`.
   Adding to the wrong one THROWS with the validating `Add`. Use `TryAddWithoutValidation` on the response
   bag and fall back to the content bag — that fallback is what makes it generic:
   ```csharp
   if (!response.Headers.TryAddWithoutValidation(name, values))
       response.Content.Headers.TryAddWithoutValidation(name, values);
   ```
2. **Null `Content` is not empty `Content`.** A `HEAD` or a `204` must still get
   `new StreamContent(Stream.Null)` (or `StringContent("")`), because a great deal of calling code does
   `response.Content.ReadAsStringAsync()` unconditionally and would `NullReferenceException` instead.
3. **Cancellation is how timeouts work.** `HttpClient.Timeout` is implemented by cancelling the token handed
   to `SendAsync`. So: let `OperationCanceledException` propagate when `ct.IsCancellationRequested`, and wrap
   *everything else* in `HttpRequestException` — that is the contract callers above expect. Getting this
   backwards is the usual cause of "why is my timeout throwing something strange".
   ⚠ **And the sync core cannot be cancelled mid-call.** A blocking DuckDB HTTP request will not abort because
   a token fired; the token can only stop us WAITING for it. Either accept that (the request completes and its
   result is discarded) or plumb cancellation into the C++ side — the same problem `InterruptScope` solves for
   host queries, and the place to reuse that machinery.
4. **`RequestMessage` must be set** on the response. Handlers that skip it break redirect handling and a lot
   of diagnostic code.
5. **Streaming vs buffering.** `request.Content.ReadAsStreamAsync(ct)` keeps uploads streaming;
   `HttpCompletionOption.ResponseHeadersRead` only means anything if the response body is a real stream. ⚠ If
   the ABI can only carry `byte[]`, both directions become fully buffered and large payloads sit in memory —
   worth deciding deliberately, since a REST provider paging a large result is exactly the case.
6. **We inherit DuckDB's behaviour, not `SocketsHttpHandler`'s.** Redirect following, the cookie container,
   automatic gzip/brotli decompression, proxy handling and client certificates are features of
   `HttpClientHandler`. Bypassing it means the C++ stack has to provide them or we do. There is no public
   built-in decompression handler to chain in, so `Content-Encoding` would have to be handled by wrapping the
   response stream in `GZipStream`/`BrotliStream` ourselves.
   ⚠ **Check what DuckDB's HTTP layer already does** before implementing any of it — following redirects
   twice is worse than not following them.
7. **Response stream lifetime.** `HttpResponseMessage.Dispose()` disposes the content, which disposes our
   `StreamContent`, which disposes the underlying stream — and must therefore release whatever native
   resource the C++ side is holding. Verify that chain rather than assuming it.

## 4. Composition, and the one DI trap

Because it is an ordinary handler, the whole pipeline still works:

```csharp
services.AddHttpClient("api", c => c.BaseAddress = new Uri("https://api.example.com"))
        .ConfigurePrimaryHttpMessageHandler(sp => new DuckDbHttpHandler(sp.GetRequiredService<IOpenerAccessor>()))
        .AddHttpMessageHandler<AuthHandler>()
        .AddPolicyHandler(retryPolicy);
```

⚠ `IHttpClientFactory` **pools and rotates** primary handlers (`SetHandlerLifetime`, 2 minutes by default), so
it will construct and dispose ours periodically. Anything expensive behind it must be a singleton the handler
does NOT own — the `ownsInner` flag pattern. For us the "inner" is the DuckDB opener, whose lifetime belongs
to the connection, so the handler must **never** dispose it.

## 5. What this would let us delete

The point is not elegance, it is deleting credential code:

- A REST provider would stop resolving its own credentials — `CREATE SECRET (TYPE http, …)` scoped to the API
  prefix covers bearer tokens and basic auth.
- The recorded TLS gap closes: `ca_cert_file` would finally reach a managed provider's HTTP calls, which it
  does not today (the MinIO self-signed case).
- ⚠ It does **not** replace the Fabric/Azure credential chain: those mint AAD tokens for specific audiences,
  which is an identity problem, not a transport one. `FabricCredentialResolver` stays.

## 6. ⚠ Measure these FIRST — the cheaper alternatives, in order

CLAUDE.md already records this as the ordering, and it has not been done:

1. **The unexplained 180 ms-vs-3 ms asymmetry.** Sequential ranged `ReadAsync` on one `DataLakeFileClient`
   cost ~180 ms/request while `ReadContentAsync` on fresh clients cost 2–6 ms. If ranged reads are dropping
   or renegotiating the connection, an SDK transport tweak wins with none of this work.
2. **The EXISTING host-FS bridge already routes reads through DuckDB's stack.** `DuckDbTableFileSystem` over
   `abfss://` gets `ExternalFileCache` and DuckDB's TLS/proxy settings with **zero new ABI**. A hybrid — reads
   through the host FS, commit primitives through the direct SDK for atomicity — is most of the benefit at a
   fraction of the cost. For a REST API specifically this does not apply (there is no file), which is exactly
   why the shim is the *third* option and not the first.
3. **Only then the shim.**

## 7. Open questions to settle before writing code

- **Does DuckDB expose a usable HTTP entry point to an extension at all?** `HTTPUtil` is C++-internal;
  confirm what is reachable from a loadable extension and what would need new ABI entries. This is the
  gating question — everything above assumes it is reachable.
- **Which secret types apply.** `TYPE http` exists; confirm how its scope matching interacts with an
  arbitrary API base URL.
- **Cancellation** — reuse `InterruptScope`, or accept non-cancellable in-flight requests (§3.3).
- **Streaming** — does the ABI carry streams, or only `byte[]` (§3.5)?
- **Does DuckDB follow redirects / decompress?** Determines how much of §3.6 we implement.
- **Threading** — is DuckDB's HTTP layer safe to call concurrently from several pool threads? `SendAsync` is
  re-entrant by contract, so this must be answered before `Task.Run` fan-out is safe.

## 8. A worked skeleton

Kept as a starting point rather than a specification. The mapping is mechanical; the interesting parts are the
comments.

```csharp
public sealed class DuckDbHttpHandler : HttpMessageHandler
{
    private readonly nint _opener;      // captured at CONSTRUCTION — the ambient is gone by SendAsync time

    public DuckDbHttpHandler(nint opener) => _opener = opener;

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        byte[]? body = request.Content is null
            ? null
            : await request.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);   // see §3.5

        var headers = request.Headers.Concat(request.Content?.Headers ?? Enumerable.Empty<...>());

        DuckDbHttpResponse raw;
        try
        {
            // ONE blocking point, off the caller's thread. See §2.1.
            raw = await Task.Run(() => HostHttp.Send(_opener, request.Method.Method,
                                                     request.RequestUri!, headers, body), ct)
                            .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;                                          // timeouts ARE cancellation — §3.3
        }
        catch (Exception ex)
        {
            throw new HttpRequestException(ex.Message, ex); // the contract callers expect
        }

        var response = new HttpResponseMessage((HttpStatusCode)raw.StatusCode)
        {
            RequestMessage = request,                       // §3.4 — do not skip
            Version = request.Version,
            Content = new StreamContent(raw.Body ?? Stream.Null),   // §3.2 — never null
        };
        foreach (var (name, values) in raw.Headers)
        {
            if (!response.Headers.TryAddWithoutValidation(name, values))
                response.Content.Headers.TryAddWithoutValidation(name, values);  // §3.1
        }
        return response;
    }
}
```
