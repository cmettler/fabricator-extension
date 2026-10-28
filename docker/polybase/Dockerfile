# SQL Server 2025 + PolyBase (generic ODBC sources, Linux since 2025) for the DuckLake-over-ODBC experiment
# (docs/ducklake-catalog.md §12). Separate from the main rig's image on purpose: the service tier does not need
# PolyBase, and an image that fails to build must not take the rig down with it.
FROM mcr.microsoft.com/mssql/server:2025-latest
USER root
# The base image already trusts Microsoft's key (/usr/share/keyrings/microsoft-prod.gpg); only the
# mssql-server-2025 repository, which carries mssql-server-polybase, is missing.
RUN echo "deb [arch=amd64 signed-by=/usr/share/keyrings/microsoft-prod.gpg] https://packages.microsoft.com/ubuntu/24.04/mssql-server-2025 noble main" \
      > /etc/apt/sources.list.d/mssql-server-2025.list \
 && apt-get update -qq \
 && ACCEPT_EULA=Y DEBIAN_FRONTEND=noninteractive apt-get install -y unixodbc mssql-server-polybase \
 && rm -rf /var/lib/apt/lists/*
# The package's postinst installs the External Execution Service (its own .NET 8 under /opt/mssql-ees-dotnet,
# the mssql_ees user) only when systemd is present, so a container never gets it. Run the installer here.
RUN /opt/mssql/bin/installexecutionservice.sh --non-interactive
# Register the DuckDB driver BESIDE Microsoft's ODBC Driver 18 -- replacing /etc/odbcinst.ini would remove the
# driver sqlcmd and PolyBase's own SQL Server connector need.
RUN printf '\n[DuckDB]\nDescription=DuckDB ODBC driver (rebuilt with -Wl,-Bsymbolic, see docs/ducklake-catalog.md §12)\nDriver=/opt/duckodbc/libduckdb_odbc.so\n' >> /etc/odbcinst.ini
COPY start.sh /opt/fabricator/start.sh
RUN chmod 755 /opt/fabricator/start.sh
ENTRYPOINT ["/opt/fabricator/start.sh"]
CMD []
