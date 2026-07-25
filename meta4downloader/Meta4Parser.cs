using System.Xml.Linq;

namespace meta4downloader;

/// <summary>
/// Parses meta4/metalink XML files as well as plain URL lists (line-, comma- or semicolon-separated)
/// </summary>
public class Meta4Parser
{
    public static List<Meta4File> Parse(string inputFilePath)
    {
        if (!File.Exists(inputFilePath))
        {
            throw new FileNotFoundException($"Input file not found: {inputFilePath}");
        }

        var content = File.ReadAllText(inputFilePath);

        return content.TrimStart().StartsWith('<')
            ? ParseMetalink(content)
            : ParseUrlList(content);
    }

    private static List<Meta4File> ParseMetalink(string xmlContent)
    {
        var files = new List<Meta4File>();
        var doc = XDocument.Parse(xmlContent);

        // Match by local name so files with a missing or wrong metalink namespace still parse
        var fileElements = doc.Descendants().Where(e => e.Name.LocalName == "file");

        foreach (var fileElement in fileElements)
        {
            // RFC 5854: multiple <url> elements are mirrors; optional priority attribute, lower = preferred
            var urls = fileElement.Elements()
                .Where(e => e.Name.LocalName == "url")
                .OrderBy(e => int.TryParse(e.Attribute("priority")?.Value, out var p) ? p : 999999)
                .Select(e => e.Value.Trim())
                .Where(IsHttpUrl)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (urls.Count == 0)
            {
                continue;
            }

            var name = fileElement.Attribute("name")?.Value?.Trim() ?? string.Empty;
            if (string.IsNullOrEmpty(name))
            {
                name = FileNameFromUrl(urls[0]);
            }

            long.TryParse(fileElement.Elements()
                .FirstOrDefault(e => e.Name.LocalName == "size")?.Value, out var size);

            var hash = fileElement.Elements()
                .FirstOrDefault(e => e.Name.LocalName == "hash" && e.Attribute("type")?.Value == "sha-256")?
                .Value?.Trim() ?? string.Empty;

            if (!string.IsNullOrEmpty(name))
            {
                files.Add(new Meta4File
                {
                    Name = name,
                    Size = size,
                    Sha256Hash = hash,
                    Urls = urls
                });
            }
        }

        return MakeNamesUnique(files);
    }

    private static List<Meta4File> ParseUrlList(string content)
    {
        var urls = content
            .Split(new[] { '\r', '\n', ',', ';' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(u => u.Trim())
            .Where(u => u.Length > 0 && !u.StartsWith('#'))
            .Where(IsHttpUrl)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var files = urls
            .Select(url => new Meta4File
            {
                Name = FileNameFromUrl(url),
                Urls = new List<string> { url }
            })
            .Where(f => !string.IsNullOrEmpty(f.Name))
            .ToList();

        return MakeNamesUnique(files);
    }

    private static bool IsHttpUrl(string value) =>
        Uri.TryCreate(value, UriKind.Absolute, out var uri) &&
        (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);

    private static string FileNameFromUrl(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            return string.Empty;
        }

        var name = Path.GetFileName(Uri.UnescapeDataString(uri.AbsolutePath));
        foreach (var c in Path.GetInvalidFileNameChars())
        {
            name = name.Replace(c, '_');
        }
        return name;
    }

    /// <summary>
    /// Distinct URLs pointing to identically named files would overwrite each other
    /// in the target directory; suffix duplicates with a counter instead.
    /// </summary>
    private static List<Meta4File> MakeNamesUnique(List<Meta4File> files)
    {
        var seen = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in files)
        {
            if (seen.TryGetValue(file.Name, out var count))
            {
                seen[file.Name] = count + 1;
                var baseName = Path.GetFileNameWithoutExtension(file.Name);
                var extension = Path.GetExtension(file.Name);
                file.Name = $"{baseName}_{count}{extension}";
            }
            else
            {
                seen[file.Name] = 1;
            }
        }
        return files;
    }
}
