/* duckdb_arrow_scan: SELECTing a NON-PREFIX subset of the stream's columns crashes.
 *
 * Pure C, public C API only, no extensions. Registers a 3-column Arrow stream (utf8, int64, utf8) as a view
 * via duckdb_arrow_scan, then queries it four ways that differ ONLY in the projection.
 *
 *   SELECT a0, a1, a2   (all)          -> ok      <- positive control
 *   SELECT a0, a1       (prefix)       -> ok      <- positive control
 *   SELECT a0, a2       (skips col 1)  -> CRASH
 *   SELECT a1, a2       (skips col 0)  -> CRASH
 *
 * The Arrow array is hand-built here, so no producer library is involved. Build (MSVC):
 *   cl /nologo /EHsc /I<duckdb>/src/include arrow_scan_repro.c /link duckdb_static.lib ... ws2_32.lib
 */
#include "duckdb.h"
#include <stdio.h>
#include <stdlib.h>
#include <string.h>

/* ---- minimal Arrow C data interface (as in duckdb.h's arrow section) ---- */
struct AS {
	const char *format, *name, *metadata;
	int64_t flags, n_children;
	struct AS **children, *dictionary;
	void (*release)(struct AS *);
	void *private_data;
};
struct AA {
	int64_t length, null_count, offset, n_buffers, n_children;
	const void **buffers;
	struct AA **children, *dictionary;
	void (*release)(struct AA *);
	void *private_data;
};
struct AST {
	int (*get_schema)(struct AST *, struct AS *);
	int (*get_next)(struct AST *, struct AA *);
	const char *(*get_last_error)(struct AST *);
	void (*release)(struct AST *);
	void *private_data;
};

static void rel_s(struct AS *s) { s->release = NULL; }
static void rel_a(struct AA *a) { a->release = NULL; }

/* two rows: ("x", 1, "px"), ("y", 2, "py") */
static const int32_t OFF0[3] = {0, 1, 2};
static const char DATA0[3] = {'x', 'y', 0};
static const int64_t I1[2] = {1, 2};
static const int32_t OFF2[3] = {0, 2, 4};
static const char DATA2[5] = {'p', 'x', 'p', 'y', 0};

static struct AS c0, c1, c2, *kids_s[3];
static struct AA a0, a1, a2, *kids_a[3];
static const void *buf0[3], *buf1[2], *buf2[3], *buf_root[1];
static int served;

static int get_schema(struct AST *st, struct AS *out) {
	(void)st;
	c0 = (struct AS) {"u", "a0", NULL, 2, 0, NULL, NULL, rel_s, NULL};
	c1 = (struct AS) {"l", "a1", NULL, 2, 0, NULL, NULL, rel_s, NULL};
	c2 = (struct AS) {"u", "a2", NULL, 2, 0, NULL, NULL, rel_s, NULL};
	kids_s[0] = &c0; kids_s[1] = &c1; kids_s[2] = &c2;
	memset(out, 0, sizeof(*out));
	out->format = "+s"; out->flags = 2; out->n_children = 3; out->children = kids_s; out->release = rel_s;
	return 0;
}

static int get_next(struct AST *st, struct AA *out) {
	(void)st;
	memset(out, 0, sizeof(*out));
	if (served++) { return 0; } /* one batch, then EOF */
	buf0[0] = NULL; buf0[1] = OFF0; buf0[2] = DATA0;
	buf1[0] = NULL; buf1[1] = I1;
	buf2[0] = NULL; buf2[1] = OFF2; buf2[2] = DATA2;
	a0 = (struct AA) {2, 0, 0, 3, 0, buf0, NULL, NULL, rel_a, NULL};
	a1 = (struct AA) {2, 0, 0, 2, 0, buf1, NULL, NULL, rel_a, NULL};
	a2 = (struct AA) {2, 0, 0, 3, 0, buf2, NULL, NULL, rel_a, NULL};
	kids_a[0] = &a0; kids_a[1] = &a1; kids_a[2] = &a2;
	buf_root[0] = NULL;
	out->length = 2; out->n_buffers = 1; out->n_children = 3;
	out->buffers = buf_root; out->children = kids_a; out->release = rel_a;
	return 0;
}

static const char *get_err(struct AST *st) { (void)st; return NULL; }
static void rel_st(struct AST *st) { st->release = NULL; }

static void run(duckdb_database db, const char *sql) {
	duckdb_connection con;
	struct AST st;
	duckdb_result res;
	printf("  %-34s ... ", sql); fflush(stdout);
	duckdb_connect(db, &con);
	served = 0;
	memset(&st, 0, sizeof(st));
	st.get_schema = get_schema; st.get_next = get_next; st.get_last_error = get_err; st.release = rel_st;
	if (duckdb_arrow_scan(con, "v", (duckdb_arrow_stream)&st) != DuckDBSuccess) {
		printf("register FAILED\n"); duckdb_disconnect(&con); return;
	}
	if (duckdb_query(con, sql, &res) != DuckDBSuccess) {
		printf("ERROR: %s\n", duckdb_result_error(&res));
	} else {
		printf("ok, %lld rows\n", (long long)duckdb_row_count(&res));
	}
	duckdb_destroy_result(&res);
	duckdb_disconnect(&con);
	fflush(stdout);
}

int main(void) {
	duckdb_database db;
	duckdb_open(NULL, &db);
	printf("duckdb %s\n", duckdb_library_version());
	printf("positive controls:\n");
	run(db, "SELECT a0, a1, a2 FROM v");
	run(db, "SELECT a0, a1 FROM v");
	printf("subject (non-prefix projection):\n");
	run(db, "SELECT a0, a2 FROM v");
	run(db, "SELECT a1, a2 FROM v");
	printf("all four completed\n");
	duckdb_close(&db);
	return 0;
}
