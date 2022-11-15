# BeamNG Tools for Mapbuilders
Clean your map level from unused files before deployment

## Features
- Analyze your map and delete all unused files.
- Copy another map and rename it, then you can use it as a basemap for your new project

## Download
Open the [latest release](https://github.com/alexkleinwaechter/BeamNG_LevelCleanUp/releases/) on this page and download the *.exe file. It is not signed yet. Windows will ask you to allow execution. Why so big? 
The tool is coded in Microsoft .Net and the download contains all necessary framework files to run without having to install .Net on your computer.

## Roadmap
- Copy assets from other maps to your project

## Not working
- Maps linking to cdae files instead of dae files. Since I can't disassemble cdae files it is not possible to get the materiallist out of them. Tokyo's Shuto Expressway will fail in a great way bcause of that. As long as you provide the dae file along the cdae file in the same directory the tool will work and delete the dae files later on.
