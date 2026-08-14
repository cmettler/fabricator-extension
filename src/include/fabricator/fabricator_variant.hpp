//===----------------------------------------------------------------------===//
// fabricator — VARIANT over the Arrow C boundary
//
// DuckDB's VARIANT logical type has no Arrow C-data-interface representation (the exporter's default
// branch throws "Unsupported Arrow type VARIANT" unless an Arrow type extension is registered for it).
// RegisterFabricatorVariantExtension registers the `ew.variant_transport` extension so VARIANT crosses EVERY
// Arrow boundary in this process transparently as ONE self-delimiting binary value per row — the
// parquet-variant metadata bytes immediately followed by the value bytes — tagged with
// ARROW:extension:name. Export (bulk INSERT / CTAS / COPY appenders, host-query result streams,
// create-table schemas) AND import (scan ingest, the catalog bind schema via FetchTableSchema,
// host-query input streams) resolve through DuckDB's own ArrowTypeExtension machinery, so no
// per-operator pre-casts are needed anywhere.
//
// Why a single BLOB and not the canonical arrow.parquet.variant struct<metadata,value>: DuckDB's
// ArrowAppender finalize walk hands the LOGICAL type (VARIANT — 4 struct children) to the child
// appender's finalize, so a NESTED internal type crashes upstream; a LEAF internal type is the shape
// the built-in extensions (arrow.bool8, geoarrow.wkb) already exercise. Hence the non-canonical name.
//
// The value conversions delegate to the parquet extension's scalars (statically linked in this build):
//   VARIANT -> transport:  variant_to_parquet_variant(v), children concatenated (metadata || value)
//   transport -> VARIANT:  variant_bytes_to_variant(blob)   (its documented inverse — takes exactly
//                          the concatenated form; the variant metadata header is self-delimiting)
//===----------------------------------------------------------------------===//
#pragma once

namespace duckdb {
class DatabaseInstance;
} // namespace duckdb

namespace fabricator {

// Idempotent (safe under static + loadable double-registration on one instance).
void RegisterFabricatorVariantExtension(duckdb::DatabaseInstance &db);

} // namespace fabricator
