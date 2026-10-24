@echo off
rem Rebuilds the LVar WASM module package with the MSFS SDK and refreshes the prebuilt copy in WasmModule\Prebuilt,
rem which is what the app ships with. Build the WasmModule project (Release|MSFS) in Visual Studio first.

if "%MSFS_SDK%"=="" (
    echo The MSFS_SDK environment variable isn't set. Install the MSFS SDK first.
    exit /b 1
)

"%MSFS_SDK%Tools\bin\fspackagetool.exe" MSFSWASMProject\BetterBravoLightsLVars.xml -outputdir MSFSWASMProject\_Build || exit /b 1

rd /s /q WasmModule\Prebuilt\better-bravo-lights-lvar-module
xcopy /e /i /y MSFSWASMProject\_Build\Packages\better-bravo-lights-lvar-module WasmModule\Prebuilt\better-bravo-lights-lvar-module
