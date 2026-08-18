using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Apache.Arrow;
using Apache.Arrow.Types;

namespace Fabricator.Bridge;

/// <summary>
/// <c>SELECT * FROM fabricator_http_request('&lt;url&gt;' [, method := 'GET'] [, headers := '{"K":"v"}']
/// [, body := '…'])</c> — one HTTP request through DuckDB's own HTTP stack, over the same
/// <see cref="DuckDbHttpHandler"/> a plugin would use.
/// </summary>
/// <remarks>
/// <para>
/// Its job is to make the transport OBSERVABLE. The whole value of routing through DuckDB is invisible from
/// C# — that a request picked up the <c>TYPE http</c> secret whose SCOPE matched its URL, that
/// <c>ca_cert_file</c> reached it, that <c>http_retries</c> governed it — so without a SQL surface the only
/// way to check any of it would be to build a plugin first. A plugin author debugging "why is my call
/// unauthenticated" reaches for this before anything else.
/// </para>
/// <para>
/// It deliberately reports the OUTCOME rather than throwing on a non-2xx: <c>status</c> plus <c>headers</c>
/// plus the body, so a 401 is a row you can look at. A TRANSPORT failure (DNS, connect, TLS) is the one thing
/// that does surface as an error, which is the same split the handler makes for .NET callers.
/// </para>
/// </remarks>
internal sealed class HttpRequestFunction : ITableFunction
{
    public string Name => "fabricator_http_request";

    public Schema Parameters { get; } = new(new[]
    {
        Params.Positional("url", StringType.Default),
        Params.Named("method", StringType.Default),
        // A JSON object {"Name":"value", …}. One value per name — see DuckDbHttpHandler's remarks.
        Params.Named("headers", StringType.Default),
        // UTF-8 text; a binary body is out of scope for a diagnostic function.
        Params.Named("body", StringType.Default),
    }, metadata: null);

    public ITableFunctionBinding Bind(RecordBatch args) =>
        new Binding(ReadString(args, 0) ?? string.Empty, ReadString(args, 1), ReadString(args, 2),
                    ReadString(args, 3));

    private static string? ReadString(RecordBatch args, int ordinal) =>
        args.Column(ordinal) is StringArray a && a.Length > 0 && !a.IsNull(0) ? a.GetString(0) : null;

    private sealed class Binding : ITableFunctionBinding
    {
        private readonly string _url;
        private readonly string? _method;
        private readonly string? _headers;
        private readonly string? _body;

        public Binding(string url, string? method, string? headers, string? body)
        {
            _url = url;
            _method = method;
            _headers = headers;
            _body = body;
        }

        public Schema OutputSchema { get; } = new(new[]
        {
            new Field("status", Int32Type.Default, nullable: false),
            new Field("reason", StringType.Default, nullable: false),
            // The response headers as a JSON object, so a suite can assert one without a second function.
            new Field("headers", StringType.Default, nullable: false),
            new Field("body_bytes", Int64Type.Default, nullable: false),
            // The body decoded as UTF-8. NULL when it is not valid UTF-8 rather than mojibake — a diagnostic
            // that quietly corrupts what it shows you is worse than one that says it cannot show it.
            new Field("body", StringType.Default, nullable: true),
        }, metadata: null);

        public bool SupportsFilterPushdown => false;
        public bool SupportsProjectionPushdown => false;

        public void Dispose()
        {
        }

        // Dispose the pushed filter values in a PLAIN method, and CAPTURE THE AMBIENT OPENER here — this runs
        // inside the crossing that set it, while the iterator body runs at the first batch pull, on whatever
        // thread DuckDB pulls from, where AmbientOpener.Current is legitimately 0. The standing rule for every
        // global table function; see InstallPluginFunction, which learned it the expensive way.
        public IAsyncEnumerable<RecordBatch> Execute(TableFunctionScan scan, CancellationToken ct = default)
        {
            scan.FilterValues?.Dispose();
            return Rows(AmbientOpener.Current, ct);
        }

        private async IAsyncEnumerable<RecordBatch> Rows(nint opener, [EnumeratorCancellation] CancellationToken ct)
        {
            // Re-establish what Execute captured: the handler resolves the ambient PER REQUEST (it holds no
            // opener — see HostHttpTransport), and this iterator body runs at the first batch pull, a
            // different crossing on whatever thread DuckDB pulls from, where the ambient would be 0.
            AmbientOpener.Current = opener;
            ct.ThrowIfCancellationRequested();
            using var client = DuckDbHttpHandler.CreateClient();
            using var request = new HttpRequestMessage(new HttpMethod(_method ?? "GET"), _url);
            if (_body is not null)
            {
                request.Content = new StringContent(_body, Encoding.UTF8);
            }
            if (!string.IsNullOrEmpty(_headers))
            {
                using var doc = JsonDocument.Parse(_headers!);
                foreach (var header in doc.RootElement.EnumerateObject())
                {
                    var value = header.Value.GetString() ?? string.Empty;
                    if (!request.Headers.TryAddWithoutValidation(header.Name, value))
                    {
                        // Content headers (Content-Type above all) live on the content, not the request.
                        request.Content ??= new ByteArrayContent(System.Array.Empty<byte>());
                        // ⚠ REMOVE FIRST, and ONLY on the content bag. TryAddWithoutValidation APPENDS, and
                        // StringContent has already set Content-Type: text/plain; charset=utf-8 — so without
                        // this the server received the joined `text/plain; charset=utf-8, application/json`
                        // (measured). ⚠ The same Remove on the REQUEST bag THROWS "Misused header name",
                        // because Remove validates the name where TryAddWithoutValidation does not — which is
                        // why it cannot simply be hoisted above the if.
                        request.Content.Headers.Remove(header.Name);
                        request.Content.Headers.TryAddWithoutValidation(header.Name, value);
                    }
                }
            }

            using var response = await client.SendAsync(request, ct).ConfigureAwait(false);
            var bytes = await response.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);

            var status = new Int32Array.Builder();
            var reason = new StringArray.Builder();
            var headers = new StringArray.Builder();
            var bodyBytes = new Int64Array.Builder();
            var body = new StringArray.Builder();

            status.Append((int)response.StatusCode);
            reason.Append(response.ReasonPhrase ?? string.Empty);
            headers.Append(RenderHeaders(response));
            bodyBytes.Append(bytes.LongLength);
            body.Append(TryDecodeUtf8(bytes));

            yield return new RecordBatch(OutputSchema, new IArrowArray[]
            {
                status.Build(), reason.Build(), headers.Build(), bodyBytes.Build(), body.Build(),
            }, 1);
        }

        private static string RenderHeaders(HttpResponseMessage response)
        {
            using var buffer = new System.IO.MemoryStream();
            using (var writer = new Utf8JsonWriter(buffer))
            {
                writer.WriteStartObject();
                foreach (var header in response.Headers.Concat(response.Content.Headers))
                {
                    writer.WriteString(header.Key, string.Join(", ", header.Value));
                }
                writer.WriteEndObject();
            }
            return Encoding.UTF8.GetString(buffer.ToArray());
        }

        private static string? TryDecodeUtf8(byte[] bytes)
        {
            if (bytes.Length == 0)
            {
                return string.Empty;
            }
            try
            {
                return new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true)
                    .GetString(bytes);
            }
            catch (ArgumentException)
            {
                return null;
            }
        }
    }
}
