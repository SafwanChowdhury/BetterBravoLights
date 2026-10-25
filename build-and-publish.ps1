# Builds Better Bravo Lights and packages it into BetterBravoLights.zip.
#
# The WASM module is taken from WasmModule\Prebuilt. If you've changed WasmModule.cpp, rebuild it first
# with the MSFS SDK (see README.md) and copy the new package into WasmModule\Prebuilt.

$ErrorActionPreference = 'Stop'

if (Test-Path -Path .\BravoLights\bin\Release) {
	Remove-Item -Path .\BravoLights\bin\Release -Recurse
}

dotnet test BravoLights.Tests\BravoLights.Tests.csproj -c Release
if ($LASTEXITCODE -ne 0) { throw "Tests failed" }

dotnet publish BravoLights\BravoLights.csproj -p:PublishProfile=FolderProfile -c Release
if ($LASTEXITCODE -ne 0) { throw "Publish failed" }

$publishDir = "BravoLights\bin\Release\net8.0-windows\win-x64\publish"
$path = Resolve-Path "$publishDir\BetterBravoLights.exe"

if (Test-Path -Path BetterBravoLights) {
	Remove-Item -Path BetterBravoLights -Recurse
}

New-Item -ItemType Directory BetterBravoLights | Out-Null
New-Item -ItemType Directory BetterBravoLights\Program | Out-Null
Copy-Item -Path "$publishDir\*" -Destination "BetterBravoLights\Program" -Recurse
Copy-Item -Path "BravoLights\install.bat" -Destination "BetterBravoLights"
Copy-Item -Path "BravoLights\uninstall.bat" -Destination "BetterBravoLights"
Copy-Item -Path "BravoLights\LICENSES.md" -Destination "BetterBravoLights"

$o = [system.diagnostics.fileversioninfo]::GetVersionInfo($path)
Write-Output "Better Bravo Lights $($o.ProductVersion)" | Out-File -Encoding UTF8 BetterBravoLights\VERSION.txt

Compress-Archive -Path "BetterBravoLights\*" -Force -DestinationPath BetterBravoLights.zip
Write-Output "Created BetterBravoLights.zip"
