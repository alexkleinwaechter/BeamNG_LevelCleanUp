using GeoTiff2BeamNG;
using ImageMagick;
using OSGeo.GDAL;
using System.ComponentModel;
using System.Windows.Markup;

internal class BeamNGTerrainFileBuilder
{
    private string CroppedOutputFile;
    private DirectoryInfo OutputDirectory { get; }
    private DirectoryInfo InputDirectory { get; }
    private string DateFileString { get; }
    private string TerrainFileName { get; }

    private Dictionary<string, MagickImage> images = new Dictionary<string, MagickImage>();
    public BeamNGTerrainFileBuilder(string croppedOutputFile, DirectoryInfo outputDirectory, DirectoryInfo inputDirectory)
    {
        CroppedOutputFile = croppedOutputFile;
        OutputDirectory = outputDirectory;
        InputDirectory = inputDirectory;
        DateFileString = DateTime.Now.ToString("yyyyMMdd_HHmmss_");
        TerrainFileName = $"{DateFileString}theTerrain.ter";
    }

    internal void Build()
    {
        
        var materialNames = new List<string>();

        var layers = Directory.GetFiles(InputDirectory.FullName, "*.png");

        if (layers.Count() == 0)
        {
            materialNames = new() //This should be dynamic!!!
            {
                "Grass2",
                "Dirt",
                "Mud",
                "asphalt",
                "ROCK",
                "asphalt2"
            };
            var materialNamesAsString = string.Join(", ", materialNames);
            LoggeM.WriteLine($"Using default material list ({materialNamesAsString})");
            LoggeM.WriteLine("If you want to use your own materials, add png's to the input folder with filename format materialName_Priority.png, eg: ROCK_10.png");
        }
        if (layers.Count() > 0) { materialNames.Clear(); }
        foreach ( var layer in layers)
        {
            var filename = new FileInfo(layer).Name;
            var materialName = filename.Split("_").First();

            materialNames.Add(materialName);
            LoggeM.WriteLine($"Added {materialName} to the material list");
        }

        var heightArray = GetHeightArray(CroppedOutputFile);
        WriteTerrainFile(heightArray, materialNames);

    }
    private void WriteTerrainFile(double[,] heightArray, List<string> materialNames)
    {
        LoggeM.WriteLine("Creating theTerrain.ter file");
        //data to the terrainfile is seemingly written to file startin lower left, to lower right, ending at upperright 
        byte version = 8; // unsure if beamng render/map version, or version of the map

        uint size = (uint)heightArray.GetLength(0);

        
        var binaryWriter = new BinaryWriter(File.Open($@"{OutputDirectory.FullName}\{TerrainFileName}", FileMode.Create));
        binaryWriter.Write(version);
        binaryWriter.Write(size);

        WriteHeightMap(binaryWriter, heightArray);
        WriteLayerMap(binaryWriter, heightArray);
        WriteLayerTexture(binaryWriter, heightArray);

        binaryWriter.Write(materialNames.Count);
        WriteMaterialNames(binaryWriter, materialNames);

        binaryWriter.Close();
    }
    private static void WriteMaterialNames(BinaryWriter binaryWriter, List<string> materialNames)
    {
        LoggeM.WriteLine("Adding material names to the terrain file...");
        foreach (var materialName in materialNames)
            binaryWriter.Write(materialName);
    }

    private static void WriteLayerTexture(BinaryWriter binaryWriter, double[,] heightArray)
    {
        LoggeM.WriteLine("Painting the material onto the terrain...");
        foreach (var p in heightArray)
            binaryWriter.Write(0);
    }

    private static void WriteLayerMap(BinaryWriter binaryWriter, double[,] heightArray)
    {

        var longitudes = heightArray.GetLength(0);
        var latitudes = heightArray.GetLength(1);

        var longitudeCounter = 0;
        var latitudeCounter = 0;



        while (latitudeCounter < latitudes)
        {
            byte theByte = 0;
            binaryWriter.Write(theByte);

            longitudeCounter++;

            if (longitudeCounter > longitudes - 1) 
            {
                longitudeCounter = 0;
                latitudeCounter++;
            }
        }
    }

    private void WriteHeightMap(BinaryWriter binaryWriter, double[,] heightArray)
    {
        LoggeM.WriteLine("Setting terrain heigt points...");
        var minAltitude = double.MaxValue;
        var maxAltitude = double.MinValue;
        foreach (var height in heightArray)
        {
            minAltitude = Math.Min(minAltitude, height);
            maxAltitude = Math.Max(maxAltitude, height);
        }
        var heightDifference = maxAltitude - minAltitude;
        var steps = 65535d;
        var stepsPerMeter = steps / heightDifference;

        var longitudeCounter = 0;
        var latitudeCounter = 0;
        var latitudes = heightArray.GetLength(1);

        while (latitudeCounter < latitudes)
        {
            var localAltitude = heightArray[longitudeCounter, latitudeCounter] - minAltitude;
            var binaryAltitude = localAltitude * stepsPerMeter;
            ushort binaryInt = (ushort)Math.Round(binaryAltitude, 0);
            binaryWriter.Write(binaryInt);

            longitudeCounter++;

            if (longitudeCounter > heightArray.GetLength(0) - 1)
            {
                longitudeCounter = 0;
                latitudeCounter++;
            }
        }
        LoggeM.WriteLine($"Done setting the height points. minAltitude: {minAltitude} maxAltitude: {maxAltitude} difference: {heightDifference}");

        var jsonString = $""""name":"theTerrain","class":"TerrainBlock","persistentId":"ter_pid","__parent":"MissionGroup","position":[0,0,{minAltitude}],"materialTextureSet":"BON_AAA_000TerrainMaterialTextureSet","maxHeight":{heightDifference},"terrainFile":"/levels/YOURLEVELNAME/{TerrainFileName}"""";
        jsonString = $"{{\"{jsonString}\"}}";

        File.WriteAllText($"{OutputDirectory}\\{DateFileString}items.level.json", jsonString);

    }
    

    private double[,] GetHeightArray(string fileName)
    {
        // Open the GeoTIFF file
        Dataset dataSet = Gdal.Open(fileName, Access.GA_ReadOnly);

        // Get the raster band
        Band band = dataSet.GetRasterBand(1);
        band.AsMDArray();

        // Get the dimensions of the raster
        int width = dataSet.RasterXSize;
        int height = dataSet.RasterYSize;

        // Get the value of the pixels at a given x and y coordinate
        var pixelValues = new double[width * height];

        band.ReadRaster(0, 0, width, height, pixelValues, width, height, 0, 0); //

        var resultPixelValues = new double[width, height];

        //pictures are read from top left to bottom right, but we use lon and lat, so we read from bottom left to top right, this takes care of that... 

        var longitudeCounter = 0;
        var latitudeCounter = height - 1;

        foreach (var value in pixelValues)
        {
            resultPixelValues[longitudeCounter, latitudeCounter] = value;

            longitudeCounter++;
            if (longitudeCounter > width - 1)
            {
                longitudeCounter = 0;
                latitudeCounter--;
            }
        }
        dataSet.FlushCache();
        dataSet.Dispose();

        
        
        return resultPixelValues;
    }
}