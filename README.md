# BeamNG Tools for Mapbuilders
Clean your map level from unused files before deployment

Watch the tutorial videos:

[![Watch the video](https://img.youtube.com/vi/-M06aIGzuKk/0.jpg)](https://youtu.be/-M06aIGzuKk) 
[![Watch the video](https://img.youtube.com/vi/OE02UEP8ups/0.jpg)](https://youtu.be/OE02UEP8ups) 
## Features
1. Map Shrinker
You can load a zipped map and let the tool search for orphaned files and assets which are not needed in the map. This can save a lot of space. All files and configurations will be checked against your scene tree with all of its dependencies and a proposal for file deletion is shown. Please check the list. It is always possible that the tool deletes too much. So always use a copy of your project! This tool is made for deleting things :) The good thing: You still have all your crap in the project and can use it for further ideas while having a lean deployment file.

2. Rename Map
You can load a zipped map and then change the name in the file system. This is important if you want to see this map beside the original one. If you customized the industrial level for example and want to have your own customized level beside industrial, you could rename it with industrial_larry :) You can change the shown name in the map overview, too. After renaming you can build a new zipfile with the tool and you are ready to go.

3. Copy Assets (Experimental)
You can load a zipped map you want to copy assets from and a zipped map you want to have the assets. At the moment the tool allows to copy decalroads, decals and collada assets (dae files). You get a list of all the assets and can select the ones you want to copy. No need anymore for copying the whole folders from the desired maps. The tool copies only the needed materials and places them in a dedicated folder per assettype starting with a "MT_" for Mapping-tools.

This functionality is not well tested yet. Therefore the experimantal state. Very old maps with a lot of cs files are likely not working with this functionality. This tool doesn't like corrupted json files or files with duplicate keys (and the funny json comments) which you can even find in the vanilla maps. The tool throws an error with a detailed errormessage and if your assets aren't copied because of that you have to correct the errors and start again.

Before you start
It's a good idea to copy the maps you want to work with to a dedicated folder. The tool does a lot of unzipping, zipping and logfiles. You don't want to have that in your vanilla maps folder :)

## Performance Example

Extreme Example: 
East Coast Reworked | v2.5
The tool shrinks out almost 4.7 GB of data
Zipsize before: 3.1 GB
Zipsize after: 1.1 GB

## Download
Open the [latest release](https://github.com/alexkleinwaechter/BeamNG_LevelCleanUp/releases/) on this page and download the *.exe file. It is not signed yet. Windows will ask you to allow execution. Why so big? 
The tool is coded in Microsoft .Net and the download contains all necessary framework files to run without having to install .Net on your computer.

## Warning
Never use this tool on your working project files. Always make a copy and use it with this tool. It is meant to delete things. 
## Roadmap
- Copy forest, prefab and terrain assets from other maps to your project
- Convert content of missiongroup folder to forest items

## Not working
- Maps linking to cdae files instead of dae files. Since I can't disassemble cdae files it is not possible to get the materiallist out of them. Tokyo's Shuto Expressway will fail in a great way bcause of that. As long as you provide the dae file along the cdae file in the same directory the tool will work and delete the dae files later on.

-----
