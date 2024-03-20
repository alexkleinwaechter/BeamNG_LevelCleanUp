namespace etsmaterialgen
{
    internal class PitParser
    {
        public List<MaterialEts> Parse(string filePath)
        {
            var lines = File.ReadAllLines(filePath);
            LookEts currentLook = null;
            MaterialEts currentMaterial = null;
            TextureEts currentTexture = null;
            var allMaterials = new List<MaterialEts>();
            var allLooks = new List<LookEts>();

            foreach (var line in lines)
            {
                var trimmedLine = line.Trim();
                if (trimmedLine.StartsWith("Look {"))
                {
                    currentLook = new LookEts();
                    allLooks.Add(currentLook);
                }
                else if (trimmedLine.StartsWith("Material {") && currentLook != null)
                {
                    currentMaterial = new MaterialEts();
                    currentTexture = null;
                    currentLook.Materials.Add(currentMaterial);
                    allMaterials.Add(currentMaterial);
                }
                else if (trimmedLine.StartsWith("Texture {") && currentMaterial != null)
                {
                    currentTexture = new TextureEts();
                    currentMaterial.Textures.Add(currentTexture);
                }
                else if (trimmedLine.StartsWith("Attribute {") && currentMaterial != null)
                {
                    currentTexture = null;
                }

                // Parse key-value pairs
                if (currentLook != null)
                {
                    ParseKeyValue(trimmedLine, currentLook, currentMaterial, currentTexture);
                }
            }

            return allMaterials;
        }

        private void ParseKeyValue(string line, LookEts currentLook, MaterialEts currentMaterial, TextureEts currentTexture)
        {
            var parts = line.Replace("\"", "").Split(": ", StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2) return;

            var key = parts[0].Trim();
            var value = parts[1].Trim();

            // Assign values to the current object in context
            if (currentLook != null && currentMaterial == null)
            {
                switch (key)
                {
                    case "Name":
                        currentLook.Name = value;
                        break;
                }
            }
            if (currentMaterial != null)
            {
                switch (key)
                {
                    case "Alias":
                        currentMaterial.Alias = value;
                        break;
                    case "Effect":
                        currentMaterial.Effect = value;
                        if (value.Contains("decal.over"))
                        {
                            currentMaterial.AddLerp = true;
                        }
                        if (value.Contains("lightmap.night"))
                        {
                            currentMaterial.UseGlow = true;
                            currentMaterial.AddAlpha = true;
                        }
                        if (value.Contains("window.day"))
                        {
                            currentMaterial.UseGlow = true;
                        }
                        break;
                    case "Flags":
                        currentMaterial.Flags = int.Parse(value);
                        break;
                    case "AttributeCount":
                        currentMaterial.AttributeCount = int.Parse(value);
                        break;
                    case "TextureCount":
                        currentMaterial.TextureCount = int.Parse(value);
                        break;
                    default:
                        break;
                }
            }
            if (currentTexture != null)
            {
                switch (key)
                {
                    case "Tag":
                        currentTexture.Tag = value;
                        if (value.EndsWith(":texture_base"))
                        {
                            currentTexture.Type = TextureType.Diffuse;
                        }
                        else if (value.EndsWith("texture_nmap"))
                        {
                            currentTexture.Type = TextureType.Normal;
                        }
                        else
                        {
                            currentTexture.Type = TextureType.Unknown;
                        }
                        //else if (value.EndsWith("specular"))
                        //{
                        //    currentTexture.Type = TextureType.Specular;
                        //}
                        break;
                    case "Value":
                        currentTexture.Value = value;
                        break;
                    default:
                        break;
                }
            }
        }
    }


    public class LookEts
    {
        public string Name { get; set; }
        public List<MaterialEts> Materials { get; set; } = new List<MaterialEts>();
    }

    public class MaterialEts
    {
        public string Alias { get; set; }
        public string Effect { get; set; }
        public int Flags { get; set; }
        public int AttributeCount { get; set; }
        public int TextureCount { get; set; }
        public List<TextureEts> Textures { get; set; } = new List<TextureEts>();
        public bool AddAlpha { get; set; }
        public bool UseGlow { get; set; }
        public bool AddLerp { get; internal set; }
    }

    public class TextureEts
    {
        public string Tag { get; set; }
        public string Value { get; set; }
        public TextureType Type { get; set; }
    }

    public enum TextureType
    {
        Diffuse,
        Normal,
        Unknown
    }

}
