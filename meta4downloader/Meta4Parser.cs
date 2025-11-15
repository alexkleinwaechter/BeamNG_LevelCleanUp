using System.Xml.Linq;

namespace meta4downloader;

/// <summary>
/// Parses meta4 metalink XML files
/// </summary>
public class Meta4Parser
{
    public static List<Meta4File> Parse(string meta4FilePath)
    {
        if (!File.Exists(meta4FilePath))
        {
  throw new FileNotFoundException($"Meta4 file not found: {meta4FilePath}");
  }

        var files = new List<Meta4File>();
        var doc = XDocument.Load(meta4FilePath);
        XNamespace ns = "urn:ietf:params:xml:ns:metalink";

   var fileElements = doc.Descendants(ns + "file");

     foreach (var fileElement in fileElements)
        {
  var meta4File = new Meta4File
         {
                Name = fileElement.Attribute("name")?.Value ?? string.Empty,
    Size = long.Parse(fileElement.Element(ns + "size")?.Value ?? "0"),
            Sha256Hash = fileElement.Elements(ns + "hash")
     .FirstOrDefault(h => h.Attribute("type")?.Value == "sha-256")?.Value ?? string.Empty,
         Url = fileElement.Element(ns + "url")?.Value ?? string.Empty
            };

            if (!string.IsNullOrEmpty(meta4File.Name) && !string.IsNullOrEmpty(meta4File.Url))
   {
     files.Add(meta4File);
       }
        }

        return files;
    }
}
