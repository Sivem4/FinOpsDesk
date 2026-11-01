# Starts the Vite dev server with TLS verification against the public ASP.NET development certificate.
# The certificate file is written outside the repository and contains no private key.
# -Cookie selects the local OIDC cookie session. The default remains DevelopmentJwt.

param(
    [switch] $Cookie
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$certificateDirectory = Join-Path $env:USERPROFILE '.finopsdesk'
$certificatePath = Join-Path $certificateDirectory 'localhost-dev.crt'

$check = & dotnet dev-certs https --check --trust 2>&1 | Out-String
if ($LASTEXITCODE -ne 0) {
    Write-Error 'The ASP.NET Core HTTPS development certificate is not trusted. Run: dotnet dev-certs https --trust'
    exit 1
}

if ($check -notmatch '([0-9A-Fa-f]{40})') {
    Write-Error 'Could not read the development certificate thumbprint.'
    exit 1
}

$thumbprint = $Matches[1].ToUpperInvariant()
$certificate = Get-ChildItem -Path Cert:\CurrentUser\Root |
    Where-Object { $_.Thumbprint -eq $thumbprint } |
    Select-Object -First 1

if ($null -eq $certificate) {
    Write-Error "Trusted certificate $thumbprint was not found in Cert:\CurrentUser\Root."
    exit 1
}

if ($certificate.HasPrivateKey) {
    Write-Error 'Refusing to export a certificate copy that reports a private key.'
    exit 1
}

New-Item -ItemType Directory -Force -Path $certificateDirectory | Out-Null
$der = $certificate.Export([System.Security.Cryptography.X509Certificates.X509ContentType]::Cert)
$pem = "-----BEGIN CERTIFICATE-----`n$([Convert]::ToBase64String($der, 'InsertLineBreaks'))`n-----END CERTIFICATE-----"
Set-Content -Path $certificatePath -Value $pem -Encoding ascii

$env:NODE_EXTRA_CA_CERTS = $certificatePath
Write-Host "NODE_EXTRA_CA_CERTS=$certificatePath"
Push-Location (Join-Path $repoRoot 'src\frontend')
try {
    if ($Cookie) { npm run dev:cookie } else { npm run dev }
}
finally {
    Pop-Location
}
