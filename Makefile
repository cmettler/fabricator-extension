PROJ_DIR := $(dir $(abspath $(lastword $(MAKEFILE_LIST))))

# Configuration of extension
EXT_NAME=mssql_net
EXT_CONFIG=${PROJ_DIR}extension_config.cmake

# Build the managed (C#) bridge into the extension output before/after the
# native build so the self-contained .NET runtime sits next to the extension.
.PHONY: managed
managed:
	pwsh -NoProfile -File scripts/publish-managed.ps1

# Include the Makefile from extension-ci-tools
include extension-ci-tools/makefiles/duckdb_extension.Makefile
