using GeoTiff2BeamNG;
using OSGeo.GDAL;
using OSGeo.OGR;
using System;
using System.IO;
using System.Runtime.InteropServices;

var InputDirectory = new DirectoryInfo(@"C:\GeoTiff2BeamNG\Input");
var OutputDirectory = new DirectoryInfo(@"c:\GeoTiff2BeamNG\Output");
var CombinedOutputFile = "Combined.tif";
var CroppedOutputFile = "Cropped.tif";

CheckArgs();
GdalSetup();

BoundaryBox inputBB = await new CombineGeoTiffs(InputDirectory, CombinedOutputFile).Combine();
await new GeoTiffCropper().GeoTiffOutputExtractor(CombinedOutputFile, CroppedOutputFile, inputBB);
new BeamNGTerrainFileBuilder(CroppedOutputFile, OutputDirectory, InputDirectory).Build();

Cleanup();

Console.WriteLine("We are done, press enter to exit...");
Console.ReadLine();
void Cleanup()
{
    LoggeM.WriteLine("Cleaning up temporary files...");
    File.Delete(CombinedOutputFile);
}

void CheckArgs()
{
    var exit = false;
    var count = 0;
    foreach (string arg in args)
    {
        if (arg == "-i") InputDirectory = new(args[count + 1]);
        if (arg == "-o") OutputDirectory = new(args[count + 1]);
        count++;
    }
    Console.WriteLine("Hello, BeamNG Worldcreator!");

    if (!InputDirectory.Exists)
    {
        LoggeM.WriteLine($"'{InputDirectory.FullName}' is not a valid folder, use -i 'Path' to set correct folder, or create the folder.");
        exit = true;
    }
    if (!OutputDirectory.Exists)
    {
        LoggeM.WriteLine($"'{OutputDirectory.FullName}' is not a valid folder, use -o 'Path' to set correct folder, or create the folder.");
        exit = true;
    }
    if (exit) Environment.Exit(0);

}
void GdalSetup()
{
    GdalConfiguration.ConfigureGdal();
    GdalConfiguration.ConfigureOgr();

    Gdal.AllRegister();
    Ogr.RegisterAll();
}