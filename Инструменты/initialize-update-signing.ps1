param()
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$keyDirectory = Join-Path $env:LOCALAPPDATA 'LOPATA-Publishing/Keys'
$privatePath = Join-Path $keyDirectory 'file-updates-p256.pem'
$publicPath = Join-Path $root 'Исходники/LOPATA.Updates/release-keys.json'
$keyId = 'lopata-file-updates-1'
$null = New-Item -ItemType Directory -Path $keyDirectory -Force
$acl = [Security.AccessControl.DirectorySecurity]::new()
$acl.SetAccessRuleProtection($true, $false)
$inherit = [Security.AccessControl.InheritanceFlags]'ContainerInherit, ObjectInherit'
$propagation = [Security.AccessControl.PropagationFlags]::None
foreach ($identity in @([Security.Principal.WindowsIdentity]::GetCurrent().User, [Security.Principal.SecurityIdentifier]::new('S-1-5-18'))) {
    $rule = [Security.AccessControl.FileSystemAccessRule]::new($identity, 'FullControl', $inherit, $propagation, 'Allow')
    $acl.AddAccessRule($rule)
}
Set-Acl -LiteralPath $keyDirectory -AclObject $acl
$key = [Security.Cryptography.ECDsa]::Create([Security.Cryptography.ECCurve+NamedCurves]::nistP256)
try {
    if (Test-Path -LiteralPath $privatePath) { $key.ImportFromPem([IO.File]::ReadAllText($privatePath)) }
    else {
        if (Test-Path -LiteralPath $publicPath) { throw 'Pinned public key exists but private key is missing. Restore the original signing key; do not rotate silently.' }
        [IO.File]::WriteAllText($privatePath, $key.ExportPkcs8PrivateKeyPem(), [Text.UTF8Encoding]::new($false))
    }
    $public = $key.ExportSubjectPublicKeyInfoPem()
    if (Test-Path -LiteralPath $publicPath) {
        $existing = Get-Content -LiteralPath $publicPath -Raw | ConvertFrom-Json -AsHashtable
        if ($existing[$keyId] -ne $public) { throw 'Existing pinned verification key differs. Explicit key rotation is required.' }
    }
    else {
        [IO.File]::WriteAllText($publicPath, (@{ $keyId = $public } | ConvertTo-Json), [Text.UTF8Encoding]::new($false))
    }
    Write-Host 'Signing key is stored outside the repository with current-user/SYSTEM permissions. Public verification key is ready.'
}
finally { $key.Dispose() }
