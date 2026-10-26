@echo off
call "C:\Program Files\Microsoft Visual Studio\18\Enterprise\VC\Auxiliary\Build\vcvars64.bat"
rem DuckDB 2.0 takes its version from this variable at CONFIGURE time (otherwise v2.0.0-devNNNNN), and a
rem stock-version engine is what loads extensions built for v2.0.0. cmake --build re-runs configure when a
rem CMakeLists changes, so it is set here and not only for the first configure.
set DUCKDB_VERSION=v2.0.0
cmake --build D:\repos\fabricator-extension\build\release-v2 --target unittest shell fabricator_loadable_extension -- -k 0
