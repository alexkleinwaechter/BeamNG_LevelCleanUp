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
        private List<MaterialJson> _materialsBeam = new List<MaterialJson>();
        private List<MaterialEts> _materialsEts = new List<MaterialEts>();
        private string _textureBasePath;
        internal EtsMaterialConverter(string materialsJson, string pitFileOrFolder)
        {
            _materialsJson = materialsJson;
            _pitFileOrFolder = pitFileOrFolder;
            SetBasePath();
            var fiMaterials = new FileInfo(materialsJson);
            if (!fiMaterials.Exists)
            {
                Console.WriteLine($"Generate new materials json: {materialsJson}");
            }
            else
            {
                ReadMaterials();
            }
            ReadPit();
            WriteMaterialsEts();
            var matDict = new Dictionary<string, MaterialJson>();
            foreach (var mat in _materialsBeam)
            {
                if (!matDict.ContainsKey(mat.Name))
                {
                    matDict.Add(mat.Name, mat);
                }
            }

            var newMatJson = JsonSerializer.Serialize(matDict, BeamJsonOptions.GetJsonSerializerOptions(true));
            if (fiMaterials.Exists)
            {
                fiMaterials.MoveTo($"{fiMaterials.FullName}.{DateTime.Now.Ticks}.bak");
            }

            File.WriteAllText(_materialsJson, newMatJson);
        }

        private void SetBasePath()
        {
            var temp = _materialsJson.Replace("\\", "/").Replace("main.materials.json", "");
            var parts = temp.Split("/levels");
            if (parts.Length > 1)
            {
                _textureBasePath = "/levels" + parts[1];
            }
        }

        internal void Convert()
        {
            ReadMaterials();
        }

        private void ReadPit()
        {
            var pitParser = new PitParser();
            _materialsEts = pitParser.Parse(_pitFileOrFolder);
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
                                _materialsBeam.Add(material);
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

        private void WriteMaterialsEts()
        {
            foreach (var matEts in _materialsEts)
            {
                if (_materialsBeam.Any(x => x.Name == matEts.Alias))
                {
                    continue;
                }

                var beam = new MaterialJson();
                beam.Name = matEts.Alias;
                beam.Class = "Material";
                beam.MapTo = matEts.Alias;
                beam.PersistentId = Guid.NewGuid();
                var stage = new MaterialStage();
                double diffuseColorDefault = 0.800000012;
                stage.DiffuseColor = new List<double> { diffuseColorDefault, diffuseColorDefault, diffuseColorDefault, 1 };
                foreach (var texture in matEts.Textures)
                {
                    var info = new FileInfo(texture.Value);
                    var path = _textureBasePath + info.Name + ".dds";
                    if (texture.Type == TextureType.Diffuse)
                    {
                        stage.DiffuseMap = path;
                    }
                    else if (texture.Type == TextureType.Normal)
                    {
                        stage.NormalMap = path;
                    }
                    if (matEts.UseGlow) stage.Glow = true;
                }
                beam.Stages = new List<MaterialStage> { stage };
                if (matEts.AddLerp)
                {
                    beam.Translucent = true;
                    beam.TranslucentRecvShadows = true;
                }
                if (matEts.AddAlpha)
                {
                    beam.Translucent = true;
                    beam.TranslucentBlendOp = "AddAlpha";
                }

                _materialsBeam.Add(beam);
            }
        }
    }
}
