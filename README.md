# BeamNG Tools for Mapbuilders
Clean your map level from unused files before deployment

Watch the tutorial videos:

[![Watch the video](https://img.youtube.com/vi/tX4AfwVJY7s/0.jpg)](https://youtu.be/tX4AfwVJY7s) 
[![Watch the video](https://img.youtube.com/vi/-M06aIGzuKk/0.jpg)](https://youtu.be/-M06aIGzuKk) 
[![Watch the video](https://img.youtube.com/vi/OE02UEP8ups/0.jpg)](https://youtu.be/OE02UEP8ups) 
[![Watch the video](https://img.youtube.com/vi/a4nC3N8aasI/0.jpg)](https://youtu.be/a4nC3N8aasI) 

## Features

### Create level wizard
This feature guides you through the whole process to get your first map up and running

### Generate Terrain
Generate your real life terrain from geotiff elevation data together with smooth roads using data from Open Street Map.

### Map Shrinker
You can load a zipped map and let the tool search for orphaned files and assets which are not needed in the map. This can save a lot of space. All files and configurations will be checked against your scene tree with all of its dependencies and a proposal for file deletion is shown. Please check the list. It is always possible that the tool deletes too much. So always use a copy of your project! This tool is made for deleting things :) The good thing: You still have all your crap in the project and can use it for further ideas while having a lean deployment file.

### Rename Map
You can load a zipped map and then change the name in the file system. This is important if you want to see this map beside the original one. If you customized the industrial level for example and want to have your own customized level beside industrial, you could rename it with industrial_larry :) You can change the shown name in the map overview, too. After renaming you can build a new zipfile with the tool and you are ready to go.
The "Rename Map" feature automatically detects changes you have made in world editor and asks you if you want to add them to yor renamed map.

### Copy Assets (Experimental)
You can load a zipped map you want to copy assets from and a zipped map you want to have the assets. At the moment the tool allows to copy decalroads, decals and collada assets (dae files). You get a list of all the assets and can select the ones you want to copy. No need anymore for copying the whole folders from the desired maps. The tool copies only the needed materials and places them in a dedicated folder per assettype starting with a "MT_" for Mapping-tools.

### Copy/Replace Terrain Materials
A powerful feature for transferring terrain materials between BeamNG.drive maps.

Flexible Copy Modes:
- Add Mode: Copy terrain materials from source to target with automatic name suffixing to prevent conflicts
- Replace Mode: Replace existing target terrain materials while preserving their names and persistent IDs

Smart Material Handling:
- Auto-detect and copy all material dependencies
- Dynamic roughness and base color adjustment for each material
- Material-specific roughness presets (Wet Asphalt, Concrete, Grass, Mud, etc.)
- Visual color picker for custom base colors (painting the ground)
  
Automatic GroundCover Management:
- Automatically detects and copies associated groundcover vegetation
- Updates groundcover layer references to match new terrain materials
- In Replace mode: cleanly removes old groundcover layers referencing replaced materials
- Copies groundcover dependencies (materials, DAE meshes)
  
PBR (Physically Based Rendering) Upgrade:
- Automatically detects if target level lacks PBR terrain materials
- Prompts for optional upgrade to modern PBR format
- Extracts texture sizes from source level's TerrainMaterialTextureSet
- Creates and configures TerrainMaterialTextureSet in target level
- Updates TerrainBlock objects to reference the new texture set
  
Multi-Target Replacement:
- Replace multiple target materials with a single source material (e.g., replace all grass types with snow)
- Batch processing for efficient large-scale material operations
  
Use Cases
- Just add new terrain to your map
- Winter Maps: Replace grass materials with snow across an entire map
- Desert Conversions: Swap vegetation materials for sand/dirt terrain
- Legacy Upgrades: Modernize old maps to use PBR terrain rendering
  
Technical Details
- Generates placeholder textures for base color, roughness, and normal maps

### Convert TSStatic Assets to forest items
You can load a zipped map and then convert your static assets placed in the scene tree to forest items. After selecting a level your scene tree is shown in the tool to select the items or folders you want to convert. Only assets with an identical scale on all axis and an existing rotation matrix will be converted.

During the process the tool asks if you want to delete the assets out of the scene tree. If you don't delete them you will have duplicate assets. One in the tree and one in the forest items. This only makes sense to check if the tool does it right :-)

A discussion if it generally makes sense to convert static assets to forest items can be found here:
https://www.beamng.com/threads/converting-placed-meshes-to-forest-items.86837

### Before you start
It's a good idea to copy the maps you want to work with to a dedicated folder. The tool does a lot of unzipping, zipping and logfiles. You don't want to have that in your vanilla maps or your mods folder :)

## Performance Example

Extreme Example: 
East Coast Reworked | v2.5
The tool shrinks out almost 4.7 GB of data
Zipsize before: 3.1 GB
Zipsize after: 1.1 GB

## Download
Open the [latest release](https://github.com/alexkleinwaechter/BeamNG_LevelCleanUp/releases/) on this page and download the *.exe file. It is not signed yet. Windows will ask you to allow execution. Why so big? 
The tool is coded in Microsoft .Net and the download contains all necessary framework files to run without having to install .Net on your computer.

## Troubleshooting
If you get the error: System.IO.FileNotFoundException: Cannot locate the file, you need to install the Microsoft WebView2 Runtime.
The tool needs the Microsoft WebView2 Runtime to run properly. Its already installed on Windows 11 and newer versions of Windows 10. You can Download and install the Runtime from this Website:
https://developer.microsoft.com/en-us/microsoft-edge/webview2/
Direct link to the installer:
https://go.microsoft.com/fwlink/p/?LinkId=2124703

## Warning
Never use this tool on your working project files. Always make a copy and use it with this tool. It is meant to delete things. 

## Roadmap
- Copy forest, prefab and terrain assets from other maps to your project
- Convert content of missiongroup folder to forest items (DONE!)

## Not working
- Map Shrinker: maps linking to cdae files instead of dae files. Since I can't disassemble cdae files it is not possible to get the materiallist out of them. Tokyo's Shuto Expressway will fail in a great way bcause of that. As long as you provide the dae file along the cdae file in the same directory the tool will work and delete the dae files later on.

-----
