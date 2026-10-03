# Women of Wasteland

Independent BepInEx 5 / Harmony mod for Apocalypter. Converts the selected percentage of Flexa and Sprokka spawns into Gungirl and Shoota. Preserves their game equipment and FSMs, replaces the skinned models/textures, hides configured accessories, replaces local voice references, adjusts shout pauses, and dresses matching corpses and restored saves. Existing Gungirl/Shoota names and the old Flexa scale marker remain supported.

No Gunplay, Gunplay HUD, NPCAI, or ApocaSetter dependency is required. The original combined Apocaraider mod should be removed when installing its replacements to prevent running both implementations.

## Build and install

Requires a .NET SDK capable of building .NET Framework 4.7.2 and the game's installed BepInEx/game assemblies. Set `APOCALYPTER_GAME_DIR` to the installed Apocalypter directory, then run:

```powershell
dotnet build WomenOfWasteland.csproj -c Release
```

Alternatively pass `-p:GameDir="<installed game directory>"`. The build never deploys to the game. Copy `bin/Release/WomenOfWasteland.dll`, `bin/Release/Models/`, and `bin/Release/Sounds/` together into `BepInEx/plugins/WomenOfWasteland/` when installing. The resources must remain beside the DLL in these subdirectories.

## Settings and compatibility

`General/EnableFemaleNpc` and `FemalePopulation` retain their original semantics. Disabling new female spawns still restores existing females and corpses. `Gungirl` and `Shoota` retain the original `Model`, `Texture`, and `HideParts` keys; these six settings are now exposed to support model selection. Paths are relative to the plugin folder unless absolute. Changes to model/texture paths require restarting the game; accessory visibility applies to newly dressed NPCs. Both default GLB/PNG pairs are included.

The original voice defaults, voice normalization/volume, shout intervals and debug spawn key remain code-bound hidden settings, stored in a separate unsaved ConfigFile. The shipped voice set applies to both female variants, preserving Apocaraider behavior. `Debug/VerboseLog` is visible.

ApocaSetter discovers the plugin's normal BepInEx Config entries through `General/Apocasetter=true`; no reflection into Apocaraider is required. On first use of the new config, matching exposed settings are imported from `com.denis.apocalypter.apocaraider.cfg` without editing that file. Existing replacement configs take precedence. ApocaSetter index summaries/update listings would require adding the new GUID to its external index; settings discovery already works without such an entry.

Only `CreateObject.OnEnter` is patched, with a female conversion/corpse postfix. A persistent runner survives the game's destruction of plugin GameObjects during scene transitions. Save restoration scans run independently, quickly during initial loading and every five seconds afterwards, replacing the old optimization through the AI registrar.

Reflection users can discover public `WomenOfWasteland.Gungirl.MakeGungirl(GameObject)`, `IsGungirl(GameObject)`, and `IsFemale(GameObject)` in the new assembly. Consumers of the original Apocaraider namespace must update their optional reflection lookup.

## Verification

Release compilation: **BUILD VERIFIED**, zero warnings/errors with .NET SDK 10.0.401 against the installed game assemblies. The source extraction, resource hashes, no-sibling dependency design and ApocaSetter discovery were checked statically. **RUNTIME NOT VERIFIED**: the game was not launched. In-game checks remain for female selection, saved/corpse restoration, mesh deformation/accessories, voice replacement, and toggling settings across scene transitions.
