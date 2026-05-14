if (args.Length != 3)
{
    Console.Error.WriteLine("Usage: fbxtodae <fbxSourceDir> <textureSourceDir> <targetDir>");
    Console.Error.WriteLine();
    Console.Error.WriteLine("  fbxSourceDir     Folder containing *.fbx files (non-recursive).");
    Console.Error.WriteLine("  textureSourceDir Folder containing {fbxname}_d.png and {fbxname}_n.png.");
    Console.Error.WriteLine("  targetDir        Output folder for *.dae files and main.materials.json.");
    return 2;
}

return FbxToDae.Program.Run(args[0], args[1], args[2]);

// FbxToDae.Program: runner helpers separate from the compiler-synthesized top-level Program class.
namespace FbxToDae
{
    public static class Program
    {
        public static int Run(string fbxDir, string textureDir, string targetDir)
        {
            if (!Directory.Exists(fbxDir))
            {
                Console.Error.WriteLine($"FBX source directory does not exist: {fbxDir}");
                return 1;
            }
            if (!Directory.Exists(textureDir))
            {
                Console.Error.WriteLine($"Texture source directory does not exist: {textureDir}");
                return 1;
            }
            Directory.CreateDirectory(targetDir);

            var fbxFiles = Directory.EnumerateFiles(fbxDir, "*", SearchOption.TopDirectoryOnly)
                .Where(p => p.EndsWith(".fbx", StringComparison.OrdinalIgnoreCase))
                .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (fbxFiles.Count == 0)
            {
                Console.Error.WriteLine($"No *.fbx files found in {fbxDir}");
                return 1;
            }

            Console.WriteLine($"Converting {fbxFiles.Count} FBX file(s) to {targetDir}");

            var beamngPathPrefix = BeamngPath.DeriveLevelRelativePrefix(targetDir);
            if (beamngPathPrefix is null)
                Console.Error.WriteLine(
                    "  WARN: target dir is not under a BeamNG '/levels/' path; "
                  + "materials.json will reference textures by filename only (won't resolve in-game).");
            else
                Console.WriteLine($"BeamNG texture path prefix: {beamngPathPrefix}");

            var textures = new TextureMatcher(textureDir);
            var materialsPath = Path.Combine(targetDir, "main.materials.json");
            var materials = new MaterialsJsonWriter(existingFile: materialsPath);
            var converter = new Converter(textures, materials, targetDir, beamngPathPrefix);

            int ok = 0, failed = 0;
            foreach (var fbx in fbxFiles)
            {
                Console.WriteLine(Path.GetFileName(fbx));
                try
                {
                    converter.Convert(fbx);
                    ok++;
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"  ERROR: {ex.Message}");
                    failed++;
                }
            }

            materials.Save(materialsPath);

            Console.WriteLine();
            Console.WriteLine($"Done. {ok} converted, {failed} failed. Materials -> {materialsPath}");
            return failed == 0 ? 0 : 1;
        }
    }
}
