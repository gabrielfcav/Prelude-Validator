param([string]$OutputDirectory = (Join-Path $PSScriptRoot 'dist'))
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$compiler = 'C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$client = Join-Path $PSScriptRoot 'Support\TheIsleClient-Win64-Shipping.exe'
$expected = 'ecfe336d75287a7d25df07ca7f5881326e3269c4bcaef397a71786baf4a44423'
if ((Get-Item -LiteralPath $client).Length -ne 74727936 -or
    (Get-FileHash -LiteralPath $client -Algorithm SHA256).Hash.ToLowerInvariant() -ne $expected) {
    throw 'Executável completo diferente da versão validada.'
}
New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null
$output = Join-Path $OutputDirectory 'PRELUDE.exe'
$compilerArgs = @(
    '/nologo', '/target:winexe', '/platform:x64', '/optimize+', '/debug-', '/codepage:65001',
    ('/out:' + $output),
    ('/win32icon:' + (Join-Path $PSScriptRoot 'Prelude.ico')),
    ('/win32manifest:' + (Join-Path $PSScriptRoot 'app.manifest')),
    ('/resource:' + $client + ',Prelude.Client'),
    '/reference:System.dll', '/reference:System.Core.dll',
    '/reference:System.Drawing.dll', '/reference:System.Windows.Forms.dll',
    '/reference:System.Web.Extensions.dll',
    (Join-Path $PSScriptRoot 'Backend.cs'), (Join-Path $PSScriptRoot 'InstallerArguments.cs'),
    (Join-Path $PSScriptRoot 'NativeGui.cs')
)
& $compiler @compilerArgs
if ($LASTEXITCODE -ne 0) { throw "Falha na compilação: $LASTEXITCODE" }
$report = [ordered]@{
    output = (Resolve-Path -LiteralPath $output).Path
    sha256 = (Get-FileHash -LiteralPath $output -Algorithm SHA256).Hash.ToLowerInvariant()
    bytes = (Get-Item -LiteralPath $output).Length
    compiler = $compiler
    resources = @('Prelude.Client')
    embedded_client_sha256 = $expected
    installer_obfuscation = $false
    client_key_concealment_preserved = $true
    python_runtime = $false
    delta_patching = $false
    game_installed_or_launched = $false
}
$report | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $PSScriptRoot 'build_native_report.json') -Encoding UTF8
$report | ConvertTo-Json
