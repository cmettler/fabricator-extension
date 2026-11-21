@echo off
call "C:\Program Files\Microsoft Visual Studio\18\Enterprise\VC\Auxiliary\Build\vcvars64.bat"
rem DuckDB 2.0 takes its version from this variable at CONFIGURE time (otherwise v2.0.1-devNNNNN), and a
rem stock-version engine is what loads extensions built for that version. cmake --build re-runs configure when a
rem CMakeLists changes, so it is set here and not only for the first configure.
set DUCKDB_VERSION=v2.0.1
rem GNU patch for DuckLake's APPLY_PATCHES (extension_config.cmake). Git for Windows ships it in usr\bin, which
rem is not on a cmd PATH. Appended, so MSVC's link.exe still wins over the coreutils one in the same folder.
for /f "delims=" %%G in ('where git 2^>nul') do if not defined FAB_GIT_DIR set "FAB_GIT_DIR=%%~dpG.."
if defined FAB_GIT_DIR set "PATH=%PATH%;%FAB_GIT_DIR%\usr\bin"
rem The build tree: the shared build\release-v2 unless another one is given (a worktree's own tree).
set "BUILD_DIR=%~1"
if "%BUILD_DIR%"=="" set "BUILD_DIR=D:\repos\fabricator-extension\build\release-v2"
cmake --build "%BUILD_DIR%" --target unittest shell fabricator_loadable_extension -- -k 0
