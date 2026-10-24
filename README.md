# Better Bravo Lights

Better Bravo Lights drives the annunciator, autopilot and landing gear lights on the Honeycomb Bravo Throttle Quadrant
from Microsoft Flight Simulator. It replaces the standard Honeycomb lights utility with one that is easier to configure,
more flexible and more responsive.

It works natively with **MSFS 2024** and **MSFS 2020** (Microsoft Store and Steam).

## Features

- **Dashboard**: live status of the simulator, the Bravo and the LVar module; the loaded aircraft; which profile is
  active and why; an on-screen Bravo showing every light; and, for any light, its expression and the live values of the
  variables behind it. There's also a light test.
- **Aircraft profiles**: each aircraft (or family of aircraft) has its own profile file. Profiles are matched to the
  loaded aircraft by wildcard patterns (`FNX_32X`, `PMDG 737-*`), can inherit lights from the default profile, and are
  edited in the app with live validation. You can also pick a profile manually.
- **Import and export**: share a single profile (`.bblprofile`) or your whole library (`.bblpack`). Old
  `Config.ini` files can be imported and are converted to profiles.
- **Variable scanner**: lists every LVar in the running simulator, plus the known A: simulation variables, with live
  values. To find the variable behind a cockpit light: *Watch all*, *Mute noisy*, *Only changed*, then flip the switch.
  Right-click a variable to add it straight to a light in a profile.
- **Installation**: from the Settings page, install into each simulator: auto-start via `exe.xml` and the WASM module
  in the Community folder. A backup of `exe.xml` is kept.

## Profiles

Profiles live in `%APPDATA%\BetterBravoLights\Profiles`, one JSON file per profile. Changes made outside the app are
picked up automatically. The built-in profiles are copied there on first run; profiles you delete stay deleted (Settings
→ *Restore built-in profiles* brings them back).

```json
{
  "format": "BetterBravoLights.Profile",
  "version": 1,
  "id": "fenix-a32x",
  "name": "Fenix A319/A320/A321",
  "enabled": true,
  "aircraft": [ "FNX_32X", "fnx320" ],
  "inheritDefault": true,
  "masterEnable": "L:S_OH_ELEC_BAT1 == 1 OR L:S_OH_ELEC_BAT2 == 1",
  "lights": {
    "MasterWarning": { "expression": "L:I_MIP_MASTER_WARNING_CAPT == 1" },
    "ParkingBrake": { "expression": "L:S_MIP_PARKING_BRAKE == 1", "note": "Optional notes" },
    "Door": { "expression": "L:SOME_DOOR == 0", "invert": true }
  }
}
```

- `aircraft` patterns are matched case-insensitively against the aircraft's folder (`FNX_32X`), its full path
  (`FNX_32X\presets\fnx\FNX_320_CFM\config`, as reported by MSFS 2024) and its title. `*` and `?` are wildcards. If
  several profiles match, the most specific pattern wins; if none match, the default profile is used.
- Light names are `HDG NAV APR REV ALT VS IAS AUTOPILOT`, `GearLGreen GearLRed GearCGreen GearCRed GearRGreen GearRRed`,
  `MasterWarning EngineFire LowOilPressure LowFuelPressure AntiIce StarterEngaged APU` and
  `MasterCaution Vacuum LowHydPressure AuxFuelPump ParkingBrake LowVolts Door`.
- Expressions use `L:NAME` and `A:NAME, units` variables with `== != < <= > >= + - * / & |`, `AND OR NOT` (or
  `&& ||`), brackets, and `ON`/`OFF`.

## Developer documentation

Requirements: the .NET 8 SDK. Visual Studio 2022 is optional. The MSFS SDK is only needed to rebuild the WASM module.

```
dotnet build BravoLights\BravoLights.csproj
dotnet test BravoLights.Tests\BravoLights.Tests.csproj
```

Project layout:

- `BravoLights` – the WPF app. `AppCore` ties everything together; `Profiles\` has the profile model, matcher, store and
  legacy INI importer; `Connections\` talks to SimConnect and the WASM module; `Installation\` finds MSFS 2020/2024
  installs and manages `exe.xml` and the Community folder; `UI\` holds the MVVM views and view models.
- `BravoLights\Templates` – the built-in aircraft profiles.
- `BravoLights.Common` – the expression parser and Bravo USB code (shared with `DCSBravoLights`).
- `lib\SimConnect` – the vendored SimConnect managed wrapper and native DLL (v11; works with MSFS 2020 and 2024).
- `WasmModule` – the C++ source of the LVar WASM module; `WasmModule\Prebuilt` is the compiled package the app ships.

Developer command-line switches:

- `BetterBravoLights.exe /convert-ini Config.ini OutputDir` converts an old INI configuration into `.bblprofile` files.
- `BetterBravoLights.exe /render-pages OutputDir` renders each page of the UI to a PNG.

### Rebuilding the WASM module

1. Install the MSFS SDK (2020 or 2024) and Visual Studio's MSFS platform toolset.
2. Build the `WasmModule` project (`Release|MSFS`).
3. Bump the version in `MSFSWASMProject\PackageDefinitions\better-bravo-lights-lvar-module.xml` if the protocol changed.
4. Run `assemble-wasm-for-ide.cmd`, which packages the module and refreshes `WasmModule\Prebuilt`.

## Releasing

1. Change the version number in `BravoLights\BravoLights.csproj`. Follow https://semver.org/ versioning.
2. Using PowerShell, run `build-and-publish.ps1` from the root directory. It runs the tests, publishes a self-contained
   build and packages it into `BetterBravoLights.zip`.
3. Test that .zip, then commit, tag and release.

## Manual testing

1. Start BBL with the simulator closed: the dashboard should show *Not running* and the Bravo status.
2. Start MSFS 2024 (or 2020): the dashboard should connect and name the simulator.
3. Load a default aircraft, e.g. the C172: the matching built-in profile should become active.
4. Load an aircraft that uses LVars (e.g. Fenix or PMDG): the LVar module card should show a count, and the scanner
   should list the aircraft's LVars.
5. Edit a light in the active profile and save: the Bravo should update immediately.
6. Exit to the main menu: the lights should go out. Quit the sim: BBL should exit (if set to).
