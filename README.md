# Real Portal

Source code of **Real Portal**, a Valheim mod: portals you can see through and walk through, with a live view of the destination and no loading screen.

- Thunderstore: [drakeich/RealPortal](https://thunderstore.io/c/valheim/p/drakeich/RealPortal/)
- Nexus Mods: [Real Portal](https://www.nexusmods.com/valheim/mods/4547)
- What the mod does and its settings: [Release/thunderstore/README.md](Release/thunderstore/README.md); changes: [CHANGELOG](Release/thunderstore/CHANGELOG.md)

## Where each file of the package comes from

| Package file | Source |
|---|---|
| `plugins/RealPortal.dll` | `Plugin/`, with `Unity/Assets/RealPortal/Runtime/` compiled in |
| `plugins/realportal.bundle` | the shaders in `Unity/Assets/RealPortal/Shaders/`, built by the Unity project (`Real Portal/Build bundle`, `Unity/Assets/RealPortal/Editor/RealPortalBuild.cs`) |
| `plugins/RealPortalEngine.dll` | `Engine/` (the helper plugin; every optimization in it can be switched off in its config) |
| `plugins/RealPortal.Burst.dll`, `plugins/RealPortal.Burst_win_x86_64.dll`, `plugins/RealPortal.Burst.json` | `Unity/Assets/RealPortal.Burst/`: jobs compiled by Unity's Burst compiler (`Real Portal/Build Burst library`, `Unity/Assets/RealPortal/Editor/BurstLibraryBuild.cs`). The native DLL is the Burst output of that C# code (`lib_burst_generated.dll` of one player build), the JSON ties the pair together. The engine loads them when a job first asks for them (`Engine/Modules/BurstBootstrapModule.cs`). Since 1.0.5 the plugin's proxy cull and emission run through them (`Unity/Assets/RealPortal.Burst/ProxyViewJobs.cs`, the Burst twins of the jobs in `Plugin/FarWorld/Proxy/ProxyDraw.cs`), after the plugin compared them with its own jobs on its first renders; without the library the same jobs run as plain C#. |
| `patchers/RealPortal.Preloader.dll` | `Engine/Preloader/`: a BepInEx preloader patcher that marks six ZDO methods NoInlining and adds one int field to ZDO for the engine's ZDO journal (see the comment at the top of `Preloader.cs`); without it the engine falls back to Harmony alone. |

## Building

- Plugin and engine: `dotnet build Plugin/RealPortal.csproj -c Release` (builds `Engine/` too). The projects reference the game's managed DLLs and BepInEx: pass `-p:ValheimDir=...` and `-p:BepInExDir=...` if yours are not in the default places.
- Preloader: `dotnet build Engine/Preloader/RealPortal.Preloader.csproj -c Release`.
- Shader bundle and Burst library: the Unity project in `Unity/` (Unity 6000.0.75f1, the game's version), menu `Real Portal`.
