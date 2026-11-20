# Battery Guard - Copyright (C) 2026 Dion Fabian. Free software under the GNU General Public License v3.0; see LICENSE.
# Builds and runs the scenario tests (the real service code against a simulated battery).
$ErrorActionPreference = 'Stop'
$src = Split-Path $PSScriptRoot
$obj = Join-Path $src 'obj'
New-Item -ItemType Directory -Force $obj | Out-Null
$csc = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path $csc)) { $csc = Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319\csc.exe' }
Push-Location $src
try {
    & $csc /nologo /optimize+ /target:exe /out:"$obj\Scenarios.exe" /main:BatteryGuard.Tests.Program `
        /reference:System.Management.dll /reference:System.ServiceProcess.dll `
        tests\Scenarios.cs AssemblyInfo.cs Common.cs Gauge.cs Core.cs
    if ($LASTEXITCODE -ne 0) { throw 'Compiling the tests failed.' }
    & "$obj\Scenarios.exe"
    exit $LASTEXITCODE
}
finally { Pop-Location }
