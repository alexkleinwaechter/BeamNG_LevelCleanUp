using BeamNgTerrainPoc.Terrain.Models;

namespace BeamNgTerrainPoc.Terrain.Services;

/// <summary>
///     CPU hydraulic erosion pass based on droplet sediment transport.
/// </summary>
public sealed class HydraulicErosionProcessor
{
    public float[] Apply(float[] heights, int mapSize, float maxHeight, HydraulicErosionSettings settings)
    {
        if (!settings.Enabled || settings.IterationCount <= 0 || heights.Length == 0)
            return heights.ToArray();

        if (mapSize < 3)
            return heights.ToArray();

        var result = NormalizeHeights(heights, maxHeight, out var scale);
        var random = new Random(settings.RandomSeed);
        var erosionRadius = Math.Clamp(settings.ErosionRadius, 1, Math.Max(1, mapSize / 2 - 1));

        for (var iteration = 0; iteration < settings.IterationCount; iteration++)
            SimulateDroplet(result, mapSize, settings, erosionRadius, random);

        DenormalizeHeights(result, scale, maxHeight);
        return result;
    }

    private static float[] NormalizeHeights(float[] heights, float maxHeight, out float scale)
    {
        scale = maxHeight > 0 ? maxHeight : heights.Where(float.IsFinite).DefaultIfEmpty(1f).Max();
        if (scale <= 0 || !float.IsFinite(scale))
            scale = 1f;

        var normalized = new float[heights.Length];
        for (var i = 0; i < heights.Length; i++)
        {
            var value = float.IsFinite(heights[i]) ? heights[i] : 0f;
            normalized[i] = Math.Clamp(value / scale, 0f, 1f);
        }

        return normalized;
    }

    private static void DenormalizeHeights(float[] heights, float scale, float maxHeight)
    {
        var upperBound = maxHeight > 0 ? maxHeight : scale;
        for (var i = 0; i < heights.Length; i++)
        {
            var value = float.IsFinite(heights[i]) ? heights[i] * scale : 0f;
            heights[i] = Math.Clamp(value, 0f, upperBound);
        }
    }

    private static void SimulateDroplet(
        float[] map,
        int mapSize,
        HydraulicErosionSettings settings,
        int erosionRadius,
        Random random)
    {
        var posX = (float)(random.NextDouble() * (mapSize - 2));
        var posY = (float)(random.NextDouble() * (mapSize - 2));
        var dirX = 0f;
        var dirY = 0f;
        var speed = Math.Max(0f, settings.InitialSpeed);
        var water = Math.Max(0.0001f, settings.InitialWaterVolume);
        var sediment = 0f;

        for (var lifetime = 0; lifetime < settings.MaxDropletLifetime; lifetime++)
        {
            var nodeX = (int)posX;
            var nodeY = (int)posY;
            var dropletIndex = nodeY * mapSize + nodeX;
            var cellOffsetX = posX - nodeX;
            var cellOffsetY = posY - nodeY;

            var heightAndGradient = CalculateHeightAndGradient(map, mapSize, posX, posY);

            var inertia = Math.Clamp(settings.Inertia, 0f, 1f);
            dirX = dirX * inertia - heightAndGradient.GradientX * (1f - inertia);
            dirY = dirY * inertia - heightAndGradient.GradientY * (1f - inertia);

            var length = MathF.Sqrt(dirX * dirX + dirY * dirY);
            if (length == 0f || !float.IsFinite(length))
                break;

            dirX /= length;
            dirY /= length;
            posX += dirX;
            posY += dirY;

            if (posX < 0 || posX >= mapSize - 1 || posY < 0 || posY >= mapSize - 1)
                break;

            var newHeight = CalculateHeightAndGradient(map, mapSize, posX, posY).Height;
            var deltaHeight = newHeight - heightAndGradient.Height;
            var sedimentCapacity = Math.Max(
                -deltaHeight * speed * water * settings.SedimentCapacityFactor,
                settings.MinSedimentCapacity);

            if (sediment > sedimentCapacity || deltaHeight > 0f)
            {
                var amountToDeposit = deltaHeight > 0f
                    ? Math.Min(deltaHeight, sediment)
                    : (sediment - sedimentCapacity) * Math.Clamp(settings.DepositSpeed, 0f, 1f);

                sediment -= amountToDeposit;
                Deposit(map, dropletIndex, mapSize, amountToDeposit, cellOffsetX, cellOffsetY);
            }
            else
            {
                var amountToErode = Math.Min(
                    (sedimentCapacity - sediment) * Math.Clamp(settings.ErodeSpeed, 0f, 1f),
                    -deltaHeight);
                sediment += Erode(map, mapSize, dropletIndex, erosionRadius, amountToErode);
            }

            speed = MathF.Sqrt(Math.Max(0f, speed * speed + deltaHeight * settings.Gravity));
            water *= 1f - Math.Clamp(settings.EvaporateSpeed, 0f, 1f);
            if (water <= 0.0001f)
                break;
        }
    }

    private static void Deposit(float[] map, int dropletIndex, int mapSize, float amount, float offsetX, float offsetY)
    {
        if (amount <= 0f)
            return;

        map[dropletIndex] += amount * (1f - offsetX) * (1f - offsetY);
        map[dropletIndex + 1] += amount * offsetX * (1f - offsetY);
        map[dropletIndex + mapSize] += amount * (1f - offsetX) * offsetY;
        map[dropletIndex + mapSize + 1] += amount * offsetX * offsetY;
    }

    private static float Erode(float[] map, int mapSize, int dropletIndex, int radius, float amountToErode)
    {
        if (amountToErode <= 0f)
            return 0f;

        var eroded = 0f;
        var centerX = dropletIndex % mapSize;
        var centerY = dropletIndex / mapSize;
        var weightSum = 0f;

        for (var y = -radius; y <= radius; y++)
        for (var x = -radius; x <= radius; x++)
        {
            var squaredDistance = x * x + y * y;
            if (squaredDistance >= radius * radius)
                continue;

            var coordX = centerX + x;
            var coordY = centerY + y;
            if (coordX < 0 || coordX >= mapSize || coordY < 0 || coordY >= mapSize)
                continue;

            weightSum += 1f - MathF.Sqrt(squaredDistance) / radius;
        }

        if (weightSum <= 0f)
            return 0f;

        for (var y = -radius; y <= radius; y++)
        for (var x = -radius; x <= radius; x++)
        {
            var squaredDistance = x * x + y * y;
            if (squaredDistance >= radius * radius)
                continue;

            var coordX = centerX + x;
            var coordY = centerY + y;
            if (coordX < 0 || coordX >= mapSize || coordY < 0 || coordY >= mapSize)
                continue;

            var weight = (1f - MathF.Sqrt(squaredDistance) / radius) / weightSum;
            var nodeIndex = coordY * mapSize + coordX;
            var weightedAmount = amountToErode * weight;
            var deltaSediment = Math.Min(map[nodeIndex], weightedAmount);
            map[nodeIndex] -= deltaSediment;
            eroded += deltaSediment;
        }

        return eroded;
    }

    private static HeightAndGradient CalculateHeightAndGradient(float[] nodes, int mapSize, float posX, float posY)
    {
        var coordX = (int)posX;
        var coordY = (int)posY;
        var x = posX - coordX;
        var y = posY - coordY;

        var nodeIndex = coordY * mapSize + coordX;
        var heightNw = nodes[nodeIndex];
        var heightNe = nodes[nodeIndex + 1];
        var heightSw = nodes[nodeIndex + mapSize];
        var heightSe = nodes[nodeIndex + mapSize + 1];

        var gradientX = (heightNe - heightNw) * (1f - y) + (heightSe - heightSw) * y;
        var gradientY = (heightSw - heightNw) * (1f - x) + (heightSe - heightNe) * x;
        var height = heightNw * (1f - x) * (1f - y)
                     + heightNe * x * (1f - y)
                     + heightSw * (1f - x) * y
                     + heightSe * x * y;

        return new HeightAndGradient(height, gradientX, gradientY);
    }
    private readonly record struct HeightAndGradient(float Height, float GradientX, float GradientY);
}