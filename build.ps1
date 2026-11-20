# Battery Guard - Copyright (C) 2026 Dion Fabian. All rights reserved. See LICENSE.
# Builds Battery Guard with the C# compiler that comes with Windows (.NET Framework 4.x).
# Output: dist\BatteryGuard-Setup-<version>.exe - one file that installs, updates and removes it.
# Nothing is installed by this script; run the setup file for that.
$ErrorActionPreference = 'Stop'
$src = $PSScriptRoot
$obj = Join-Path $src 'obj'
$dist = Join-Path $src 'dist'
New-Item -ItemType Directory -Force $obj, $dist | Out-Null

$csc = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path $csc)) { $csc = Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319\csc.exe' }
$version = ([regex]'Version = "([\d.]+)"').Match((Get-Content (Join-Path $src 'AssemblyInfo.cs') -Raw)).Groups[1].Value

function Compile([string[]]$arguments) {
    & $csc /nologo /optimize+ /debug- /warnaserror- @arguments
    if ($LASTEXITCODE -ne 0) { throw "Compile failed: $($arguments -join ' ')" }
}

Push-Location $src
try {
    # Icon, drawn at every size.
    Compile @('/target:exe', "/out:$obj\MakeIcon.exe", '/reference:System.Drawing.dll', '/reference:System.Windows.Forms.dll', 'tools\MakeIcon.cs', 'UI.cs', 'Common.cs', 'AssemblyInfo.cs')
    & "$obj\MakeIcon.exe" $obj
    if ($LASTEXITCODE -ne 0) { throw 'Drawing the icon failed.' }

    $shared = @('AssemblyInfo.cs', 'Common.cs')
    $forms = @('/reference:System.Windows.Forms.dll', '/reference:System.Drawing.dll', '/reference:System.ServiceProcess.dll')

    Compile (@('/target:winexe', "/out:$obj\BatteryGuardCore.exe", "/win32icon:$obj\app-small.ico", '/reference:System.Management.dll', '/reference:System.ServiceProcess.dll') +
        $shared + @('Gauge.cs', 'Core.cs'))
    $voice = Join-Path $src 'sounds\charging-stopped.wav'   # the spoken alert (Kokoro voice, Apache-2.0)
    Compile (@('/target:winexe', "/out:$obj\BatteryGuard.exe", "/win32icon:$obj\app.ico", "/resource:$obj\app-small.ico,app.ico", "/resource:$voice,charging-stopped.wav") +
        $forms + $shared + @('UI.cs', 'Agent.cs', 'Flyout.cs'))

    $setup = Join-Path $dist "BatteryGuard-Setup-$version.exe"
    $license = Join-Path $src 'LICENSE'   # installed next to the programs as LICENSE.txt
    $payload = @("/resource:$obj\BatteryGuardCore.exe,payload.core", "/resource:$obj\BatteryGuard.exe,payload.agent", "/resource:$obj\app-small.ico,app.ico", "/resource:$license,license.txt")
    Compile (@('/target:winexe', "/out:$setup", "/win32icon:$obj\app.ico", '/win32manifest:setup.manifest') + $payload + $forms + $shared + @('UI.cs', 'Setup.cs'))
    # Same installer without the admin request, only for looking at its pages while developing.
    Compile (@('/target:winexe', "/out:$obj\Setup-preview.exe", "/win32icon:$obj\app.ico") + $payload + $forms + $shared + @('UI.cs', 'Setup.cs'))

    # Nothing in the shipped files may point back to where they were built.
    $banned = @('\Users\')
    foreach ($file in @($setup, "$obj\BatteryGuardCore.exe", "$obj\BatteryGuard.exe")) {
        $bytes = [IO.File]::ReadAllBytes($file)
        $ascii = [Text.Encoding]::ASCII.GetString($bytes)
        $wide = [Text.Encoding]::Unicode.GetString($bytes) + [Text.Encoding]::Unicode.GetString($bytes, 1, $bytes.Length - 1)
        foreach ($word in $banned) {
            if ($ascii.IndexOf($word, [StringComparison]::OrdinalIgnoreCase) -ge 0 -or $wide.IndexOf($word, [StringComparison]::OrdinalIgnoreCase) -ge 0) {
                throw "Found '$word' inside $file"
            }
        }
    }
    Get-Item $setup | Select-Object Name, @{n = 'KB'; e = { [math]::Round($_.Length / 1KB) } }, FullName
}
finally { Pop-Location }
