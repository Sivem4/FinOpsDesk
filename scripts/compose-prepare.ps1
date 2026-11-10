# Creates or reuses the local Compose secret set outside the repository.
# A valid existing set is left byte-for-byte unchanged.
# A partial or invalid set is refused without writes or deletes.

[CmdletBinding()]
param(
    [string] $RepositoryRoot
)

function Set-OperatorProtection {
    param(
        [Parameter(Mandatory = $true)]
        [string] $Path,

        [switch] $File
    )

    $identity = [System.Security.Principal.WindowsIdentity]::GetCurrent().User
    if ($null -eq $identity) {
        throw 'The current Windows identity has no user SID.'
    }

    $existing = Get-Acl -LiteralPath $Path
    $existingRules = @($existing.Access)
    if ($existing.AreAccessRulesProtected -and $existingRules.Count -eq 1) {
        $existingSid = $existingRules[0].IdentityReference.Translate(
            [System.Security.Principal.SecurityIdentifier])
        $fullControl = [System.Security.AccessControl.FileSystemRights]::FullControl
        if ($existingSid.Value -eq $identity.Value -and
            $existingRules[0].AccessControlType -eq 'Allow' -and
            ($existingRules[0].FileSystemRights -band $fullControl) -eq $fullControl) {
            return
        }
    }

    $security = if ($File) {
        New-Object System.Security.AccessControl.FileSecurity
    }
    else {
        New-Object System.Security.AccessControl.DirectorySecurity
    }

    $security.SetAccessRuleProtection($true, $false)
    $rights = [System.Security.AccessControl.FileSystemRights]::FullControl
    $inheritance = if ($File) {
        [System.Security.AccessControl.InheritanceFlags]::None
    }
    else {
        [System.Security.AccessControl.InheritanceFlags]'ContainerInherit, ObjectInherit'
    }
    $propagation = [System.Security.AccessControl.PropagationFlags]::None
    $rule = New-Object System.Security.AccessControl.FileSystemAccessRule(
        $identity,
        $rights,
        $inheritance,
        $propagation,
        'Allow')
    $security.AddAccessRule($rule)
    Set-Acl -LiteralPath $Path -AclObject $security
}

$ErrorActionPreference = 'Stop'

if ([string]::IsNullOrWhiteSpace($RepositoryRoot)) {
    $RepositoryRoot = [System.IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot))
}

if ([string]::IsNullOrWhiteSpace($env:USERPROFILE)) {
    Write-Error 'USERPROFILE is required.'
    exit 1
}

$destination = [System.IO.Path]::GetFullPath((Join-Path $env:USERPROFILE '.finopsdesk\compose'))
$project = Join-Path $PSScriptRoot 'compose-prepare\ComposePrepare.csproj'
& dotnet run --project $project -- check $destination $RepositoryRoot
if ($LASTEXITCODE -ne 0) {
    exit $LASTEXITCODE
}

$secretNames = @(
    'sql-password.txt',
    'jwt-signing-key.txt',
    'api.crt',
    'api.key'
)
$existingSecrets = @($secretNames | Where-Object {
    Test-Path -LiteralPath (Join-Path $destination $_)
})
if ($existingSecrets.Count -eq 0) {
    if (-not (Test-Path -LiteralPath $destination)) {
        New-Item -ItemType Directory -Path $destination | Out-Null
    }

    Set-OperatorProtection -Path $destination
}

& dotnet run --project $project -- $destination $RepositoryRoot
$code = $LASTEXITCODE
if ($code -ne 0 -and $code -ne 10) {
    exit $code
}

Set-OperatorProtection -Path $destination
foreach ($secretName in $secretNames) {
    $secretPath = Join-Path $destination $secretName
    if (Test-Path -LiteralPath $secretPath) {
        Set-OperatorProtection -Path $secretPath -File
    }
}

exit 0
