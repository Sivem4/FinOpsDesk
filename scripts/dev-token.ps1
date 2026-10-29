# Development-only HMAC JWT for the local FinOpsDesk API.
# Stdout is the token. This script does not call the API and does not write a file.

[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string] $Subject,

    [Parameter(Mandatory = $true)]
    [string] $OrganizationId,

    [ValidateRange(1, 480)]
    [int] $LifetimeMinutes = 60
)

$ErrorActionPreference = 'Stop'

function ConvertTo-Base64UrlBytes([byte[]] $bytes) {
    return [Convert]::ToBase64String($bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_')
}

function ConvertTo-Base64UrlText([string] $value) {
    return ConvertTo-Base64UrlBytes ([System.Text.Encoding]::UTF8.GetBytes($value))
}

$allowedIssuer = 'https://finopsdesk.local'
$integrationTestKey = 'finopsdesk-integration-test-signing-key'

$issuer = $env:Authentication__JwtBearer__Issuer
$audience = $env:Authentication__JwtBearer__Audience
$signingKey = $env:Authentication__JwtBearer__SigningKey

if ([string]::IsNullOrWhiteSpace($issuer) -or $issuer -cne $allowedIssuer) {
    Write-Error "Authentication__JwtBearer__Issuer must be $allowedIssuer."
    exit 1
}

if ([string]::IsNullOrWhiteSpace($audience) -or $audience.Length -gt 200 -or $audience -match '[\s"]') {
    Write-Error 'Authentication__JwtBearer__Audience must be a non-empty audience without spaces or quotes.'
    exit 1
}

if ([string]::IsNullOrWhiteSpace($signingKey)) {
    Write-Error 'Authentication__JwtBearer__SigningKey is required and must not be committed.'
    exit 1
}

if ($signingKey -eq $integrationTestKey) {
    Write-Error 'Do not reuse the integration test signing key.'
    exit 1
}

$signingKeyBytes = [System.Text.Encoding]::UTF8.GetByteCount($signingKey)
if ($signingKeyBytes -lt 32) {
    Write-Error 'Authentication__JwtBearer__SigningKey must be at least 32 UTF-8 bytes.'
    exit 1
}

$normalizedSubject = $Subject.Trim()
if ($normalizedSubject.Length -lt 1 -or $normalizedSubject.Length -gt 256) {
    Write-Error 'Subject must contain 1 to 256 characters after trimming.'
    exit 1
}

foreach ($character in $normalizedSubject.ToCharArray()) {
    if ([int]$character -lt 32) {
        Write-Error 'Subject cannot contain control characters.'
        exit 1
    }
}

$organization = [guid]::Empty
if (-not [guid]::TryParse($OrganizationId, [ref]$organization) -or $organization -eq [guid]::Empty) {
    Write-Error 'OrganizationId must be a non-empty GUID.'
    exit 1
}

$now = [DateTimeOffset]::UtcNow.ToUnixTimeSeconds()
$payload = (@{
    iss = $issuer
    aud = $audience
    sub = $normalizedSubject
    org_id = $organization.ToString('D')
    nbf = $now - 30
    exp = $now + ($LifetimeMinutes * 60)
} | ConvertTo-Json -Compress)

$header = '{"alg":"HS256","typ":"JWT"}'
$unsigned = "$(ConvertTo-Base64UrlText $header).$(ConvertTo-Base64UrlText $payload)"
$hmac = [System.Security.Cryptography.HMACSHA256]::new([System.Text.Encoding]::UTF8.GetBytes($signingKey))
try {
    $signature = $hmac.ComputeHash([System.Text.Encoding]::UTF8.GetBytes($unsigned))
}
finally {
    $hmac.Dispose()
}

Write-Output "$unsigned.$(ConvertTo-Base64UrlBytes $signature)"
