# Generates the self-signed TLS certificate RustFS serves and SQL Server trusts (test-only).
#
# SANs must cover every name a client uses to reach RustFS:
#   - DNS:rustfs     -> SQL Server inside the compose network (LOCATION = 's3://rustfs:9000/')
#   - DNS:localhost + IP:127.0.0.1 -> DuckDB tests on the host (ENDPOINT 'localhost:9000')
#
# Output (public.crt + private.key, PEM) lands next to this script and is GITIGNORED. The compose file mounts
# public.crt into SQL Server's CA store (/var/opt/mssql/security/ca-certificates, read once at process start), and
# RustFS reads the same pair from rustfs/ under its own file names (RUSTFS_TLS_PATH). To rotate: re-run, then
# `docker compose up -d --force-recreate rustfs` and restart SQL Server.
#
# The certificate is its own CA (CA:TRUE). curl and SQL Server accept that; rustls (RustFS's rc client) refuses it
# as CaUsedAsEndEntity, which is why rustfs-init runs rc with --insecure.

$ErrorActionPreference = 'Stop'
$dir = $PSScriptRoot

$cnf = Join-Path $dir 'openssl.cnf'
@'
[req]
distinguished_name = dn
x509_extensions = v3_req
prompt = no

[dn]
CN = rustfs

[v3_req]
basicConstraints = critical, CA:TRUE
keyUsage = critical, digitalSignature, keyEncipherment, keyCertSign
extendedKeyUsage = serverAuth
subjectAltName = @alt_names

[alt_names]
DNS.1 = rustfs
DNS.2 = localhost
IP.1 = 127.0.0.1
'@ | Set-Content -Path $cnf -Encoding ascii

# openssl ships with Git for Windows; fall back to a dockerized openssl if absent.
$openssl = Get-Command openssl -ErrorAction SilentlyContinue
if ($openssl) {
    & openssl req -x509 -nodes -days 3650 -newkey rsa:2048 `
        -keyout (Join-Path $dir 'private.key') -out (Join-Path $dir 'public.crt') -config $cnf
    if ($LASTEXITCODE -ne 0) { throw "openssl failed ($LASTEXITCODE)" }
} else {
    docker run --rm -v "${dir}:/certs" alpine/openssl req -x509 -nodes -days 3650 -newkey rsa:2048 `
        -keyout /certs/private.key -out /certs/public.crt -config /certs/openssl.cnf
    if ($LASTEXITCODE -ne 0) { throw "dockerized openssl failed ($LASTEXITCODE)" }
}

# RustFS reads its certificate from a directory under fixed names (RUSTFS_TLS_PATH): the same pair, renamed.
$rustfsDir = Join-Path $dir 'rustfs'
New-Item -ItemType Directory -Force -Path $rustfsDir | Out-Null
Copy-Item -Force (Join-Path $dir 'public.crt') (Join-Path $rustfsDir 'rustfs_cert.pem')
Copy-Item -Force (Join-Path $dir 'private.key') (Join-Path $rustfsDir 'rustfs_key.pem')
# ⚠ RustFS runs as uid 10001, and openssl writes the key 0600 for the user running this script, so on Linux (CI) the
# container cannot read it and exits at once ("TLS source I/O error ... Permission denied"); compose then reports it
# unhealthy. A Windows bind mount shows every file as 0777, which is why it works there. Test-only key: world-readable.
if ($IsLinux -or $IsMacOS) {   # both undefined (false) in Windows PowerShell 5.1
    & chmod 0644 (Join-Path $rustfsDir 'rustfs_cert.pem') (Join-Path $rustfsDir 'rustfs_key.pem')
    if ($LASTEXITCODE -ne 0) { throw "chmod failed ($LASTEXITCODE)" }
}

Write-Host "Generated $(Join-Path $dir 'public.crt') (CN=rustfs; SANs: rustfs, localhost, 127.0.0.1)"
Write-Host "Copied    $(Join-Path $rustfsDir 'rustfs_cert.pem') + rustfs_key.pem (for RustFS)"
