@echo off
call "C:\Program Files\Microsoft Visual Studio\18\Enterprise\VC\Auxiliary\Build\vcvars64.bat"
cmake --build D:\repos\fabricator-extension\build\release-v2 --target unittest shell fabricator_loadable_extension -- -k 0
