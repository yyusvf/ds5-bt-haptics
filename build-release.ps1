param([string]$Iscc)
$ErrorActionPreference = 'Stop'
Push-Location $PSScriptRoot
try {
    [xml]$project = Get-Content DS5BtHaptics.App/DS5BtHaptics.App.csproj
    $version = $project.Project.PropertyGroup.Version | Where-Object { $_ }
    if (!$Iscc) {
        $candidates = @(
            "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe",
            "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
            "$env:ProgramFiles\Inno Setup 6\ISCC.exe"
        )
        $Iscc = $candidates | Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
        if (!$Iscc) { $Iscc = (Get-Command ISCC -ErrorAction SilentlyContinue).Source }
    }
    if (!$Iscc) { throw 'Inno Setup 6 is required to build the setup. Install it from https://jrsoftware.org/isinfo.php.' }

    dotnet publish DS5BtHaptics -c Release -o dist/cli -p:DebugType=None -p:DebugSymbols=false
    if ($LASTEXITCODE -ne 0) { throw 'CLI publish failed.' }
    & dist/cli/ds5-bt-haptics.exe selftest
    if ($LASTEXITCODE -ne 0) { throw 'Self test failed.' }
    dotnet publish DS5BtHaptics.App -c Release -o dist/app -p:DebugType=None -p:DebugSymbols=false
    if ($LASTEXITCODE -ne 0) { throw 'App publish failed.' }

    # Setup uses the small framework-dependent EXE; a missing Desktop Runtime is downloaded during installation.
    & $Iscc "/DAppVersion=$version" setup/DS5BtHaptics.iss
    if ($LASTEXITCODE -ne 0) { throw 'Setup compilation failed.' }
    Write-Host "Ready: dist/DS5BtHaptics-Setup.exe"
}
finally { Pop-Location }
