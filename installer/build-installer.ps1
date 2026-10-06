[CmdletBinding()]
param(
    [switch]$Offline,
    [string]$MSBuildPath,
    [string]$MakensisPath
)
$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot

if (!$MSBuildPath) {
    $command = Get-Command MSBuild.exe -ErrorAction SilentlyContinue
    if ($command) { $MSBuildPath = $command.Source }
    else {
        $vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
        if (Test-Path -LiteralPath $vswhere) {
            $MSBuildPath = & $vswhere -latest -products '*' -requires Microsoft.Component.MSBuild -find 'MSBuild\**\Bin\MSBuild.exe' | Select-Object -First 1
        }
    }
}
if (!$MSBuildPath) { throw 'Instale Visual Studio Build Tools com MSBuild e o Developer Pack do .NET Framework 4.8.' }
if (!$MakensisPath) {
    $command = Get-Command makensis.exe -ErrorAction SilentlyContinue
    if ($command) { $MakensisPath = $command.Source }
    else { $MakensisPath = Join-Path ${env:ProgramFiles(x86)} 'NSIS\makensis.exe' }
}
if (!(Test-Path -LiteralPath $MakensisPath)) { throw 'Instale NSIS 3 ou informe -MakensisPath.' }

Push-Location $repoRoot
try {
    & $MSBuildPath 'PrivateCoin.Desktop\PrivateCoin.Desktop.csproj' /p:Configuration=Release /m:2 /verbosity:minimal
    if ($LASTEXITCODE -ne 0) { throw "A compilação falhou: $LASTEXITCODE." }
    $payload = Join-Path $repoRoot 'PrivateCoin.Desktop\bin\Release'
    $version = [Reflection.AssemblyName]::GetAssemblyName((Join-Path $payload 'PrivateCoin.Desktop.exe')).Version.ToString()
    $outputDir = Join-Path $repoRoot 'artifacts\installer'
    New-Item -ItemType Directory -Force -Path $outputDir | Out-Null
    $mode = if ($Offline) { 'Offline' } else { 'Online' }
    $outputFile = Join-Path $outputDir "PrivateCoin.Desktop-$version-Setup-$mode.exe"
    $arguments = @("/DAPP_VERSION=$version", "/DPAYLOAD_DIR=$payload", "/DOUTPUT_FILE=$outputFile")

    if ($Offline) {
        $runtimeDir = Join-Path $repoRoot 'artifacts\prerequisites'
        New-Item -ItemType Directory -Force -Path $runtimeDir | Out-Null
        $runtime = Join-Path $runtimeDir 'ndp48-x86-x64-allos-enu.exe'
        $expectedHash = '95889d6de3f2070c07790ad6cf2000d33d9a1bdfc6a381725ab82ab1c314fd53'
        if (!(Test-Path -LiteralPath $runtime)) {
            $temporaryFile = Join-Path $runtimeDir ('dotnet48-download-' + [Guid]::NewGuid().ToString('N'))
            try {
                [Net.ServicePointManager]::SecurityProtocol = [Net.ServicePointManager]::SecurityProtocol -bor [Net.SecurityProtocolType]::Tls12
                Invoke-WebRequest -UseBasicParsing -Uri 'https://download.visualstudio.microsoft.com/download/pr/7afca223-55d2-470a-8edc-6a1739ae3252/abd170b4b0ec15ad0222a809b761a036/ndp48-x86-x64-allos-enu.exe' -OutFile $temporaryFile
                if ((Get-FileHash -LiteralPath $temporaryFile -Algorithm SHA256).Hash -ne $expectedHash) { throw 'SHA-256 do .NET inválido.' }
                Move-Item -LiteralPath $temporaryFile -Destination $runtime
            } finally {
                if (Test-Path -LiteralPath $temporaryFile) { Remove-Item -LiteralPath $temporaryFile -Force }
            }
        }
        if ((Get-FileHash -LiteralPath $runtime -Algorithm SHA256).Hash -ne $expectedHash) { throw 'SHA-256 do .NET inválido.' }
        $signature = Get-AuthenticodeSignature -LiteralPath $runtime
        if ($signature.Status -ne 'Valid' -or $signature.SignerCertificate.Subject -notmatch '(?:^|,\s*)O=Microsoft Corporation(?:,|$)') { throw 'A assinatura digital da Microsoft não pôde ser validada.' }
        $arguments += "/DDOTNET48_INSTALLER=$runtime"
    }
    & $MakensisPath @arguments (Join-Path $PSScriptRoot 'PrivateCoin.Desktop.nsi')
    if ($LASTEXITCODE -ne 0) { throw "A geração do instalador falhou: $LASTEXITCODE." }
    $hash = (Get-FileHash -LiteralPath $outputFile -Algorithm SHA256).Hash.ToLowerInvariant()
    "$hash  $([IO.Path]::GetFileName($outputFile))" | Set-Content -LiteralPath "$outputFile.sha256" -Encoding ascii
    Write-Host "Instalador gerado: $outputFile"
} finally { Pop-Location }
