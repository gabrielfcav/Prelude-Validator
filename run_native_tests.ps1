$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$compiler = 'C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$client = Join-Path $PSScriptRoot 'Support\TheIsleClient-Win64-Shipping.exe'
$outputDirectory = Join-Path $PSScriptRoot 'test-build'
New-Item -ItemType Directory -Path $outputDirectory -Force | Out-Null
$testExe = Join-Path $outputDirectory 'NativeBackendTests.exe'
$compilerArgs = @(
    '/nologo', '/target:exe', '/platform:x64', '/debug-', '/codepage:65001',
    '/define:PRELUDE_FIXTURE_TEST', ('/out:' + $testExe),
    ('/resource:' + $client + ',Prelude.Client'),
    '/reference:System.dll', '/reference:System.Core.dll', '/reference:System.Web.Extensions.dll',
    (Join-Path $PSScriptRoot 'Backend.cs'), (Join-Path $PSScriptRoot 'InstallerArguments.cs'),
    (Join-Path $PSScriptRoot 'NativeBackendTests.cs')
)
& $compiler @compilerArgs
if ($LASTEXITCODE -ne 0) { throw "Falha na compilação dos testes: $LASTEXITCODE" }
& $testExe
if ($LASTEXITCODE -ne 0) { throw "Falha nos testes: $LASTEXITCODE" }
