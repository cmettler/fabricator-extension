# Routing managed HTTP through DuckDB's httpfs — design, NOT built

> **Status: ANALYSIS ONLY (2026-08-18). Nothing is implemented.**
>
> **WHO THIS IS FOR: A PLUGIN THAT TALKS TO A REST API.** A third-party plugin (the Sustainalytics one is the
> first) needs to make HTTPS calls, and today that means it carries its own credential handling, its own TLS
> and proxy configuration, its own retry policy and its own way of being told a token. The goal is that it
> carries NONE of that: the host hands it an `HttpClient` already wired to DuckDB's HTTP stack, so
> authentication is a `CREATE SECRET` the user already knows how to write and the plugin never sees a
> credential at all.
>
> ⚠ **This is NOT about the storage read path.** Remote FILE reads (`abfss://` Delta logs, `s3://` parquet)
> are a separate problem with separate options and a separate measurement backlog — see §6, which is a
> POINTER, not a prerequisite.

## 1. Why this is worth wanting

A plugin that calls a REST API has to solve, itself, everything in the left column — all of which the user
has already configured once for DuckDB:

| concern | DuckDB `httpfs` / secrets | what a plugin does today |
|---|---|---|
| credentials | `CREATE SECRET (TYPE http, …)`, scoped by URL prefix | declares its own `SecretFields`, resolves them itself, assembles its own auth header |
| TLS trust | `ca_cert_file`, `enable_curl_server_cert_verification` | ⚠ **does not reach a .NET SDK at all** — the MinIO self-signed case is the recorded example |
| proxy | `http_proxy`, `http_proxy_username/password` | `HttpClient.DefaultProxy`, unconfigured |
| retry | `http_retries`, `http_retry_backoff`, `http_timeout` | per-plugin, or none |
| logging | one place | per-plugin |

**The deletion this enables is the point.** A plugin on this transport declares no secret fields for
authentication and writes no token flow: the user writes `CREATE SECRET (TYPE http, …)` scoped to the API's
URL prefix and the plugin's calls are authenticated by the host. That is the same argument that made the
**host filesystem bridge** (`docs/filesystem-bridge.md`) worth building — and it paid there: an `abfss://`
Delta log inherits DuckDB's Azure configuration because the reads go through DuckDB.

⚠ **Two costs, stated up front.** DuckDB's `ExternalFileCache` is a VFS-layer cache, so an HTTP shim does
**not** inherit range caching (irrelevant for a REST call, which is not a file). And `HTTPUtil` is
C++-internal, so this needs new ABI entries plus header/stream marshaling — see §7, where the gating unknown
is whether it is reachable from a loadable extension at all.

## 1b. The plugin-facing surface — THE decision to make first

Everything below is mechanics. **The design question that actually matters is what a plugin author writes**,
because that is the contract we would be committing to in `Fabricator.Abstractions`, and it is the part that
cannot be changed quietly later.

The shape to aim for is that a plugin never constructs the transport:

```csharp
public sealed class SustainalyticsBackend : IBackend
{
    // Handed in by the host, already wired to DuckDB's HTTP stack and this connection's secrets.
    public IBackendCatalog OpenCatalog(string connectionString, string optionsJson, IHttpClientFactory http) => ...
}
```

Open, and worth settling before any marshaling is written:

- **How does the plugin RECEIVE it?** A new optional parameter on `OpenCatalog` (a default-implementation
  overload, the pattern `IBackend` already uses for `requestedProvider`), or an ambient the host sets around
  the crossing, or a property injected at registration. ⚠ An AMBIENT would be the worst of the three here:
  a plugin's HTTP calls happen at SCAN time, on pool threads, long after the crossing that set it — exactly
  the `AsyncLocal`-per-crossing trap that made `fabricator_install_plugin` report itself disabled.
- **Is it an `HttpClient`, an `HttpMessageHandler`, or a factory?** A factory lets the plugin set its own
  `BaseAddress` and add `DelegatingHandler`s; a bare `HttpClient` is simpler and stops the plugin adding
  retry policies that fight DuckDB's.
- **Which secret does a call use?** DuckDB's `TYPE http` secrets are scoped by URL prefix, so the natural
  answer is "whatever matches the request URI" and the plugin passes nothing. Confirm that scope matching is
  reachable from where we would call it.
- **What does a plugin do when the host does NOT provide it** (an older host, or a build without httpfs)? A
  null factory that the plugin must null-check is a trap; refusing at registration is louder and probably
  right.

⚠ **This changes the Sustainalytics skeleton.** It currently declares `client_id` / `client_secret` /
`base_url` as `SecretFields` — which is the right shape for a plugin that authenticates itself, and the wrong
one if the host authenticates for it. Do not build that plugin's auth until this is settled.

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

## 5. What this would let a plugin delete

- **Its entire credential surface.** No `SecretFields`, no token flow, no refresh handling: the user writes
  `CREATE SECRET (TYPE http, …)` scoped to the API prefix.
- **Its TLS configuration.** `ca_cert_file` would reach a plugin's HTTP calls, which it does not today.
- **Its retry and timeout policy**, in favour of DuckDB's, which the user can already tune.

⚠ It does **not** replace the Fabric/Azure credential chain. Those mint AAD tokens for a specific
AUDIENCE — an identity problem, not a transport one — so `FabricCredentialResolver` stays exactly as it is.
A plugin needing an AAD token for a first-party Microsoft API is not the case this serves.

## 6. NOT a prerequisite: the storage read path is a different problem

CLAUDE.md records a "measure these first" list — the unexplained 180 ms-vs-3 ms ranged-read asymmetry, and
the fact that the existing host-FS bridge already routes reads through DuckDB's stack with zero new ABI.
**That list is about remote FILE reads and does not gate this.** A REST call has no file, no range request
and nothing `ExternalFileCache` could serve, so neither alternative applies:

- the ranged-read asymmetry is a property of `DataLakeFileClient` reading a blob;
- the host-FS bridge answers "read this path", which a REST API does not have.

Kept as a pointer only, so the two are not conflated again: they are separate topics, to be addressed
separately.

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
