using GeoTiff2BeamNG;
using OSGeo.GDAL;
using System.Data;

internal class CombineGeoTiffs
{
    internal DirectoryInfo InputFolder { get;}
    internal string CombinedOutputFile;
    public CombineGeoTiffs(DirectoryInfo inputFolder, string combinedOutputFile)
    {
        InputFolder= inputFolder;
        CombinedOutputFile= combinedOutputFile;
    }

    internal async Task<BoundaryBox> Combine()
    {
        LoggeM.WriteLine("Combining GeoTiff files from input folder...");
        var inputFiles = new List<string>();
        var tifInputFiles = Directory.GetFiles(InputFolder.FullName, "*.tif");
        var tiffInputFiles = Directory.GetFiles(InputFolder.FullName, "*.tiff");
        var geotiffInputFiles = Directory.GetFiles(InputFolder.FullName, "*.geotiff");
        inputFiles.AddRange(tifInputFiles.ToList());
        inputFiles.AddRange(tiffInputFiles.ToList());
        inputFiles.AddRange(geotiffInputFiles.ToList());

        if (inputFiles.Count == 0)
        {
            LoggeM.WriteLine($"No files found in '{InputFolder}'. .tif, .tiff and .geotiff supported");
            Environment.Exit(0);
        }

        LoggeM.WriteLine($"Found {inputFiles.Count} files");

        // Initialize variables to store the overall bounding box of all input files
        double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue;
        double[] geoTransform = new double[6];
        double pixelSizeX = 0.0;
        double pixelSizeY = 0.0;

        // Open the first GeoTIFF file to get its information
        Dataset firstDataset = Gdal.Open(inputFiles[0], Access.GA_ReadOnly);

        // Calculate the dimensions of each tile
        int tileWidth = firstDataset.RasterXSize;
        int tileHeight = firstDataset.RasterYSize;

        foreach (var file in inputFiles)
        {
            Dataset dataset = Gdal.Open(file, Access.GA_ReadOnly);
            if (tileWidth != dataset.RasterXSize || tileHeight != dataset.RasterYSize) throw new Exception("Tiles should be the same size");

            dataset.GetGeoTransform(geoTransform);

            pixelSizeX = Math.Abs(geoTransform[1]); // Get the absolute pixel size in X direction
            pixelSizeY = Math.Abs(geoTransform[5]); // Get the absolute pixel size in Y direction

            // Calculate the bounding box coordinates in geographic space
            double fileMinX = geoTransform[0];
            double fileMaxX = geoTransform[0] + geoTransform[1] * dataset.RasterXSize;
            double fileMinY = geoTransform[3] - geoTransform[1] * dataset.RasterYSize;
            double fileMaxY = geoTransform[3];

            // Update overall bounding box
            minX = Math.Min(minX, fileMinX);
            minY = Math.Min(minY, fileMinY);
            maxX = Math.Max(maxX, fileMaxX);
            maxY = Math.Max(maxY, fileMaxY);

            dataset.Dispose();
        }
        //maxX -= tileWidth * pixelSizeX; // Adjust for pixel size
        //minY -= tileHeight * pixelSizeY; // Adjust for pixel size

        ////////////////////////////////////this is wrong when there is just one tile... somehow... 

        var extentX = maxX - minX;
        var extentY = maxY - minY;

        var totalExtentMultiplyerX = (int)(Math.Pow(2, Math.Floor(Math.Log(extentX, 2))))/2048;//Math.Floor(extentX / 2048);
        var totalExtentMultiplyerY = (int)(Math.Pow(2, Math.Floor(Math.Log(extentX, 2))))/2048;//Math.Floor(extentY / 2048);

        if (totalExtentMultiplyerX == 3) totalExtentMultiplyerX = 2;
        if (totalExtentMultiplyerX == 5) totalExtentMultiplyerX = 4;
        if (totalExtentMultiplyerX == 6) totalExtentMultiplyerX = 4;
        if (totalExtentMultiplyerX == 7) totalExtentMultiplyerX = 4;

        var totalExtentX = (int)totalExtentMultiplyerX * 2048;
        var totalExtentY = (int)totalExtentMultiplyerY * 2048;
        var totalExtent = Math.Min(totalExtentX, totalExtentY); // Take the minimum extent

        var centerX = minX + ((maxX - minX) / 2);
        var centerY = minY + ((maxY - minY) / 2);

        // Calculate the dimensions of the final output image
        int totalWidth = tileWidth * (int)Math.Sqrt(inputFiles.Count);
        int totalHeight = tileHeight * (int)Math.Sqrt(inputFiles.Count);

        if (totalWidth == 0 || totalHeight == 0)
        {
            LoggeM.WriteLine("The files in the input folder, does not create a map large enough to make the minimum size map of 2048x2048m, please add more files!");
            LoggeM.WriteLine($"Current extent: Longitudes: {extentX} Latitudes: {extentY}");
            Environment.Exit(0);
        }

        var outputFile = CombinedOutputFile;
        if (File.Exists(outputFile)) File.Delete(outputFile);

        // Create the output dataset with the calculated dimensions
        Dataset outputDataset = Gdal.GetDriverByName("GTiff").Create(
            outputFile,
            totalWidth,
            totalHeight,
            firstDataset.RasterCount,
            firstDataset.GetRasterBand(1).DataType,
            null);

        geoTransform[0] = minX;
        geoTransform[3] = maxY;

        outputDataset.SetGeoTransform(geoTransform);
        var firstGeoProjection = firstDataset.GetProjection();
        outputDataset.SetProjection(firstGeoProjection);

        // Loop through input files and copy raster data to the output
        foreach (var inputFile in inputFiles)
        {
            // Calculate the current tile's offset within the output image based on geographic location
            var currentGeoTransform = new double[6];
            Dataset currentDataset = Gdal.Open(inputFile, Access.GA_ReadOnly);
            currentDataset.GetGeoTransform(currentGeoTransform);

            int xOffset = (int)((currentGeoTransform[0] - minX) / pixelSizeX); // Adjust for pixel size
            int yOffset = (int)((maxY - currentGeoTransform[3]) / pixelSizeY); // Adjust for pixel size

            // Ensure xOffset and yOffset are within the bounds of the output image
            xOffset = Math.Max(xOffset, 0);
            yOffset = Math.Max(yOffset, 0);
            int maxXOffset = totalWidth - tileWidth;
            int maxYOffset = totalHeight - tileHeight;
            xOffset = Math.Min(xOffset, maxXOffset);
            yOffset = Math.Min(yOffset, maxYOffset);

            // Loop through bands in the input file
            for (int bandIndex = 1; bandIndex <= currentDataset.RasterCount; bandIndex++)
            {
                Band inputBand = currentDataset.GetRasterBand(bandIndex);
                Band outputBand = outputDataset.GetRasterBand(bandIndex);

                int xSize = tileWidth;
                int ySize = tileHeight;

                // Read data from the current input tile's band
                double[] buffer = new double[xSize * ySize];
                inputBand.ReadRaster(0, 0, xSize, ySize, buffer, xSize, ySize, 0, 0);

                // Write data to the output image at the calculated offset
                outputBand.WriteRaster(xOffset, yOffset, xSize, ySize, buffer, xSize, ySize, 0, 0);
            }

            // Dispose of the input dataset
            currentDataset.Dispose();
        }


        // Dispose of the output dataset
        outputDataset.FlushCache();
        firstDataset.Dispose();
        outputDataset.Dispose();
        
        decimal bbLon = (decimal)centerX;
        decimal bbLat = (decimal)centerY;
        decimal bbr = (decimal)totalExtent / 2;

        var lowerLeft = new CorePoint(bbLon - bbr, bbLat - bbr);
        var upperRight = new CorePoint(bbLon + bbr, bbLat + bbr);
        var boundaryBox = new BoundaryBox(lowerLeft, upperRight);

        return boundaryBox;
    }
}