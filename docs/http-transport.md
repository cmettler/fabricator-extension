# Routing managed HTTP through DuckDB's own stack — BUILT (ABI v76)

> **Status: BUILT and MEASURED (2026-08-18). ABI v76, additive host service `http_request`.**
>
> **WHO THIS IS FOR: A PLUGIN THAT TALKS TO A REST API.** A third-party plugin (the Sustainalytics one is
> the first) needs to make HTTPS calls, and before this it carried its own TLS trust, proxy configuration
> and retry policy, none of which DuckDB's settings could reach. Now `DuckDbHttpHandler` is an ordinary
> .NET `HttpMessageHandler` whose transport is DuckDB's own HTTP layer, so a managed call inherits the
> `TYPE http` secret matching its URL, `ca_cert_file`, `http_proxy*`, `http_timeout` and the retry knobs.
>
> ⚠ **This is NOT about the storage read path.** Remote FILE reads (`abfss://` Delta logs, `s3://` parquet)
> are a separate problem with separate options — see §7, which is a POINTER, not a prerequisite.

**What exists:** the `http_request` host service (`src/fabricator_http.cpp`), its managed wrapper
(`HostFs.HttpRequest`), the handler (`dotnet/Fabricator.Abstractions/DuckDbHttpHandler.cs` — the
CONTRACT assembly, so a plugin reaches it), and the SQL surface
`fabricator_http_request(...)` that makes any of it observable. Gate:
[`test/verify_http_transport.test`](../test/verify_http_transport.test) (21, service tier).

---

## 1. What it buys, and exactly how much

A plugin calling a REST API had to solve everything in the left column itself — all of it already
configured once for DuckDB:

| concern | DuckDB `httpfs` / secrets | what a plugin did before |
|---|---|---|
| credentials | `CREATE SECRET (TYPE http, …)`, scoped by URL prefix | its own `SecretFields`, resolved and assembled by hand |
| TLS trust | `ca_cert_file`, `enable_curl_server_cert_verification` | ⚠ **did not reach a .NET SDK at all** — the MinIO self-signed case is the recorded example |
| proxy | `http_proxy`, `http_proxy_username/password` | `HttpClient.DefaultProxy`, unconfigured |
| retry | `http_retries`, `http_retry_backoff`, `http_timeout` | per-plugin, or none |

### 1.1 ⚠ HOW MUCH IS DELETED DEPENDS ON THE API'S AUTH, and my first write-up overstated it

MEASURED against DuckDB 1.5.5, not assumed:

```
CREATE SECRET s (TYPE http, BEARER_TOKEN '…', SCOPE 'https://api.example.com');          -- accepted
CREATE SECRET s (TYPE http, EXTRA_HTTP_HEADERS MAP {'X-Api-Key':'…'}, SCOPE '…');        -- accepted
CREATE SECRET s (TYPE http, CLIENT_ID 'a', CLIENT_SECRET 'b');
   -> Binder Error: Unknown parameter 'client_secret' for secret type 'http'             -- REFUSED
```

DuckDB's `http` secret carries a **STATIC credential** — a bearer token or arbitrary headers. It performs
no OAuth2 client-credentials exchange, and there is no field for a client id and secret. So:

| the API authenticates with | what the plugin still has to do |
|---|---|
| a static API key or a long-lived token | **nothing** — the secret covers it, and the plugin declares no `SecretFields` at all |
| **OAuth2 client credentials** (Sustainalytics) | **keep its own secret type** for `client_id`/`client_secret` and perform the exchange itself; set `Authorization` per request |

⚠ **The TRANSPORT win and the CREDENTIAL win are separable, and for an OAuth2 API only the first is
available.** Still worth having — one HTTP stack, DuckDB's TLS trust, proxy and retry — but "the plugin
declares no credentials" is true only of the first row. Do not sell the second row on the first row's
benefit. (An earlier version of this file told the Sustainalytics skeleton its `SecretFields` were the
wrong shape and its auth should wait. That was wrong, and is corrected there too.)

### 1.2 The two measurements that settle it

Both are the gate's own sections, run against the docker rig's MinIO (self-signed HTTPS), so they are
LOCAL and repeatable. Both are true A/Bs — identical statements, one variable:

| leg | result |
|---|---|
| `enable_curl_server_cert_verification = true` (default) | **`SSL peer certificate … was not OK`** |
| `= false` | **200 OK** |
| `CREATE SECRET (TYPE http, SCOPE 'https://localhost:9000', VERIFY_SSL false)` | **200 OK** |
| the byte-identical secret scoped `'https://elsewhere.example'` | **`SSL peer certificate … was not OK`** |

The first pair proves DuckDB's settings govern a call made from C#. The second proves a `TYPE http`
secret's fields reach it **and** that SCOPE decides which secret applies — the negative leg is the one that
matters, because without it the positive would be equally true of an implementation that applied every http
secret it could find, which is a credential LEAK rather than a passing test.

A live third measurement, off the rig: with a bearer-token secret scoped to `httpbin.org`, the server
echoed back `"Authorization": "Bearer fabricator-token-123"`, and the control without a secret did not.

## 1.3 ⚠⚠ httpfs IS A HARD PREREQUISITE — and I shipped this without noticing, because our own binaries hide it

**MEASURED against a stock DuckDB 1.5.5 wheel with only fabricator loaded: every request, GET included,
failed with `'https' scheme is not supported`** — an error naming neither httpfs nor the fix.

`HTTPUtil::Get(db)` returns whatever sits in `DBConfig`, and **only httpfs' `Load` calls `SetHTTPUtil`**.
The built-in fallback has no TLS client compiled in, implements GET alone, and — the half that would have
been SILENT rather than loud — its `HTTPParams::Initialize` reads proxy and logging and **no secrets at
all**. So a configuration where it half-worked would have applied no credential while looking fine.

⚠ **Why the whole gate and every live probe passed anyway:** `extension_config.cmake` statically links
httpfs into our test binaries and shell, so it is always loaded there. The suite even says `require httpfs`.
**Nothing in either tier can reach the unloaded case**, which is exactly the shape the shipped single-file
artifact has. Found only because the prerequisite question was asked directly, and answered by running the
real shipped configuration.

**Fix: auto-load httpfs at REQUEST time** (never during `Extension::Load`, which has its own chain-loading
locking rules), and REFUSE with instructions if it cannot be loaded. Deliberately not a fallback onto the
built-in client — that would authenticate nothing while appearing to work.

⚠ `TryAutoLoadExtension` **ignores `autoload_known_extensions`**: read it, and it consults
`autoinstall_known_extensions` only to decide whether to INSTALL, then loads unconditionally. So an
already-installed httpfs comes up even with autoloading off, and the refusal is reached only when httpfs is
neither loaded nor installable. Both legs measured with the wheel; **neither is reachable from
sqllogictest**, so neither is gated.

## 1.4 What was already possible without any of this

⚠ Worth stating so the increment is not overstated: **the existing host-FS bridge could already do an
authenticated HTTPS GET from C#, with zero new ABI.** MEASURED —
`fabricator_fs_spike('https://httpbin.org/get')` returns the body, because `fs_open_read` goes through
DuckDB's `FileSystem`, where httpfs registers `HTTPFileSystem` as a subsystem for `https://`.

So `http_request` does **not** add "HTTP from managed code". What it adds is everything a REST call needs
beyond a GET of a URL: **methods** (PUT/POST/DELETE/HEAD), **request headers**, the **status code and
response headers** as data rather than an exception, and a **request body**. A filesystem read has none of
those — a 404 is an exception, and there is nowhere to put a header.

⚠ And httpfs registers **no general-purpose HTTP SQL function** to reuse instead: its `Load` registers three
filesystems, a log type, the secret-creation functions, and `SetHTTPUtil`. The nearest SQL-level
alternatives are `read_blob('https://…')` / `read_text('https://…')`, which are GET-only for the same
reason.

## 2. The gating question is answered: `HTTPUtil` is fully reachable from a loadable extension

§7 of the design version called this "the gating question — everything above assumes it is reachable".

⚠ **It was never actually gating, and calling it "closed" overstated the discovery.** `EXTENSION_STATIC_BUILD=1`
has been our configure line since the beginning, and `extension_build_tools.cmake` links **`duckdb_static`
into the extension binary** under it, after which `winapi.hpp` defines `DUCKDB_API` as EMPTY. So the `DUCKDB_API` marking is
irrelevant to us and every DuckDB symbol is available — which is why this extension already calls
`Catalog`, `ClientContext` and `FileSystem` freely. `HTTPUtil` needed no new export and no upstream change.

So the honest account is that the doubt was **mis-founded**, not resolved: it was seeded by the v73 note
that DuckDB's vendored yyjson "is not `DUCKDB_API`-exported, so a loadable cannot link it" — a reason that
file already records as an overclaim (the real obstacle there was C++ NAMESPACING, `duckdb_yyjson` vs plain
`yyjson_*`). **A doubt inherited from a neighbouring case needs its own check before it becomes a gate.**
The real prerequisite was somewhere else entirely — §1.3.

⚠ That is a property of the STATIC extension build. An extension built with `EXTENSION_STATIC_BUILD=0`
resolves DuckDB symbols from the host at load, where the export table does matter.

## 3. The shape: a terminal `HttpMessageHandler` over a sync core

`DuckDbHttpHandler` subclasses `HttpMessageHandler` directly — not `DelegatingHandler`. It is the TERMINAL
handler; there is nothing below it. Everything above composes normally (`HttpClient`,
`IHttpClientFactory`, auth and retry handlers), because to .NET it is just a handler.

```csharp
using var client = DuckDbHttpHandler.CreateClient(new Uri("https://api.example.com"));
var json = await client.GetStringAsync("/v1/things");
```

### 3.1 The sync/async inversion

DuckDB's HTTP call blocks; `SendAsync` must return a `Task`. There is no way to make a blocking C call
asynchronous, so the only question is **which thread blocks**:

| option | verdict |
|---|---|
| block the caller's thread inside `SendAsync` | ⚠ **Wrong.** `SendAsync` is contractually async; blocking deadlocks a constrained caller and makes `HttpClient.Timeout` inert. |
| `Task.Run(…)` — a pool thread per request | **What is built.** One pool thread per in-flight request, exactly the cost the sync stack imposes, and `SendAsync` stays honest for everything above it. |
| a dedicated scheduler / bounded pool | Only once pool starvation is measurable. Not measured; not built. |

⚠ **The same trade the bridge already makes in the other direction**, with the polarity reversed: an ASYNC
.NET surface over a SYNC core. The convention holds either way — **exactly one blocking point, at the
boundary, never per-await.**

### 3.2 ⚠⚠ Ownership and the ambient — I HAD THIS BACKWARDS, and the wrong answer is memory-unsafe

The first version captured the opener (the host `ClientContext *`) **at construction**, and this file said
that was load-bearing. **It is the opposite.** The handler now holds **no opener at all**: the transport
resolves the ambient PER REQUEST (`HostHttpTransport`).

The error was found by asking how a PLUGIN would receive the handler. The obvious answer — construct it in
`OpenCatalog` and hand it over — captures the ATTACH statement's `ClientContext`. But **a catalog is
DATABASE-scoped and outlives the connection that attached it**, so that pointer dangles the moment that
connection closes: the `table_stats` SIGSEGV class, and exactly what `DuckDbTableFileSystem`'s own comment
predicts about a cached opener ("safe because no object outlives its call today — load-bearing the moment
something is cached"). A catalog-lifetime handler IS such an object.

⚠ **The trap it was confused with is a DIFFERENT one.** `fabricator_install_plugin` read an ambient inside
an async ITERATOR body, which runs at a later crossing where the ambient is legitimately 0 — and the fix
there is to capture in `Execute()` and **RE-ESTABLISH** it in the iterator, not to hold a raw pointer past
its owner's life. Both rules stand and they are not the same rule:

| | |
|---|---|
| a value read from an ambient (a session id, an opener you use *within the crossing*) | capture in `Execute()`, re-establish in the iterator |
| a POINTER whose owner may die (`ClientContext *`) held by a long-lived object | never hold it — resolve per use |

Resolving per request is also the more CORRECT answer for secrets, which the user may create after the
ATTACH and per session.

`HttpClient` fans many concurrent requests into one handler, so `SendAsync` is re-entrant — and since the
handler now holds nothing at all, one instance may safely back any number of clients for a catalog's life.

## 4. What was found by building it

Every item here was found by RUNNING the transport, not by reading. Three of the five are upstream
behaviours a caller has to compensate for.

1. **⚠ A secret's `extra_http_headers` were sent TWICE.** MEASURED on the very first live request: a secret
   carrying `{'X-Fab':'yes'}` reached the server as `X-Fab: yes,yes`. `BaseRequest`'s constructor ALWAYS
   runs `MergeHeaders(headers, params)`, folding `params.extra_headers` into the request — and then httpfs'
   clients add them AGAIN unless `HTTPFSParams::pre_merged_headers` is set, **which defaults to false**.
   Every in-tree caller sets it true (httpfs' `AddHandleHeaders`, `S3FileSystem`), so the default is only
   correct for a caller that bypasses the base constructor, and there is no such caller.
   - We do **not** set that flag: it lives on `HTTPFSParams`, not `HTTPParams`, so reaching it needs a
     downcast that is valid only when httpfs is loaded — a release-mode `reinterpret_cast` onto the wrong
     type otherwise. Instead we merge the extra headers ourselves and **clear the set**, which is correct
     for both param shapes and cannot be invalidated by a client we do not control.
   - Insertion order preserves DuckDB's own precedence (a secret's header outranks the caller's), which
     matters for a plugin setting its own `Authorization`: an `extra_http_headers` secret naming
     `Authorization` beats it.
2. **⚠ POST's response body arrives in `PostRequestInfo::buffer_out`, NOT in `HTTPResponse::body`** —
   verified in BOTH httpfs clients (httplib appends via a `content_receiver`, curl assigns
   `request_info->body`). Reading `response->body` alone hands back an empty body for every POST while
   every other method works: a silent, method-specific hole. The host copies it across.
3. **⚠ `try_request` is NARROWER than it looks, and a MUTANT is what established that.** Setting it FALSE
   left the gate at 21/21. DuckDB's retry loop returns any NON-retryable response directly whatever the
   flag says, so 404/401/403 were always rows; the flag governs only the RETRYABLE set (408, 418, 429, 500,
   503, 504 and transport errors), which would otherwise throw after the last attempt. **It is therefore
   NOT GATED** — producing an exhausted-retry 500 needs a server that returns one on demand, and no local
   rig here does.
4. **⚠ `HttpRequestHeaders.Remove` THROWS "Misused header name" for a content header**, where
   `TryAddWithoutValidation` merely returns false. So a remove-then-add cannot be hoisted above the
   bag-selection `if`. Found by a `Content-Type` arriving at the server as the JOINED
   `text/plain; charset=utf-8, application/json`, because `TryAddWithoutValidation` APPENDS and
   `StringContent` had already set one.
5. **⚠ Only five methods exist.** DuckDB's `RequestType` is GET / PUT / HEAD / DELETE / POST, so PATCH,
   OPTIONS and TRACE are not expressible at all. The host refuses by name and lists the five — quietly
   sending a PATCH as a POST would corrupt a write while looking like it worked.

## 5. What crosses, and what cannot

- **Headers: ONE VALUE PER NAME, in both directions, and that is DuckDB's model** — `HTTPHeaders` is a
  case-insensitive MAP, so a repeated header cannot cross. The handler joins repeats with `", "` on the way
  out (correct for every list-valued header and WRONG for `Set-Cookie`, which is why cookies are not
  supported here). The ABI carries them as a JSON object, which is a faithful rendering of that limit.
- **Bodies are FULLY BUFFERED, both ways.** DuckDB's own `HTTPResponse::body` is a `std::string`, so there
  is no streaming to inherit; a paging REST reader must page, not stream. (A GET *could* stream through
  `content_handler` — deliberately not done, because it buys nothing for a REST call and complicates the
  ABI.) The response body crosses as a raw buffer beside the JSON envelope rather than base64 inside it.
- **Redirects ARE followed** (`follow_location` defaults true, on all three clients).
- **Decompression is NOT done** — all three clients explicitly disable it. In practice this is harmless
  because nothing advertises `Accept-Encoding`; a caller that adds one must decompress itself.
- **Not inherited from `HttpClientHandler`:** the cookie container and client certificates.
- **Error split:** a TRANSPORT failure (DNS, connect, TLS) crosses inside the envelope's `"error"` and the
  handler raises `HttpRequestException`; an HTTP status the server returned is a normal response. A
  non-zero ABI return means the request could not be ATTEMPTED at all.

## 6. The plugin-facing surface — RESOLVED: the handler lives in the contract assembly

`DuckDbHttpHandler` and `HostHttpTransport` are in **`Fabricator.Abstractions`**; the bridge fills in
`HostHttpTransport.Send` at boot. **A plugin therefore reaches the transport with the reference it already
has**, and needs no `Fabricator.Bridge` reference, no `OpenCatalog` signature change, and no contact with an
opener or an ambient:

```csharp
using var client = DuckDbHttpHandler.CreateClient(new Uri("https://api.example.com"));
var json = await client.GetStringAsync("/v1/things");
```

MEASURED: a plugin referencing only `Fabricator.Abstractions` compiles against it and its build output stays
at three files — nothing leaks.

**Why this beat the `OpenCatalog` hand-in this file previously recommended.** Handing over a ready-bound
handler means binding it to something at ATTACH time, and the only thing available then is the attaching
connection's `ClientContext` — which the catalog outlives (§3.2). The hand-in would have institutionalised
the dangling pointer. Making the transport ambient-resolved removes the need to hand anything over at all.

⚠ **The one consequence to state plainly:** the handler is usable only from inside an ABI crossing, or
anywhere the ambient still flows from one. `AsyncLocal` survives `await` and `Task.Run`, so ordinary
provider code is fine; a thread parked before the crossing began is not. With no ambient the request fails
with a message saying so, rather than silently sending an unauthenticated one.

⚠ **Referencing `Fabricator.Bridge` from a plugin is no longer necessary, and that is worth knowing because
it is not otherwise harmful** — measured: `Private="false"` keeps Bridge and Abstractions out of the plugin
output, the two assemblies expose comparable public surface (47 vs 43 types), and Bridge is guaranteed
loaded because `clr_host.cpp` hardcodes it. The costs are a clean build going 6.1 s → 9.7 s with four extra
packages, a **transitive `Fabricator.Installer.Core.dll` that copies into the plugin output** (and which
neither `ExcludeAssets="runtime"` nor `PrivateAssets="all"` on the reference suppresses — those govern NuGet
asset flow, not ProjectReference copy-local), and coupling to a surface that changes every session. Not
disqualifying; simply unnecessary now.

⚠ **Thread safety of the C++ side under fan-out** remains REASONED, not measured. `HTTPUtil::Request` makes
its own client per call and httpfs' connection cache is per-pool mutexed; reading settings off a
`ClientContext` from a pool thread is what every `HostFs` call already does. Neither is a measurement.

## 7. NOT a prerequisite: the storage read path is a different problem

CLAUDE.md records a "measure these first" list — the unexplained 180 ms-vs-3 ms ranged-read asymmetry, and
the fact that the host-FS bridge already routes reads through DuckDB's stack with zero new ABI. **That list
is about remote FILE reads and does not gate this.** A REST call has no file, no range request and nothing
`ExternalFileCache` could serve: the asymmetry is a property of `DataLakeFileClient` reading a blob, and
the host-FS bridge answers "read this path", which a REST API does not have. Kept as a pointer only, so the
two are not conflated again.

## 8. The SQL surface

```sql
SELECT status, reason, headers, body_bytes, body
FROM fabricator_http_request('<url>' [, method := 'GET'] [, headers := '{"K":"v"}'] [, body := '…']);
```

Its job is to make the transport OBSERVABLE — that a request picked up the secret whose SCOPE matched its
URL, that `ca_cert_file` reached it — none of which is visible from C#. Without it the only way to check
any of this would be to build a plugin first. It reports the OUTCOME rather than throwing on a non-2xx, so
a 401 is a row you can look at; only a transport failure is an error. `body` is NULL when the bytes are not
valid UTF-8, rather than mojibake.
