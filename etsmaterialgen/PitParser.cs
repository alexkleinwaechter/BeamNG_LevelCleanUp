namespace etsmaterialgen
{
    internal class PitParser
    {
        public Document Parse(string filePath)
        {
            var document = new Document();
            var lines = File.ReadAllLines(filePath);
            Look currentLook = null;
            Material currentMaterial = null;
            Variant currentVariant = null;

            foreach (var line in lines)
            {
                if (line.StartsWith("Header"))
                {
                    document.Header = new Header();
                }
                else if (line.StartsWith("Global"))
                {
                    document.Global = new Global();
                }
                else if (line.StartsWith("Look"))
                {
                    currentLook = new Look();
                    document.Looks.Add(currentLook);
                }
                else if (line.StartsWith("Material") && currentLook != null)
                {
                    currentMaterial = new Material();
                    currentLook.Materials.Add(currentMaterial);
                }
                else if (line.StartsWith("Variant"))
                {
                    currentVariant = new Variant();
                    document.Variants.Add(currentVariant);
                }
                // Add more else if blocks for other structures

                // Parse key-value pairs
                ParseKeyValue(line, document, currentLook, currentMaterial, currentVariant);
            }

            return document;
        }

        private void ParseKeyValue(string line, Document document, Look currentLook, Material currentMaterial, Variant currentVariant)
        {
            var parts = line.Split(new[] { ':', '"' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2) return;

            var key = parts[0].Trim();
            var value = parts[1].Trim();

            // Assign values to the current object in context
            if (document.Header != null)
            {
                // Assign values to document.Header based on key
            }
            else if (document.Global != null)
            {
                // Assign values to document.Global based on key
            }
            else if (currentLook != null)
            {
                // Assign values to currentLook based on key
            }
            else if (currentMaterial != null)
            {
                // Assign values to currentMaterial based on key
            }
            else if (currentVariant != null)
            {
                // Assign values to currentVariant based on key
            }
        }
    }

    public class Document
    {
        public Header Header { get; set; }
        public Global Global { get; set; }
        public List<Look> Looks { get; set; } = new List<Look>();
        public List<Variant> Variants { get; set; } = new List<Variant>();
    }

    public class Header
    {
        public int FormatVersion { get; set; }
        public string Source { get; set; }
        public string Type { get; set; }
        public string Name { get; set; }
    }

    public class Global
    {
        // Define properties for Global
    }

    public class Look
    {
        public string Name { get; set; }
        public List<Material> Materials { get; set; } = new List<Material>();
    }

    public class Material
    {
        // Define properties for Material
    }

    public class Variant
    {
        // Define properties for Variant
    }
}
