param([string]$ActivationScript='C:\Espressif\tools\Microsoft.v5.4.4.PowerShell_profile.ps1')
$ErrorActionPreference='Stop'
. $ActivationScript
$firmwarePython=Join-Path $env:IDF_PYTHON_ENV_PATH 'Scripts\python.exe'
$firmwareIdf=Join-Path $env:IDF_PATH 'tools\idf.py'
if (!(Test-Path -LiteralPath $firmwarePython)) { throw 'ESP-IDF Python not found.' }
$firmwareStage=Join-Path $env:TEMP ('fitness-chouqian-'+[guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $firmwareStage | Out-Null
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'main') -Destination $firmwareStage -Recurse
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'sdkconfig'),(Join-Path $PSScriptRoot 'CMakeLists.txt') -Destination $firmwareStage
Push-Location -LiteralPath $firmwareStage
try {
    & $firmwarePython $firmwareIdf build
    if ($LASTEXITCODE -ne 0) { throw "Firmware build failed. Stage: $firmwareStage" }
    $firmwareOut=Join-Path $PSScriptRoot 'firmware-release'
    New-Item -ItemType Directory -Path "$firmwareOut\bootloader","$firmwareOut\partition_table" -Force | Out-Null
    Copy-Item -LiteralPath 'build\test.bin','build\test.elf','build\flash_args','build\flasher_args.json' -Destination $firmwareOut
    Copy-Item -LiteralPath 'build\bootloader\bootloader.bin' -Destination "$firmwareOut\bootloader"
    Copy-Item -LiteralPath 'build\partition_table\partition-table.bin' -Destination "$firmwareOut\partition_table"
    Get-FileHash -LiteralPath "$firmwareOut\test.bin","$firmwareOut\bootloader\bootloader.bin","$firmwareOut\partition_table\partition-table.bin" | Select-Object Algorithm,Hash,Path | ConvertTo-Json | Set-Content "$firmwareOut\sha256.json" -Encoding utf8
    Write-Host "Build complete: $firmwareOut"
} finally { Pop-Location }
