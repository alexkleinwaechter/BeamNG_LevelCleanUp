using BeamNG_LevelCleanUp.Communication;
using BeamNG_LevelCleanUp.Objects;
using System;
using System.Collections;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices.JavaScript;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Xml.Linq;
using static System.Net.Mime.MediaTypeNames;

namespace BeamNG_LevelCleanUp.LogicCopyAssets
{
    public class DecalCopyScanner
    {
        private FileInfo _managedItemFile { get; set; }
        private List<MaterialJson> _materialsJsonCopy { get; set; }
        private List<CopyAsset> _copyAssets { get; set; }
        public DecalCopyScanner(FileInfo managedItemFile, List<MaterialJson> materialsJsonCopy, List<CopyAsset> copyAssets)
        {
            _managedItemFile = managedItemFile;
            _materialsJsonCopy = materialsJsonCopy;
            _copyAssets = copyAssets;
        }

        public void ScanManagedItems()
        {
            var managedDecalData = new List<ManagedDecalData>();
            if (_managedItemFile.Extension.Equals(".json", StringComparison.OrdinalIgnoreCase))
            {
                managedDecalData = HandleJson(_managedItemFile);
            }
            else
            {
                managedDecalData = HandleCs(_managedItemFile);
            }
        }

        private List<ManagedDecalData> HandleJson(FileInfo file)
        {
            var retVal = new List<ManagedDecalData>();
            JsonDocumentOptions docOptions = new JsonDocumentOptions { AllowTrailingCommas = true };
            try
            {
                using JsonDocument jsonObject = JsonDocument.Parse(File.ReadAllText(file.FullName), docOptions);
                if (jsonObject.RootElement.ValueKind != JsonValueKind.Undefined)
                {
                    foreach (var managedDecalData in jsonObject.RootElement.EnumerateObject())
                    {
                        try
                        {
                            var decalData = managedDecalData.Value.Deserialize<ManagedDecalData>(BeamJsonOptions.Get());
                            retVal.Add(decalData);
                        }
                        catch (Exception ex)
                        {
                            throw;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                PubSubChannel.SendMessage(true, $"Error DecalScanner {file.FullName}. {ex.Message}");
            }
            return retVal;
        }

        private List<ManagedDecalData> HandleCs(FileInfo file)
        {
            var retVal = new List<ManagedDecalData>();
            var item = new ManagedDecalData();
            foreach (string line in File.ReadAllLines(file.FullName))
            {
                int startPoint = line.IndexOf("DecalData(") > -1
                    ? line.IndexOf("DecalData(") + "DecalData(".Length
                    : -1;
                int endPoint = line.LastIndexOf(")");
                if (startPoint > -1 && endPoint > -1)
                {
                    if (!string.IsNullOrEmpty(item.Name))
                    {
                        retVal.Add(item);
                        item = new ManagedDecalData();
                    }
                    var name = line.Substring(startPoint, endPoint - startPoint);
                    if (!string.IsNullOrEmpty(name))
                    {
                        item.Name = name;
                    }
                }
                else
                {
                    var propValPair = line.Replace(";", "").Split("=", StringSplitOptions.TrimEntries);
                    if (propValPair.Count() == 2)
                    {
                        var isArray = Regex.IsMatch(propValPair.First(), WildCardToRegular("*[*]"));
                        propValPair[0] = propValPair.First().Split("[", StringSplitOptions.TrimEntries).First();
                        propValPair[1] = propValPair.Last().Replace("\"", "");
                        foreach (var prop in item.GetType().GetProperties())
                        {
                            if (propValPair.First().Equals(prop.Name, StringComparison.OrdinalIgnoreCase)
                                && isArray)
                            {
                                var arrayString = "{\"array\":[" + propValPair.Last().Replace(" ", ",") + "]}";
                                var valArray = JsonNode.Parse(arrayString);
                                object instance = Activator.CreateInstance(prop.PropertyType);
                                // List<T> implements the non-generic IList interface
                                IList list = (IList)instance;
                                if (prop.GetValue(item) != null)
                                {
                                    list = (IList)prop.GetValue(item);
                                }
                                List<decimal> decimals = new List<decimal>();
                                foreach (var value in (JsonArray)valArray["array"])
                                {
                                    decimals.Add((decimal)value);
                                }
                                list.Add(decimals);
                                prop.SetValue(item, list, null);
                                break;
                            }

                            else if (propValPair.First().Equals(prop.Name, StringComparison.OrdinalIgnoreCase))
                            {
                                object? convertedObject = GenericConverter(propValPair.Last(), prop);
                                prop.SetValue(item, convertedObject);
                                break;
                            }
                        }
                    }
                }
            }
            if (!string.IsNullOrEmpty(item.Name))
            {
                retVal.Add(item);
            }
            return retVal;
        }

        private static object? GenericConverter(string sourceValue, PropertyInfo prop, bool isList = false)
        {
            var converter = new TypeConverter();
            if (!isList)
            {
                converter = TypeDescriptor.GetConverter(prop.PropertyType);
            }
            else
            {
                converter = TypeDescriptor.GetConverter(prop.PropertyType.GetGenericArguments().Single());
            }

            if (prop.PropertyType.Equals(typeof(bool)))
            {
                sourceValue = sourceValue.Replace("0", "false").Replace("1", "true");
            }

            var convertedObject = converter.ConvertFromInvariantString(sourceValue);
            return convertedObject;
        }

        private static String WildCardToRegular(String value)
        {
            return "^" + Regex.Escape(value).Replace("\\?", ".").Replace("\\*", ".*") + "$";
        }
    }
}
