using System.Text;
using BeamNgTerrainPoc.Terrain.Logging;
using OSGeo.OSR;

namespace BeamNgTerrainPoc.Terrain.GeoTiff;

/// <summary>
///     Attempts to auto-detect the EPSG code for elevation files whose embedded CRS is unusable
///     (e.g. an engineering/local CRS carrying only a citation name like "Lambert 93").
///     Detection combines two signals:
///     1. Name matching - the citation name in the broken CRS often names the real system.
///     2. Native coordinate ranges - national grids have characteristic easting/northing windows.
///     Every candidate is verified by actually transforming the native bounding box to WGS84 and
///     checking the result lands inside the system's geographic area. Ambiguous range matches
///     (more than one verified candidate without a name hint) return null - better no guess than
///     a wrong one.
/// </summary>
public static class EpsgAutoDetector
{
    /// <summary>
    ///     Result of a successful detection.
    /// </summary>
    /// <param name="EpsgCode">The detected EPSG code.</param>
    /// <param name="Description">Human-readable CRS name (e.g. "RGF93 / Lambert-93 (France)").</param>
    /// <param name="Reason">Why this code was chosen - shown to the user for verification.</param>
    public record DetectionResult(int EpsgCode, string Description, string Reason);

    private record KnownCrs(
        int Epsg,
        string Description,
        string[] NamePatterns,
        double MinX, double MaxX,
        double MinY, double MaxY,
        double MinLon, double MaxLon,
        double MinLat, double MaxLat);

    private static readonly KnownCrs[] KnownSystems =
    [
        new(2154, "RGF93 / Lambert-93 (France)",
            ["lambert93", "rgf93"],
            0, 1_300_000, 6_000_000, 7_200_000,
            -6.0, 10.5, 40.5, 52.0),
        new(27572, "NTF (Paris) / Lambert zone II etendu (France)",
            ["lambert2etendu", "lambertiietendu", "lambertzone2", "lambertzoneii"],
            0, 1_300_000, 1_600_000, 2_800_000,
            -6.0, 10.5, 40.5, 52.0),
        new(25832, "ETRS89 / UTM zone 32N",
            ["etrs89utmzone32", "etrs89utm32"],
            200_000, 900_000, 5_000_000, 6_200_000,
            5.0, 14.0, 45.0, 56.5),
        new(25833, "ETRS89 / UTM zone 33N",
            ["etrs89utmzone33", "etrs89utm33"],
            200_000, 900_000, 5_000_000, 6_200_000,
            11.0, 20.0, 45.0, 56.5),
        new(31466, "DHDN / Gauss-Krueger zone 2 (Germany west)",
            ["gausskrugerzone2", "gk2", "dhdn2"],
            2_400_000, 2_700_000, 5_200_000, 6_100_000,
            5.0, 9.0, 47.0, 56.0),
        new(31467, "DHDN / Gauss-Krueger zone 3 (Germany)",
            ["gausskrugerzone3", "gk3", "dhdn3"],
            3_300_000, 3_700_000, 5_200_000, 6_100_000,
            7.5, 12.0, 47.0, 56.0),
        new(31468, "DHDN / Gauss-Krueger zone 4 (Germany east)",
            ["gausskrugerzone4", "gk4", "dhdn4"],
            4_300_000, 4_700_000, 5_200_000, 6_100_000,
            10.5, 15.0, 47.0, 56.0),
        new(31469, "DHDN / Gauss-Krueger zone 5 (Germany far east)",
            ["gausskrugerzone5", "gk5", "dhdn5"],
            5_300_000, 5_700_000, 5_200_000, 6_100_000,
            13.5, 18.0, 47.0, 56.0),
        new(2056, "CH1903+ / LV95 (Switzerland)",
            ["ch1903lv95", "lv95", "swissgrid"],
            2_450_000, 2_850_000, 1_050_000, 1_350_000,
            5.9, 10.6, 45.7, 48.0),
        new(21781, "CH1903 / LV03 (Switzerland)",
            ["ch1903lv03", "lv03"],
            480_000, 850_000, 70_000, 300_000,
            5.9, 10.6, 45.7, 48.0),
        new(27700, "OSGB36 / British National Grid",
            ["britishnationalgrid", "osgb"],
            0, 700_000, 0, 1_300_000,
            -8.7, 1.8, 49.8, 61.0),
        new(28992, "Amersfoort / RD New (Netherlands)",
            ["amersfoort", "rdnew"],
            -7_000, 300_000, 289_000, 629_000,
            3.2, 7.3, 50.7, 53.7),
        new(31370, "BD72 / Belgian Lambert 72",
            ["lambert72", "lambertbelge", "belgianlambert"],
            0, 300_000, 0, 300_000,
            2.5, 6.4, 49.5, 51.5)
    ];

    /// <summary>
    ///     Tries to detect the real EPSG code for an unusable CRS.
    /// </summary>
    /// <param name="projectionWkt">The (broken) projection WKT from the file - used for its citation name.</param>
    /// <param name="nativeBounds">The file's bounding box in native coordinates.</param>
    /// <returns>The detected code with an explanation, or null if no unambiguous match was found.</returns>
    public static DetectionResult? Detect(string? projectionWkt, GeoBoundingBox nativeBounds)
    {
        var crsName = ExtractCrsName(projectionWkt);
        var normalized = Normalize(crsName);

        // 1) Name matching - the strongest signal
        if (normalized.Length > 0)
            foreach (var sys in KnownSystems)
                if (sys.NamePatterns.Any(p => normalized.Contains(p)))
                {
                    if (VerifyByTransform(sys, nativeBounds))
                    {
                        var reason = $"CRS name '{crsName}' matches {sys.Description}";
                        TerrainCreationLogger.InfoFileOnlyOrQueue(
                            $"EPSG auto-detection: {reason} (EPSG:{sys.Epsg}), verified by transform");
                        return new DetectionResult(sys.Epsg, sys.Description, reason);
                    }

                    TerrainCreationLogger.InfoFileOnlyOrQueue(
                        $"EPSG auto-detection: CRS name '{crsName}' suggested EPSG:{sys.Epsg} " +
                        $"({sys.Description}) but the coordinates do not fit - rejected");
                }

        // 2) Coordinate-range matching - only trusted when exactly one candidate survives
        var candidates = KnownSystems
            .Where(sys => InNativeRange(sys, nativeBounds) && VerifyByTransform(sys, nativeBounds))
            .ToList();

        if (candidates.Count == 1)
        {
            var sys = candidates[0];
            var reason = $"native coordinate range matches {sys.Description}";
            TerrainCreationLogger.InfoFileOnlyOrQueue(
                $"EPSG auto-detection: {reason} (EPSG:{sys.Epsg}), verified by transform");
            return new DetectionResult(sys.Epsg, sys.Description, reason);
        }

        if (candidates.Count > 1)
            TerrainCreationLogger.InfoFileOnlyOrQueue(
                "EPSG auto-detection ambiguous - multiple systems fit the coordinate range: " +
                string.Join(", ", candidates.Select(c => $"EPSG:{c.Epsg} ({c.Description})")));
        else
            TerrainCreationLogger.InfoFileOnlyOrQueue(
                "EPSG auto-detection: no known system matched the CRS name or coordinate range");

        return null;
    }

    private static bool InNativeRange(KnownCrs sys, GeoBoundingBox bounds)
    {
        // GeoBoundingBox stores native X in the longitude fields and native Y in the latitude fields
        return bounds.MinLongitude >= sys.MinX && bounds.MaxLongitude <= sys.MaxX &&
               bounds.MinLatitude >= sys.MinY && bounds.MaxLatitude <= sys.MaxY;
    }

    /// <summary>
    ///     Verifies a candidate by transforming the native bbox center to WGS84 without any
    ///     UI logging (unlike GeoBoundingBox.TransformToWgs84, which reports failures to the user).
    /// </summary>
    private static bool VerifyByTransform(KnownCrs sys, GeoBoundingBox bounds)
    {
        try
        {
            GeoTiffReader.InitializeGdal();

            var sourceSrs = new SpatialReference(null);
            if (sourceSrs.ImportFromEPSG(sys.Epsg) != 0)
                return false;
            sourceSrs.SetAxisMappingStrategy(AxisMappingStrategy.OAMS_TRADITIONAL_GIS_ORDER);

            var targetSrs = new SpatialReference(null);
            targetSrs.ImportFromEPSG(4326);
            targetSrs.SetAxisMappingStrategy(AxisMappingStrategy.OAMS_TRADITIONAL_GIS_ORDER);

            using var transform = new CoordinateTransformation(sourceSrs, targetSrs);

            double[] center = [bounds.Center.Longitude, bounds.Center.Latitude, 0];
            transform.TransformPoint(center);

            var lon = center[0];
            var lat = center[1];

            return lon >= sys.MinLon && lon <= sys.MaxLon &&
                   lat >= sys.MinLat && lat <= sys.MaxLat;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    ///     Extracts the CRS name from a WKT string (works for engineering/local CRS too).
    /// </summary>
    private static string ExtractCrsName(string? projectionWkt)
    {
        if (string.IsNullOrEmpty(projectionWkt))
            return string.Empty;

        try
        {
            GeoTiffReader.InitializeGdal();
            var srs = new SpatialReference(null);
            var wktCopy = projectionWkt;
            if (srs.ImportFromWkt(ref wktCopy) == 0)
            {
                var name = srs.GetName();
                if (!string.IsNullOrEmpty(name))
                    return name;
            }
        }
        catch
        {
            // Fall through to regex extraction
        }

        // Fallback: first quoted string in the WKT, e.g. LOCAL_CS["Lambert 93",...]
        var match = System.Text.RegularExpressions.Regex.Match(projectionWkt, "\"([^\"]+)\"");
        return match.Success ? match.Groups[1].Value : string.Empty;
    }

    /// <summary>
    ///     Normalizes a CRS name for pattern matching: lowercase, diacritics folded,
    ///     everything except letters and digits removed ("Lambert 93" becomes "lambert93").
    /// </summary>
    private static string Normalize(string name)
    {
        if (string.IsNullOrEmpty(name))
            return string.Empty;

        var folded = name
            .Replace('é', 'e').Replace('è', 'e').Replace('ê', 'e')
            .Replace('ü', 'u').Replace('ö', 'o').Replace('ä', 'a')
            .Replace("ß", "ss");

        var sb = new StringBuilder(folded.Length);
        foreach (var c in folded)
            if (char.IsLetterOrDigit(c))
                sb.Append(char.ToLowerInvariant(c));

        return sb.ToString();
    }
}
