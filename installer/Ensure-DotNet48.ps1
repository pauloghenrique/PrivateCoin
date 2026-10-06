# Runs elevated only when .NET Framework 4.8 is missing.
[CmdletBinding()]
param([string]$OfflineInstaller = (Join-Path $PSScriptRoot 'ndp48-x86-x64-allos-enu.exe'))

$ErrorActionPreference = 'Stop'
$url = 'https://download.visualstudio.microsoft.com/download/pr/7afca223-55d2-470a-8edc-6a1739ae3252/abd170b4b0ec15ad0222a809b761a036/ndp48-x86-x64-allos-enu.exe'
$expectedHash = '95889d6de3f2070c07790ad6cf2000d33d9a1bdfc6a381725ab82ab1c314fd53'
$temporaryDirectory = $null
$exitCode = 1603
try {
    $release = Get-ItemPropertyValue -LiteralPath 'HKLM:\SOFTWARE\Microsoft\NET Framework Setup\NDP\v4\Full' -Name Release -ErrorAction SilentlyContinue
    if ($release -ge 528040) { exit 0 }
    if (!(Test-Path -LiteralPath $OfflineInstaller -PathType Leaf)) {
        $temporaryDirectory = Join-Path ([IO.Path]::GetTempPath()) ('PrivateCoin-DotNet-' + [Guid]::NewGuid().ToString('N'))
        New-Item -ItemType Directory -Path $temporaryDirectory | Out-Null
        $OfflineInstaller = Join-Path $temporaryDirectory 'ndp48-x86-x64-allos-enu.exe'
        [Net.ServicePointManager]::SecurityProtocol = [Net.ServicePointManager]::SecurityProtocol -bor [Net.SecurityProtocolType]::Tls12
        Write-Host 'Baixando o .NET Framework 4.8 da Microsoft...'
        Invoke-WebRequest -UseBasicParsing -Uri $url -OutFile $OfflineInstaller
    }
    if ((Get-FileHash -LiteralPath $OfflineInstaller -Algorithm SHA256).Hash -ne $expectedHash) {
        throw 'O SHA-256 do instalador do .NET não corresponde ao arquivo esperado.'
    }
    $signature = Get-AuthenticodeSignature -LiteralPath $OfflineInstaller
    if ($signature.Status -ne 'Valid' -or $signature.SignerCertificate.Subject -notmatch '(?:^|,\s*)O=Microsoft Corporation(?:,|$)') {
        throw 'A assinatura digital da Microsoft não pôde ser validada.'
    }
    Write-Host 'Instalando o .NET Framework 4.8...'
    $process = Start-Process -FilePath $OfflineInstaller -ArgumentList '/q', '/norestart' -Wait -PassThru
    $exitCode = $process.ExitCode
} catch {
    Write-Error $_ -ErrorAction Continue
} finally {
    if ($temporaryDirectory) { Remove-Item -LiteralPath $temporaryDirectory -Recurse -Force -ErrorAction SilentlyContinue }
}
exit $exitCode
