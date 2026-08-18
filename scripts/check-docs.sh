#!/usr/bin/env bash
# Verify the mechanically-checkable claims in our markdown: that the files, tests and doc links it cites
# actually exist, and that every doc is reachable from CLAUDE.md's index. Pure text, needs no build, runs in
# about a second. It found five real defects on its first run, including a doc pointing at a DELETED doc and
# two pointing at a test that the rename removed.
#
# WHAT IT CANNOT DO, and this matters more than what it can: it does not check whether prose is TRUE.
# docs/multifile-delta.md is the standing example — every path and suite name in it resolves, and its header
# still announces "Phase-A slices BUILDING" for an effort the production catalog path never adopted. Narrative
# staleness is caught by the human-maintained STATUS column in CLAUDE.md's doc index, not here. This script
# only guarantees that when a doc points AT something, the something exists.
#
# Usage: scripts/check-docs.sh      (exit 1 on any unresolved reference, 2 if it could not run)
set -uo pipefail
cd "$(dirname "$0")/.."

# python3 on Linux/macOS runners, python in this repo's Git Bash on Windows. Fail LOUDLY if neither is present:
# a doc check that silently does nothing is worse than no check, because it reads as a pass.
PY_BIN=$(command -v python3 || command -v python) || {
    echo "check-docs: no python interpreter found (tried python3, python)" >&2
    exit 2
}

"$PY_BIN" - "$@" <<'PY'
import os, re, sys, glob

FAIL = []

# Docs legitimately cite paths in OTHER repos: the sibling native mssql-extension (our compatibility target)
# and delta-rs' own tree. Those are real references, not rot, but nothing in their shape distinguishes them
# from our paths — so they are listed rather than guessed at. Keep this list SHORT: if it grows, that is a
# signal docs should be qualifying such references instead of relying on this file.
EXTERNAL = {
    'src/catalog/mssql_transaction.cpp',            # native mssql-extension (this repo has no TDS layer)
    'src/tds/tds_protocol.cpp',                     # ditto
    'test/sql/remote_pushdown/remote_pushdown_delete.test',  # ditto
    'src/DeltaLake/DeltaLake.cs',                   # delta-rs, referenced by the deltars provider doc
}

# A submodule may be absent (a docs-only CI job need not check out duckdb). Skips are REPORTED, never silent.
SUBMODULES = [b for b in ('duckdb/', 'engineered-wood/') if os.path.isdir(b) and os.listdir(b)]
MISSING = [b for b in ('duckdb/', 'engineered-wood/') if b not in SUBMODULES]

def resolves(p):
    return os.path.exists(p) or any(os.path.exists(b + p) for b in SUBMODULES)

def could_be_submodule(p):
    return bool(MISSING) and p.startswith(('src/', 'test/', 'extension/'))

def suppressed(txt, pos):
    """True if the line containing `pos` carries an inline `check-docs:ignore` marker.

    A doc legitimately names something that does not exist when the POINT of the sentence is that it does not
    exist: "planned as X; that file was never created", or ew-master-migration.md quoting a wrong suite name as
    the subject of a process-trap warning. Fixing those would destroy the passage. Deliberately an INLINE
    marker rather than a list in this script — it is deleted along with the text it guards, so it cannot go
    stale, and a reader sees why it is there.
    """
    start = txt.rfind('\n', 0, pos) + 1
    end = txt.find('\n', pos)
    return 'check-docs:ignore' in txt[start : end if end != -1 else len(txt)]

FILES = sorted(glob.glob('docs/*.md')) + [f for f in ('CLAUDE.md', 'README.md') if os.path.exists(f)]

# --- 1. repo-relative path claims -------------------------------------------------------------------------
# Conservative on purpose: a known top-level dir plus a known source extension, so ordinary prose is never
# mistaken for a path. A false POSITIVE here costs more than a miss — it trains the reader to ignore the check.
PATH_RE = re.compile(
    r'(?<![\w./-])((?:src|dotnet|test|scripts|docs|\.github)/[A-Za-z0-9_./\-]+'
    r'\.(?:cpp|hpp|h|cs|md|test|sh|yml|ps1|py|cmake))')
skipped_sub = 0
for f in FILES:
    txt = open(f, encoding='utf-8', errors='replace').read()
    for m in PATH_RE.finditer(txt):
        p = m.group(1)
        if p in EXTERNAL or resolves(p) or suppressed(txt, m.start()):
            continue
        if could_be_submodule(p):
            skipped_sub += 1
            continue
        FAIL.append(f'{f}: cites a path that does not exist -> {p}')

# --- 2. markdown link targets ------------------------------------------------------------------------------
# Only targets that LOOK like paths. Docs are full of SQL in prose ("[p.col0,…]", "[@a0]") which is
# syntactically a markdown link and semantically not one; reporting those would be pure noise.
LINK_RE = re.compile(r'\]\((?!https?:|mailto:)([^)]+)\)')
for f in FILES:
    txt = open(f, encoding='utf-8', errors='replace').read()
    base = os.path.dirname(f)
    for m in LINK_RE.finditer(txt):
        t = m.group(1).split('#')[0].strip()
        if not t or not ('/' in t or t.endswith('.md')):
            continue
        if any(os.path.exists(c) for c in (os.path.join(base, t), t)):
            continue
        if t.rstrip('/') in EXTERNAL or could_be_submodule(t) or suppressed(txt, m.start()):
            continue
        FAIL.append(f'{f}: broken link -> {t}')

# --- 2b. #anchors into our own markdown --------------------------------------------------------------------
# Renaming a HEADING silently breaks every link into it, and until 2026-08-18 nothing here noticed: the
# section above deliberately strips the fragment, so a link to a heading that no longer exists resolved as
# long as the FILE did. Two dead ones had accumulated - one created hours earlier by renaming a section in
# docs/plugin-system.md, and one long-standing in README.md pointing at "#delta-lake-catalog-provider-delta"
# for a heading that reads "Delta Lake provider".
#
# Only links whose destination is a file we already slug are checked, so a fragment into a submodule doc or
# an external page is left alone rather than guessed at.

def slug(heading):
    # GitHub's heading anchors: strip inline markup, lowercase, drop punctuation, spaces -> hyphens.
    h = re.sub(r'`([^`]*)`', r'\1', heading.strip())        # inline code keeps its text
    h = re.sub(r'\[([^\]]*)\]\([^)]*\)', r'\1', h)          # links keep their text
    h = re.sub(r'\*', '', h)                                # bold/italic markers
    # NOT '_': GitHub KEEPS underscores. Stripping them here is a real trap - it slugs
    # `fabricator_install_plugin` to `fabricatorinstallplugin` and reports a CORRECT anchor as dead.
    h = h.lower()
    return ''.join(c if (c.isalnum() or c in '-_') else ('-' if c == ' ' else '') for c in h)

ANCHORS = {}
for f in FILES:
    txt = open(f, encoding='utf-8', errors='replace').read()
    seen, got = {}, set()
    fenced = False
    for line in txt.splitlines():
        if line.lstrip().startswith('```'):
            fenced = not fenced          # a '#' inside a fence is a comment, not a heading
            continue
        if fenced:
            continue
        m = re.match(r'^(#{1,6})\s+(.*)$', line)
        if m:
            a = slug(m.group(2))
            n = seen.get(a, 0)
            seen[a] = n + 1
            got.add(a if n == 0 else f'{a}-{n}')            # GitHub disambiguates repeats with -1, -2, ...
    ANCHORS[os.path.normpath(f)] = got

ANCHOR_RE = re.compile(r'\]\((?!https?:|mailto:)([^)#]*)#([A-Za-z0-9\-_]+)\)')
for f in FILES:
    txt = open(f, encoding='utf-8', errors='replace').read()
    base = os.path.dirname(f)
    for m in ANCHOR_RE.finditer(txt):
        target, frag = m.group(1).strip(), m.group(2)
        dest = os.path.normpath(os.path.join(base, target)) if target else os.path.normpath(f)
        if dest not in ANCHORS or suppressed(txt, m.start()):
            continue
        # Inside an inline-code span it is a QUOTED link, not one: these docs discuss links (this very check
        # exists because two broke, and the write-up quotes both). An odd number of backticks between the
        # start of the line and the match means we are inside one.
        if txt.count(chr(96), txt.rfind(chr(10), 0, m.start()) + 1, m.start()) % 2 == 1:
            continue
        if frag not in ANCHORS[dest]:
            FAIL.append(f'{f}: link to a heading that does not exist -> {target or "(this file)"}#{frag}')

# --- 3. verify_*.test suites cited in prose ----------------------------------------------------------------
# Docs cite suites constantly ("gate verify_x 42"), and a renamed or deleted suite makes a doc actively
# misleading about what is covered. Three exclusions, each a false positive that would erode trust in the check:
#   * a trailing `*` — "the 7 verify_delta_rs_* suites" is a GLOB over real suites, not a name;
#   * `PRAGMA verify_serializer` and friends — DuckDB pragmas that merely share the prefix;
#   * a name ending in `_`, which is the glob case reached another way.
SUITE_RE = re.compile(r'(?<![\w/])(verify_[a-z0-9_]+)(?:\.test)?')
PRAGMA_RE = re.compile(r'(?:PRAGMA|pragma)\s+(verify_[a-z0-9_]+)')
have = {os.path.splitext(os.path.basename(p))[0] for p in glob.glob('test/verify_*.test')}
for f in FILES:
    txt = open(f, encoding='utf-8', errors='replace').read()
    pragmas = set(PRAGMA_RE.findall(txt))
    for m in SUITE_RE.finditer(txt):
        name, end = m.group(1), m.end(1)
        if name in have or name in pragmas or name.endswith('_'):
            continue
        if end < len(txt) and txt[end] == '*':
            continue
        if suppressed(txt, m.start()):
            continue
        FAIL.append(f'{f}: cites a suite that does not exist -> {name}.test')

# --- 4. every doc is reachable from CLAUDE.md --------------------------------------------------------------
# An unreferenced doc is not wrong, but it is undiscoverable — and undiscoverable is how a doc rots unnoticed.
# Empirically: when this check was written, ALL FIVE docs whose last substantive edit was the 2026-07-15
# rename were unreferenced, and 11 docs were missing from the index entirely.
#
# ⚠ CLAUDE.md IS UNTRACKED SINCE 2026-08-11 (local-only agent memory), so THIS CHECK DOES NOT RUN IN CI —
# only on a machine that has the file. It is announced rather than skipped in silence: a check that quietly
# stops running is worse than one that was never written, because the green tick still implies it ran.
skipped_index = not os.path.exists('CLAUDE.md')
if not skipped_index:
    claude = open('CLAUDE.md', encoding='utf-8', errors='replace').read()
    for d in sorted(glob.glob('docs/*.md')):
        if os.path.basename(d) not in claude:
            FAIL.append(f'CLAUDE.md: no reference to {d} (add it to the doc index, with a status)')

for line in FAIL:
    print(f'  FAIL  {line}')
if skipped_sub:
    print(f'  note  {skipped_sub} path claim(s) skipped: submodule(s) not checked out ({", ".join(MISSING)})')
if skipped_index:
    print('  note  doc-index check skipped: CLAUDE.md not present (it is untracked — local-only agent memory)')
print()
print(f'checked {len(FILES)} markdown files')
if FAIL:
    print(f'FAILED: {len(FAIL)} unresolved reference(s)')
    sys.exit(1)
print('all references resolve')
PY
