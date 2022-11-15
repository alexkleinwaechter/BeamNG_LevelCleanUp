# BeamNG Tools for Mapbuilders
Clean your map level from unused files before deployment

Watch the tutorial video:

[![Watch the video](https://i9.ytimg.com/vi/ZA1zjmpe1VU/mqdefault.jpg?sqp=COzhz5sG-oaymwEmCMACELQB8quKqQMa8AEB-AHUBoAC4AOKAgwIABABGDwgZShlMA8=&rs=AOn4CLAVTPeIFcU0jd5WY_clYZpRbE5RKQ)](https://www.youtube.com/watch?v=ZA1zjmpe1VU) 
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
