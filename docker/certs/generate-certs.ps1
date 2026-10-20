# Generates the self-signed TLS certificate MinIO serves and SQL Server trusts (test-only).
#
# SANs must cover every name a client uses to reach MinIO:
#   - DNS:minio      -> SQL Server inside the compose network (LOCATION = 's3://minio:9000/')
#   - DNS:localhost + IP:127.0.0.1 -> DuckDB tests on the host (ENDPOINT 'localhost:9000')
#
# Output (public.crt + private.key, PEM) lands next to this script and is GITIGNORED; the compose file
# mounts the folder into MinIO (/certs -> auto-TLS) and the cert into SQL Server's CA store
# (/var/opt/mssql/security/ca-certificates). Re-run + `docker compose restart` to rotate.
#
# It ALSO writes a copy to CAs/public.crt, and that copy is load-bearing rather than tidy: the Bitnami
# MinIO image's setup.sh copies ${MINIO_CERTS_DIR}/CAs/ into the bundled mc client's trust store, and mc
# is what MINIO_DEFAULT_BUCKETS uses to create the bucket -- over HTTPS, against this very self-signed
# cert. Without the copy the container starts healthy and the `fabricator` bucket is silently ABSENT.
# The cert is self-signed, so it IS its own CA and the copy is literally the same bytes.

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

$caDir = Join-Path $dir 'CAs'
New-Item -ItemType Directory -Force -Path $caDir | Out-Null
Copy-Item -Force (Join-Path $dir 'public.crt') (Join-Path $caDir 'public.crt')

Write-Host "Generated $(Join-Path $dir 'public.crt') (CN=minio; SANs: minio, localhost, 127.0.0.1)"
Write-Host "Copied    $(Join-Path $caDir 'public.crt') (trust store for the bundled mc client)"
