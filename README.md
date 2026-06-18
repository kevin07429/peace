# Peace PAK Workbench

Peace PAK Workbench is a Windows WPF utility for inspecting, unpacking, repacking, and applying learned binary or JSON patch recipes to Unreal Engine PAK files. It focuses on repeatable local workflows: detect source PAK metadata, preserve original layout where possible, and keep preset patches guarded by exact recipes.

## Features

- Drag-and-drop PAK selection with output folder management.
- PAK unpack and repack flows that preserve detected compression, encryption, mount point, and native library settings.
- Learned binary recipe support for exact in-place patching.
- JSON recipe tooling for learning, relocating, and applying structured patch operations.
- Dedicated asset and recoil editor windows for inspecting extracted asset values.
- Guarded preset buttons for known glow, range, recoil, and combined recipes.
- Activity log with progress reporting for long-running operations.

## Requirements

- Windows 10 or later.
- .NET 10 SDK for building from source.
- Native libraries included under `Tools/Native`.
- Optional Oodle DLL when working with Oodle-compressed PAK files.

## Build

```powershell
dotnet build .\PakToolGUI.csproj -c Release
```

The build output is written to:

```text
artifacts\publish\
```

Run the GUI with:

```powershell
.\artifacts\publish\PakToolGUI.exe
```

## Project Layout

```text
App.xaml / App.xaml.cs              WPF application resources and startup
MainWindow.xaml / MainWindow.xaml.cs Main workbench UI and preset flows
PakPatchPacker.cs                   PAK index, patch recipe, and in-place patch logic
PakPacker.cs                        Repack support
PakIndexReader.cs                   PAK index inspection
UAssetParser.cs                     UAssetAPI-based asset parsing helpers
RecoilEditorWindow.*                Recoil inspection and editing window
AssetEditorWindow.*                 Asset inspection and editing window
Tools/                              Native and helper binaries
Data/                               Local reference data
```

## Notes

- Keep original PAK files backed up before applying any patch.
- Exact binary recipes are version-sensitive. If the source hash does not match, learn or relocate the recipe before applying it.
- Experimental presets should stay blocked until a stable target field is verified.
- This project is intended for local research, backup, and asset workflow automation. Use it only with files you are allowed to inspect or modify.

## License

This project is licensed under the MIT License. See [LICENSE](LICENSE).
