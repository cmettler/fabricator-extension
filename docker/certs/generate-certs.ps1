# Generates the self-signed TLS certificate MinIO serves and SQL Server trusts (test-only).
#
# SANs must cover every name a client uses to reach MinIO:
#   - DNS:minio      -> SQL Server inside the compose network (LOCATION = 's3://minio:9000/')
#   - DNS:localhost + IP:127.0.0.1 -> DuckDB tests on the host (ENDPOINT 'localhost:9000')
#
# Output (public.crt + private.key, PEM) lands next to this script and is GITIGNORED; the compose file
# mounts the folder into MinIO (/root/.minio/certs -> auto-TLS) and the cert into SQL Server's CA store
# (/var/opt/mssql/security/ca-certificates). Re-run + `docker compose restart` to rotate.

$ErrorActionPreference = 'Stop'
$dir = $PSScriptRoot

$cnf = Join-Path $dir 'openssl.cnf'
@'
[req]
distinguished_name = dn
x509_extensions = v3_req
prompt = no

[dn]
CN = minio

[v3_req]
basicConstraints = critical, CA:TRUE
keyUsage = critical, digitalSignature, keyEncipherment, keyCertSign
extendedKeyUsage = serverAuth
subjectAltName = @alt_names

[alt_names]
DNS.1 = minio
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

Write-Host "Generated $(Join-Path $dir 'public.crt') (CN=minio; SANs: minio, localhost, 127.0.0.1)"
