using BeamNG_LevelCleanUp;
using BeamNG_LevelCleanUp.Objects;
using BeamNG_LevelCleanUp.Utils;
using System.Text.Json;

namespace etsmaterialgen
{
    internal class EtsMaterialConverter
    {
        private readonly string _materialsJson;
        private readonly string _pitFileOrFolder;
        private List<MaterialJson> _materials = new List<MaterialJson>();
        internal EtsMaterialConverter(string materialsJson, string pitFileOrFolder)
        {
            _materialsJson = materialsJson;
            _pitFileOrFolder = pitFileOrFolder;
            var fiMaterials = new FileInfo(materialsJson);
            if (!fiMaterials.Exists)
            {
                Console.WriteLine($"Materials file not found: {materialsJson}");
                return;
            }

            ReadMaterials();
            ReadPit();
            var matDict = new Dictionary<string, MaterialJson>();
            foreach (var mat in _materials)
            {
                if (!matDict.ContainsKey(mat.Name))
                {
                    matDict.Add(mat.Name, mat);
                }
            }

            var newMatJson = JsonSerializer.Serialize(matDict, BeamJsonOptions.GetJsonSerializerOptions(true));
            fiMaterials.MoveTo($"{fiMaterials.FullName}.{DateTime.Now.Ticks}.bak");
            File.WriteAllText(_materialsJson, newMatJson);

        }

        internal void Convert()
        {
            ReadMaterials();
        }

        private void ReadPit()
        {
            using JsonDocument jsonObject = JsonUtils.GetValidJsonDocumentFromFilePath(_pitFileOrFolder);
        }

        private void ReadMaterials()
        {
            try
            {
                using JsonDocument jsonObject = JsonUtils.GetValidJsonDocumentFromFilePath(_materialsJson);
                if (jsonObject.RootElement.ValueKind != JsonValueKind.Undefined)
                {
                    foreach (var child in jsonObject.RootElement.EnumerateObject())
                    {
                        try
                        {
                            var material = child.Value.Deserialize<MaterialJson>(BeamJsonOptions.GetJsonSerializerOptions());
                            if (material != null)
                            {
                                _materials.Add(material);
                            }
                        }
                        catch (Exception ex)
                        {
                            Console.WriteLine(ex);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine(ex);
            }
        }
    }
}
