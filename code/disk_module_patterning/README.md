# SOLIDWORKS CSV Pattern Tool

A standalone executable that reads a spreadsheet to generate a customized pattern of a subassembly in SOLIDWORKS. For each row in the CSV, the tool inserts the subassembly, selects the correct configuration, applies rotations and translations, renames the instance in the Feature Tree, and fixes it in place.

## Background
Code was initially generated 2026-11-18 by Joe Silber (LBNL) using Gemini Pro.

Initial intended usage is for patterning ePIC SVT Disk Modules per tiling patterns provided by Ernst Sichtermann (LBNL).

## Prerequisites
* **SOLIDWORKS** must be open and running on your machine before launching the tool.
* **.NET SDK** (only needed if you are compiling from source).

## Input CSV Format
The tool looks for specific column headers to map the data (column order does not matter). The required headers are:
* `type_id`: A string that matches (or is a substring of) the target configuration name in the subassembly.
* `Rx_deg`, `Ry_deg`, `Rz_deg`: Rotations in degrees.
* `x_mm`, `y_mm`, `z_mm`: Translations in millimeters.
* `disk_id`, `row_id`, `x_id`: Used to generate the Feature Tree name for the instance (e.g., `SubAssyName Disk-1 Row-2 Position-3`).

*Note: Any additional columns in your CSV (like `face_id`) will be safely ignored.*

## Usage
1. Ensure SOLIDWORKS is running.
2. Run the compiled `.exe`. 
3. A file dialog will prompt you to select your **CSV data file**.
4. A second dialog will prompt you to select the **.SLDASM file** you want to pattern.
5. The tool will automatically create a new assembly, process the CSV, and build the pattern. 

## Example files
A set of example / test files are provided [here](./examples).

| File | Description |
| ---- | ----------- |
| EC-1016-8228_DISK_MODULE_YYYY-MM-DD.SLDASM | Disk module subassembly which is to be patterned. |
| ED1_modules_cad_2026-11-11.csv | Pattern file, specifying locations, orientations, configurations, and ids. |

## Compiling
If you were not provided a working `.exe` or need to recompile for some reason, do as follows. (Alternately you can run in the VS Code debugger, see below.)

1. You must have the [.NET SDK](https://dotnet.microsoft.com/download) installed on your system to compile the code.

2. Go to this repo folder in your terminal where the `.cs` and `.csproj` files live.

3. Do this: `dotnet build -c Release`

It will generate a `.exe` that you can run whenever you need it.

As of 2026-11-18, I was doing the compilation in VS Code, using .NET version 11.0. 

## Debugging
I installed the **C# Dev Kit** extension, which gives VS Code debugging support. Make sure SOLIDWORKS is already running in the background before you start, because your code immediately tries to connect to it and will exit if it is closed. Then, go to `Patternscript.cs` and press F5 on your keyboard or click the Run and Debug icon on the left sidebar and start the debugger.

