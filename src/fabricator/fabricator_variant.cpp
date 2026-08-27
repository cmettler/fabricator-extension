// Copyright (c) Christoph Mettler and contributors.
// SPDX-License-Identifier: Apache-2.0
// See LICENSE in the project root for license information.

#include "fabricator/fabricator_variant.hpp"

#include "duckdb/common/arrow/arrow_converter.hpp"
#include "duckdb/common/arrow/arrow_type_extension.hpp"
#include "duckdb/common/arrow/schema_metadata.hpp"
#include "duckdb/common/types/data_chunk.hpp"
#include "duckdb/execution/expression_executor.hpp"
#include "duckdb/function/function_binder.hpp"
#include "duckdb/function/table/arrow/arrow_duck_schema.hpp"
#include "duckdb/main/config.hpp"
#include "duckdb/main/database.hpp"
#include "duckdb/planner/expression/bound_reference_expression.hpp"

namespace fabricator {

using namespace duckdb;

namespace {

// The transport is ONE self-delimiting binary value per row: the parquet-variant metadata bytes immediately
// followed by the value bytes (the variant metadata header carries its own size, so the two halves split
// without a length prefix — this is exactly the byte form the parquet extension's variant_bytes_to_variant
// consumes). A single-blob internal type is deliberate: DuckDB's ArrowAppender finalize walk passes the
// LOGICAL type (VARIANT, whose struct info has 4 children) to the child appender's finalize, so a NESTED
// internal type (the canonical struct<metadata,value>) crashes upstream ("index 2 within vector of size 2");
// a LEAF internal type sidesteps that entirely (the shape arrow.bool8 / geoarrow.wkb already exercise).
// Because this is NOT the canonical arrow.parquet.variant struct layout, the marker is our own name.
constexpr const char *kVariantExtensionName = "ew.variant_transport";

// Binds one of the parquet extension's variant conversion scalars over a single bound-reference arg and
// executes it over `input` (count rows). The functions live in the system catalog (parquet is statically
// linked), so a name-based bind resolves them without touching parquet internals. Returns the result vector
// typed as the bound expression's return type.
unique_ptr<Expression> BindVariantConversion(ClientContext &context, const char *function_name,
                                             const LogicalType &arg_type) {
	vector<unique_ptr<Expression>> args;
	args.push_back(make_uniq<BoundReferenceExpression>(arg_type, 0));
	FunctionBinder binder(context);
	ErrorData error;
	auto expr = binder.BindScalarFunction(DEFAULT_SCHEMA, function_name, std::move(args), error);
	if (!expr) {
		error.Throw();
	}
	return expr;
}

void ExecuteConversion(ClientContext &context, Expression &expr, Vector &input, idx_t count, Vector &result) {
	if (count > STANDARD_VECTOR_SIZE) {
		// The extension conversion machinery materializes internal vectors of one standard chunk (see
		// ColumnArrowToDuckDB / ArrowAppendData::AppendChild), so this cannot legitimately trigger; a clear
		// error beats silent corruption if a deeply nested container ever routes more rows here.
		throw NotImplementedException("VARIANT conversion over %llu rows exceeds one vector; a VARIANT deep "
		                              "inside a container type is not supported",
		                              static_cast<unsigned long long>(count));
	}
	DataChunk chunk;
	chunk.InitializeEmpty({input.GetType()});
	chunk.data[0].Reference(input);
	chunk.SetCardinality(count);
	ExpressionExecutor executor(context);
	executor.AddExpression(expr);
	executor.ExecuteExpression(chunk, result);
	result.Flatten(count);
}

// transport blob -> VARIANT (Arrow import / scan ingest).
void VariantArrowToDuck(ClientContext &context, Vector &source, Vector &result, idx_t count) {
	// The binary decoder rejects a null/empty metadata buffer outright (it does not consult validity), so
	// null rows are substituted with the minimal valid encoding — metadata v1 with an empty dictionary
	// (header 0x01, dictionary_size 0, one offset 0) ++ the primitive-null value header (0x00) — and
	// re-invalidated after the conversion.
	static constexpr uint8_t kNullVariant[4] = {0x01, 0x00, 0x00, 0x00};
	UnifiedVectorFormat src_fmt;
	source.ToUnifiedFormat(count, src_fmt);
	auto src_data = UnifiedVectorFormat::GetData<string_t>(src_fmt);
	// A valid variant blob is >= 2 bytes (metadata header ++ value header); a zero-length slot can only be
	// a NULL whose validity got lost along the crossing — treat it as NULL rather than feeding the decoder.
	auto row_is_null = [&](idx_t i) {
		auto sidx = src_fmt.sel->get_index(i);
		return !src_fmt.validity.RowIsValid(sidx) || src_data[sidx].GetSize() == 0;
	};

	Vector patched(LogicalType::BLOB, count);
	auto p_data = FlatVector::GetData<string_t>(patched);
	for (idx_t i = 0; i < count; i++) {
		if (row_is_null(i)) {
			p_data[i] = StringVector::AddStringOrBlob(
			    patched, string_t(reinterpret_cast<const char *>(kNullVariant), 4));
		} else {
			p_data[i] = StringVector::AddStringOrBlob(patched, src_data[src_fmt.sel->get_index(i)]);
		}
	}

	auto expr = BindVariantConversion(context, "variant_bytes_to_variant", LogicalType::BLOB);
	ExecuteConversion(context, *expr, patched, count, result);
	auto &result_validity = FlatVector::Validity(result);
	for (idx_t i = 0; i < count; i++) {
		if (row_is_null(i)) {
			result_validity.SetInvalid(i);
		}
	}
}

// VARIANT -> transport blob (Arrow export / appenders): variant_to_parquet_variant gives the unshredded
// struct<metadata, value>; the two halves are concatenated into the single transport blob.
void VariantDuckToArrow(ClientContext &context, Vector &source, Vector &result, idx_t count) {
	auto expr = BindVariantConversion(context, "variant_to_parquet_variant", LogicalType::VARIANT());
	Vector transport(expr->return_type, count);
	ExecuteConversion(context, *expr, source, count, transport);

	auto &entries = StructVector::GetEntries(transport);
	D_ASSERT(entries.size() >= 2);
	UnifiedVectorFormat meta_fmt, val_fmt;
	entries[0]->ToUnifiedFormat(count, meta_fmt);
	entries[1]->ToUnifiedFormat(count, val_fmt);
	auto meta_data = UnifiedVectorFormat::GetData<string_t>(meta_fmt);
	auto val_data = UnifiedVectorFormat::GetData<string_t>(val_fmt);
	auto &row_validity = FlatVector::Validity(transport);

	// The source may be dictionary/constant/sliced — resolve its validity per logical row.
	UnifiedVectorFormat src_fmt;
	source.ToUnifiedFormat(count, src_fmt);

	auto out_data = FlatVector::GetData<string_t>(result);
	auto &out_validity = FlatVector::Validity(result);
	for (idx_t i = 0; i < count; i++) {
		auto midx = meta_fmt.sel->get_index(i);
		auto vidx = val_fmt.sel->get_index(i);
		if (!src_fmt.validity.RowIsValid(src_fmt.sel->get_index(i)) || !row_validity.RowIsValid(i) ||
		    !meta_fmt.validity.RowIsValid(midx) || !val_fmt.validity.RowIsValid(vidx)) {
			out_validity.SetInvalid(i);
			continue;
		}
		auto &m = meta_data[midx];
		auto &v = val_data[vidx];
		auto combined = StringVector::EmptyString(result, m.GetSize() + v.GetSize());
		memcpy(combined.GetDataWriteable(), m.GetData(), m.GetSize());
		memcpy(combined.GetDataWriteable() + m.GetSize(), v.GetData(), v.GetSize());
		combined.Finalize();
		out_data[i] = combined;
	}
}

// Export schema: a plain binary field ("z") tagged with the extension name. The fixed format deliberately
// ignores the session's offset-size/view settings — the transport is part of the boundary contract.
void PopulateVariantSchema(DuckDBArrowSchemaHolder &root_holder, ArrowSchema &child, const LogicalType &type,
                           ClientContext &context, const ArrowTypeExtension &extension) {
	child.format = "z";
	const ArrowSchemaMetadata schema_metadata = ArrowSchemaMetadata::ArrowCanonicalType(kVariantExtensionName);
	root_holder.metadata_info.emplace_back(schema_metadata.SerializeMetadata());
	child.metadata = root_holder.metadata_info.back().get();
}

// Import schema: a field tagged ew.variant_transport resolves to DuckDB VARIANT; the ArrowType carries the
// string/binary layout info so the physical parse (into the internal BLOB vector) still works.
unique_ptr<ArrowType> GetVariantType(ClientContext &context, const ArrowSchema &schema,
                                     const ArrowSchemaMetadata &schema_metadata) {
	const string format = schema.format ? string(schema.format) : string();
	if (format == "z") {
		return make_uniq<ArrowType>(LogicalType::VARIANT(),
		                            make_uniq<ArrowStringInfo>(ArrowVariableSizeType::NORMAL));
	}
	if (format == "Z") {
		return make_uniq<ArrowType>(LogicalType::VARIANT(),
		                            make_uniq<ArrowStringInfo>(ArrowVariableSizeType::SUPER_SIZE));
	}
	throw InvalidInputException(
	    "ew.variant_transport expects a binary transport column (metadata||value), got format \"%s\"",
	    format.c_str());
}

} // namespace

void RegisterFabricatorVariantExtension(DatabaseInstance &db) {
	auto &config = DBConfig::GetConfig(db);
	if (config.HasArrowExtension(LogicalType::VARIANT())) {
		return; // already registered (static + loadable coexisting on one instance)
	}
	config.RegisterArrowExtension(
	    ArrowTypeExtension(kVariantExtensionName, PopulateVariantSchema, GetVariantType,
	                       make_shared_ptr<ArrowTypeExtensionData>(LogicalType::VARIANT(), LogicalType::BLOB,
	                                                               VariantArrowToDuck, VariantDuckToArrow)));
}

} // namespace fabricator
