[CmdletBinding()]
param(
    [string]$WalletFile = (Join-Path $env:LOCALAPPDATA 'PrivateCoin\.privatecoin\wallets.dat'),
    [string]$WalletName,
    [Parameter(Mandatory = $true)][string]$OutputFile
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Security
if (-not (Test-Path -LiteralPath $WalletFile -PathType Leaf)) { throw 'Informe o wallets.dat da sua instalação.' }
if (Test-Path -LiteralPath $OutputFile) { throw 'Escolha um arquivo de saída que ainda não exista.' }

function New-RandomBytes([int]$Length) {
    $bytes = [byte[]]::new($Length)
    $rng = [System.Security.Cryptography.RandomNumberGenerator]::Create()
    try { $rng.GetBytes($bytes) } finally { $rng.Dispose() }
    return ,$bytes
}

$clear = $null
$payload = $null
$keyBytes = $null
$passwordBytes = $null
$passwordPointer = [IntPtr]::Zero
try {
    # Run locally as the Windows user that owns the Desktop wallet. No network calls.
    $clear = [System.Security.Cryptography.ProtectedData]::Unprotect(
        [IO.File]::ReadAllBytes((Resolve-Path -LiteralPath $WalletFile)),
        [Text.Encoding]::UTF8.GetBytes('PrivateCoin'),
        [System.Security.Cryptography.DataProtectionScope]::CurrentUser)
    $store = [Text.Encoding]::UTF8.GetString($clear) | ConvertFrom-Json
    $wallets = @($store.Wallets)
    if ($WalletName) { $wallets = @($wallets | Where-Object { $_.Name -eq $WalletName }) }
    if ($wallets.Count -ne 1) { throw 'Use -WalletName para selecionar exatamente uma carteira.' }
    $selected = $wallets[0]
    if (@($selected.PrivateKeys).Count -eq 0 -or @($selected.PrivateKeys).Count -gt 998) { throw 'A carteira deve conter entre 1 e 998 chaves.' }
    $securePassword = Read-Host 'Senha do backup (mínimo de 10 caracteres)' -AsSecureString
    $passwordPointer = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($securePassword)
    $password = [Runtime.InteropServices.Marshal]::PtrToStringBSTR($passwordPointer)
    if ($password.Length -lt 10) { throw 'Use pelo menos 10 caracteres.' }
    $passwordBytes = [Text.Encoding]::UTF8.GetBytes($password)
    $password = $null
    $salt = New-RandomBytes 16
    $iv = New-RandomBytes 16
    $derivation = [System.Security.Cryptography.Rfc2898DeriveBytes]::new($passwordBytes, $salt, 210000, [System.Security.Cryptography.HashAlgorithmName]::SHA256)
    try { $keyBytes = $derivation.GetBytes(64) } finally { $derivation.Dispose() }
    $aesKey = [byte[]]$keyBytes[0..31]
    $macKey = [byte[]]$keyBytes[32..63]
    $payload = [Text.Encoding]::UTF8.GetBytes((@{ Name = $selected.Name; PrivateKeys = @($selected.PrivateKeys) } | ConvertTo-Json -Compress -Depth 5))
    $aes = [System.Security.Cryptography.Aes]::Create()
    try {
        $aes.Key = $aesKey
        $aes.IV = $iv
        $encryptor = $aes.CreateEncryptor()
        try { $cipher = $encryptor.TransformFinalBlock($payload, 0, $payload.Length) } finally { $encryptor.Dispose() }
    } finally { $aes.Dispose() }
    $authenticated = [byte[]]::new($salt.Length + $iv.Length + $cipher.Length)
    [Array]::Copy($salt, 0, $authenticated, 0, $salt.Length)
    [Array]::Copy($iv, 0, $authenticated, $salt.Length, $iv.Length)
    [Array]::Copy($cipher, 0, $authenticated, $salt.Length + $iv.Length, $cipher.Length)
    $hmac = [System.Security.Cryptography.HMACSHA256]::new($macKey)
    try { $tag = $hmac.ComputeHash($authenticated) } finally { $hmac.Dispose() }
    $record = @{ format = 'povix-dex-wallet-v1'; iterations = 210000; salt = [Convert]::ToBase64String($salt);
        iv = [Convert]::ToBase64String($iv); data = [Convert]::ToBase64String($cipher); hmac = [Convert]::ToBase64String($tag) }
    $outputPath = [IO.Path]::GetFullPath($OutputFile)
    [IO.File]::WriteAllText($outputPath, ($record | ConvertTo-Json -Compress), [Text.UTF8Encoding]::new($false))
    Write-Output "Backup cifrado salvo em $outputPath"
} finally {
    if ($passwordPointer -ne [IntPtr]::Zero) { [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($passwordPointer) }
    foreach ($bytes in @($clear, $payload, $keyBytes, $passwordBytes, $aesKey, $macKey)) {
        if ($null -ne $bytes) { [Array]::Clear($bytes, 0, $bytes.Length) }
    }
    $password = $null
    $store = $null
    $selected = $null
}
