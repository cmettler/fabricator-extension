// Copyright (c) Christoph Mettler and contributors.
// SPDX-License-Identifier: Apache-2.0
// See LICENSE in the project root for license information.

//===----------------------------------------------------------------------===//
//                         fabricator — host HTTP (impl)
//===----------------------------------------------------------------------===//

#include "fabricator_http.hpp"

#include "fabricator/abi.h"
#include "fabricator/clr_host.hpp"

#include "duckdb/common/file_opener.hpp"
#include "duckdb/common/http_util.hpp"
#include "duckdb/common/string_util.hpp"
#include "duckdb/main/client_context.hpp"
#include "duckdb/main/database.hpp"
#include "duckdb/main/extension_helper.hpp"

// yyjson (vcpkg) — OUR OWN copy, the same one FabricatorRenderAlterJson uses. Deliberately not DuckDB's
// vendored `duckdb_yyjson` (C++-namespaced and not DUCKDB_API-exported, so a loadable cannot link it).
#include <yyjson.h>

#include <cstdlib>
#include <cstring>

namespace duckdb {

namespace {

// Duplicate into a malloc'd C string for the managed side (freed via the host services' free_str, which is
// plain free()). Same convention as the fs_* services.
char *DupStr(const string &s) {
	char *out = static_cast<char *>(malloc(s.size() + 1));
	if (out) {
		memcpy(out, s.c_str(), s.size() + 1);
	}
	return out;
}

RequestType ParseMethod(const char *method) {
	string m = method ? StringUtil::Upper(string(method)) : string();
	if (m == "GET") {
		return RequestType::GET_REQUEST;
	}
	if (m == "PUT") {
		return RequestType::PUT_REQUEST;
	}
	if (m == "HEAD") {
		return RequestType::HEAD_REQUEST;
	}
	if (m == "DELETE") {
		return RequestType::DELETE_REQUEST;
	}
	if (m == "POST") {
		return RequestType::POST_REQUEST;
	}
	// DuckDB's RequestType has exactly these five, so PATCH / OPTIONS / TRACE are not expressible through
	// this transport at all. Refuse by name rather than mapping PATCH onto POST: a plugin that needs one
	// must know it has to fall back to its own HttpClient, and a silent substitution would corrupt writes.
	throw InvalidInputException("fabricator: HTTP method '%s' is not supported by DuckDB's HTTP layer "
	                            "(GET, PUT, HEAD, DELETE and POST are)",
	                            method ? method : "");
}

// Parse the request headers, which cross as a JSON object {"Name":"value", …}.
//
// ⚠ ONE VALUE PER NAME, and that is DuckDB's model rather than a shortcut of ours: HTTPHeaders is a
// case_insensitive_map_t<string>, so a REPEATED header (Set-Cookie is the everyday example) cannot be
// represented in either direction. A JSON object is a faithful rendering of what this transport can carry;
// the managed handler joins repeats with ", " before sending and documents the loss on the way back.
void ParseHeaders(const char *headers_json, HTTPHeaders &out) {
	if (!headers_json || !*headers_json) {
		return;
	}
	yyjson_doc *doc = yyjson_read(headers_json, strlen(headers_json), 0);
	if (!doc) {
		throw InvalidInputException("fabricator: http_request headers are not valid JSON");
	}
	auto *root = yyjson_doc_get_root(doc);
	if (!root || !yyjson_is_obj(root)) {
		yyjson_doc_free(doc);
		throw InvalidInputException("fabricator: http_request headers must be a JSON object");
	}
	size_t idx, max;
	yyjson_val *key, *val;
	yyjson_obj_foreach(root, idx, max, key, val) {
		auto *k = yyjson_get_str(key);
		auto *v = yyjson_get_str(val);
		if (k && v) {
			out.Insert(string(k), string(v));
		}
	}
	yyjson_doc_free(doc);
}

// Render the response envelope: everything about the response EXCEPT its body, which crosses as a raw buffer
// beside it (a body is bytes, and base64-ing it through JSON would double it in memory for nothing).
string RenderResponse(const HTTPResponse &response) {
	struct MutDocGuard {
		yyjson_mut_doc *doc;
		MutDocGuard() : doc(yyjson_mut_doc_new(nullptr)) {
			if (!doc) {
				throw IOException("fabricator: could not allocate the HTTP response document");
			}
		}
		~MutDocGuard() {
			yyjson_mut_doc_free(doc);
		}
	} guard;
	auto *doc = guard.doc;
	auto *root = yyjson_mut_obj(doc);
	yyjson_mut_doc_set_root(doc, root);

	auto add_str = [&](const char *key, const string &value) {
		// yyjson_mut_strncpy, NOT yyjson_mut_strn — the plain form does not copy (see fabricator_metadata.cpp).
		yyjson_mut_obj_add_val(doc, root, key, yyjson_mut_strncpy(doc, value.c_str(), value.size()));
	};

	yyjson_mut_obj_add_int(doc, root, "status", static_cast<int64_t>(response.status));
	add_str("reason", response.reason);
	add_str("url", response.url);
	yyjson_mut_obj_add_bool(doc, root, "success", response.success);
	// A TRANSPORT failure (DNS, connect, TLS) as opposed to an HTTP status the server actually returned.
	// The managed handler turns a non-empty error into HttpRequestException and everything else into a real
	// HttpResponseMessage — exactly the split .NET callers above an HttpMessageHandler expect.
	add_str("error", response.request_error);

	auto *headers = yyjson_mut_obj(doc);
	for (auto &entry : response.headers) {
		yyjson_mut_obj_add(headers, yyjson_mut_strncpy(doc, entry.first.c_str(), entry.first.size()),
		                   yyjson_mut_strncpy(doc, entry.second.c_str(), entry.second.size()));
	}
	yyjson_mut_obj_add_val(doc, root, "headers", headers);

	char *rendered = yyjson_mut_write(doc, 0, nullptr);
	if (!rendered) {
		throw IOException("fabricator: could not render the HTTP response envelope");
	}
	string json(rendered);
	free(rendered); // yyjson_mut_write allocates with the default allocator
	return json;
}

// The host service.
//
// `opener` is the calling operator's ClientContext, and it does two things: it selects the HTTPUtil (httpfs'
// curl/httplib client when httpfs is loaded, the built-in GET-only one otherwise) and — the reason this
// transport is worth having at all — it resolves the `TYPE http` SECRET matching this URL plus every http_*
// setting. Passing the URL through InitializeParameters IS the whole mechanism: httpfs builds a
// KeyValueSecretReader over the FileOpenerInfo's file_path, so the secret's SCOPE prefix is matched against
// the request URI with no work of ours.
int32_t HostHttpRequest(FabricatorHandle opener, const char *method, const char *url, const char *headers_json,
                        const void *body, int64_t body_length, char **out_response_json, void **out_body,
                        int64_t *out_body_length, char **err) {
	if (out_response_json) {
		*out_response_json = nullptr;
	}
	if (out_body) {
		*out_body = nullptr;
	}
	if (out_body_length) {
		*out_body_length = 0;
	}
	try {
		auto *ctx = reinterpret_cast<ClientContext *>(opener);
		if (!ctx) {
			throw InvalidInputException("fabricator: http_request requires a client context (no ambient opener)");
		}
		if (!url || !*url) {
			throw InvalidInputException("fabricator: http_request requires a url");
		}
		auto type = ParseMethod(method);

		// ⚠⚠ httpfs IS A HARD PREREQUISITE, and without it this entry does NOT degrade — it dies.
		// MEASURED against a stock DuckDB 1.5.5 wheel with only fabricator loaded: every request, GET
		// included, failed with **"'https' scheme is not supported"** — an error naming neither httpfs nor
		// the fix. `HTTPUtil::Get` returns whatever sits in DBConfig, and ONLY httpfs' Load calls
		// SetHTTPUtil; the built-in fallback has no TLS client compiled in, implements GET alone, and — the
		// half that would be silent rather than loud — its HTTPParams::Initialize reads proxy and logging
		// and NO SECRETS AT ALL. So a build where it half-worked would apply no credential while looking
		// fine. Our own binaries link httpfs statically so it is always loaded there, which is exactly why
		// the whole gate and every live probe passed without this.
		//
		// Auto-load on demand, at REQUEST time — never during Extension::Load, where chain-loading has its
		// own locking rules.
		// ⚠ `TryAutoLoadExtension` IGNORES `autoload_known_extensions` — read it: it consults
		// `autoinstall_known_extensions` only to decide whether to INSTALL, then LOADS unconditionally. So an
		// already-installed httpfs comes up even with autoloading switched off, and the refusal below is
		// reached only when httpfs is neither loaded nor installable (MEASURED with a fresh
		// `extension_directory` + autoinstall off). It is deliberately a REFUSAL and not a fallback onto the
		// built-in client, which would authenticate nothing while appearing to work.
		if (HTTPUtil::Get(*ctx->db).GetName() == "Built-In") {
			ExtensionHelper::TryAutoLoadExtension(*ctx, "httpfs");
			if (HTTPUtil::Get(*ctx->db).GetName() == "Built-In") {
				throw InvalidConfigurationException(
				    "fabricator: http_request needs the httpfs extension, which is not loaded and could not "
				    "be auto-loaded. Run `INSTALL httpfs; LOAD httpfs;` first. (DuckDB's built-in HTTP client "
				    "supports no TLS, only GET, and reads no secrets, so it is deliberately not used as a "
				    "fallback: it would authenticate nothing while appearing to work.)");
			}
		}

		auto &http_util = HTTPUtil::Get(*ctx->db);
		auto params = http_util.InitializeParameters(*ctx, url);
		HTTPHeaders headers;
		ParseHeaders(headers_json, headers);

		// ⚠ MERGE THE SECRET'S extra_http_headers OURSELVES, THEN CLEAR THEM — otherwise every one of them is
		// sent TWICE. MEASURED on the first live request: a secret carrying {'X-Fab':'yes'} arrived at the
		// server as `X-Fab: yes,yes`.
		//
		// The mechanism is a default that is wrong for every direct caller. `BaseRequest`'s constructor ALWAYS
		// runs MergeHeaders(headers, params), folding params.extra_headers into the request — and then httpfs'
		// clients add them AGAIN unless `HTTPFSParams::pre_merged_headers` is set, which DEFAULTS TO FALSE.
		// Every in-tree caller sets it true (httpfs' AddHandleHeaders, S3FileSystem), so the default is only
		// correct for a caller that bypasses the base constructor, and there is no such caller.
		//
		// We deliberately do NOT set that flag: it lives on HTTPFSParams, not HTTPParams, so reaching it needs
		// a downcast that is only valid when httpfs is loaded — a release-mode reinterpret_cast onto the wrong
		// type otherwise. Emptying the set instead is correct for BOTH shapes and cannot be invalidated by a
		// client we do not control.
		//
		// Insertion order preserves DuckDB's own precedence (MergeHeaders lets extra_headers win over the
		// caller's), so a secret's header still overrides one the caller set — which matters for a plugin that
		// sets its own Authorization: an extra_http_headers secret naming Authorization outranks it.
		for (auto &extra : params->extra_headers) {
			headers[extra.first] = extra.second;
		}
		params->extra_headers.clear();

		auto data = reinterpret_cast<const_data_ptr_t>(body);
		auto len = body ? static_cast<idx_t>(body_length) : 0;
		// PutRequestInfo holds `const string &content_type` — a REFERENCE — so this must outlive the request.
		string content_type = headers.HasHeader("Content-Type") ? headers.GetHeaderValue("Content-Type") : string();

		unique_ptr<HTTPResponse> response;
		// try_request: return a failed request instead of throwing once retries are exhausted.
		//
		// ⚠ It is NARROWER than it looks, and a mutant is what established that: setting it FALSE does not
		// make a 404 throw. DuckDB's retry loop returns any NON-RETRYABLE response directly, whatever this
		// flag says, so 404/401/403 are already rows. What the flag governs is the RETRYABLE set — 408, 418,
		// 429, 500, 503, 504 and every transport error — which would otherwise throw an HTTPException after
		// the last attempt. An HttpMessageHandler must hand a 500 back as a 500, so the flag is still
		// required; it just covers a smaller case than "non-2xx".
		// ⚠ NOT GATED: producing an exhausted-retry 500 needs a server that returns one on demand, which no
		// local rig here does. See verify_http_transport.test §3.
		switch (type) {
		case RequestType::GET_REQUEST: {
			// Both handlers null on purpose: the clients take the buffered path when neither is set, so the
			// whole body lands in response->body. Streaming a GET is possible through content_handler and is
			// deliberately not done here — see docs/http-transport.md §3.5.
			GetRequestInfo info(url, headers, *params, nullptr, nullptr);
			info.try_request = true;
			response = http_util.Request(info);
			break;
		}
		case RequestType::PUT_REQUEST: {
			PutRequestInfo info(url, headers, *params, data, len, content_type);
			info.try_request = true;
			response = http_util.Request(info);
			break;
		}
		case RequestType::HEAD_REQUEST: {
			HeadRequestInfo info(url, headers, *params);
			info.try_request = true;
			response = http_util.Request(info);
			break;
		}
		case RequestType::DELETE_REQUEST: {
			DeleteRequestInfo info(url, headers, *params);
			info.try_request = true;
			response = http_util.Request(info);
			break;
		}
		default: {
			PostRequestInfo info(url, headers, *params, data, len);
			info.try_request = true;
			response = http_util.Request(info);
			// ⚠ POST is the ONE method whose response body arrives in `buffer_out` rather than in
			// response->body — verified in BOTH httpfs clients (httplib appends via a content_receiver, curl
			// assigns request_info->body). Reading response->body alone would hand back an empty body for
			// every POST while every other method worked: a silent, method-specific hole.
			if (response && response->body.empty() && !info.buffer_out.empty()) {
				response->body = std::move(info.buffer_out);
			}
			break;
		}
		}

		if (!response) {
			throw IOException("fabricator: http_request produced no response for %s '%s'", method, url);
		}

		if (out_response_json) {
			*out_response_json = DupStr(RenderResponse(*response));
		}
		if (out_body && !response->body.empty()) {
			auto size = response->body.size();
			void *buffer = malloc(size);
			if (!buffer) {
				throw IOException("fabricator: could not allocate %llu bytes for the HTTP response body",
				                  static_cast<unsigned long long>(size));
			}
			memcpy(buffer, response->body.data(), size);
			*out_body = buffer;
			if (out_body_length) {
				*out_body_length = static_cast<int64_t>(size);
			}
		}
		return FABRICATOR_OK;
	} catch (std::exception &e) {
		if (err) {
			*err = DupStr(string(e.what()));
		}
		if (out_response_json && *out_response_json) {
			free(*out_response_json);
			*out_response_json = nullptr;
		}
		return FABRICATOR_ERROR;
	}
}

} // namespace

void RegisterHostHttp(ExtensionLoader &loader) {
	(void)loader; // nothing SQL-visible here — this registers a host service, not a function
	fabricator::SetHostHttpService(HostHttpRequest);
}

} // namespace duckdb
