using System.Numerics;

namespace BeamNgTerrainPoc.Terrain.Processing;

/// <summary>
/// High-resolution virtual heightfield for sub-pixel precision road smoothing.
/// Provides upsampling (bicubic) and downsampling (Gaussian anti-aliasing) to eliminate blocky artifacts.
/// 
/// WORKFLOW:
/// 1. Upsample original heightmap 2x-8x (bicubic interpolation)
/// 2. Process road smoothing on high-res virtual buffer
/// 3. Downsample back to original resolution (Gaussian blur + decimation)
/// 
/// This eliminates aliasing and creates smooth transitions.
/// </summary>
public class VirtualHeightfield
{
    private readonly float[,] _data;
    private readonly int _width;
    private readonly int _height;
    private readonly int _scaleFactor;
    private readonly float _virtualMetersPerPixel;
    
    /// <summary>
    /// Width of the virtual heightfield
    /// </summary>
    public int Width => _width;
    
    /// <summary>
    /// Height of the virtual heightfield
    /// </summary>
    public int Height => _height;
    
    /// <summary>
    /// Scale factor relative to original heightmap (2, 4, or 8)
    /// </summary>
    public int ScaleFactor => _scaleFactor;
    
    /// <summary>
    /// Meters per pixel in the virtual heightfield (original_meters_per_pixel / scale_factor)
    /// </summary>
    public float MetersPerPixel => _virtualMetersPerPixel;
    
    /// <summary>
    /// Access height values
    /// </summary>
    public float this[int y, int x]
    {
        get => _data[y, x];
        set => _data[y, x] = value;
    }
    
    private VirtualHeightfield(float[,] data, int scaleFactor, float originalMetersPerPixel)
    {
        _data = data;
        _height = data.GetLength(0);
        _width = data.GetLength(1);
        _scaleFactor = scaleFactor;
        _virtualMetersPerPixel = originalMetersPerPixel / scaleFactor;
    }
    
    /// <summary>
    /// Creates a virtual heightfield by upsampling the original heightmap.
    /// Uses bicubic interpolation for smooth, high-quality results.
    /// </summary>
    /// <param name="originalHeightmap">Original heightmap to upsample</param>
    /// <param name="scaleFactor">Upsampling factor (2, 4, or 8). Higher = smoother but slower</param>
    /// <param name="originalMetersPerPixel">Meters per pixel in original heightmap</param>
    /// <returns>High-resolution virtual heightfield</returns>
    public static VirtualHeightfield CreateFromHeightmap(
        float[,] originalHeightmap,
        int scaleFactor,
        float originalMetersPerPixel)
    {
        if (scaleFactor < 1 || scaleFactor > 8)
            throw new ArgumentException("Scale factor must be between 1 and 8", nameof(scaleFactor));
        
        int originalHeight = originalHeightmap.GetLength(0);
        int originalWidth = originalHeightmap.GetLength(1);
        
        int virtualHeight = originalHeight * scaleFactor;
        int virtualWidth = originalWidth * scaleFactor;
        
        Console.WriteLine($"Upsampling heightmap {originalWidth}x{originalHeight} ? {virtualWidth}x{virtualHeight} ({scaleFactor}x)...");
        var startTime = DateTime.Now;
        
        var virtualData = BicubicUpsample(originalHeightmap, virtualWidth, virtualHeight);
        
        var elapsed = DateTime.Now - startTime;
        Console.WriteLine($"  Upsampling complete in {elapsed.TotalSeconds:F1}s");
        
        return new VirtualHeightfield(virtualData, scaleFactor, originalMetersPerPixel);
    }
    
    /// <summary>
    /// Downsamples this virtual heightfield back to original resolution.
    /// Uses Gaussian blur + decimation for proper anti-aliasing.
    /// </summary>
    /// <param name="targetWidth">Target width (original heightmap width)</param>
    /// <param name="targetHeight">Target height (original heightmap height)</param>
    /// <returns>Downsampled heightmap at original resolution</returns>
    public float[,] DownsampleToHeightmap(int targetWidth, int targetHeight)
    {
        Console.WriteLine($"Downsampling virtual heightfield {_width}x{_height} ? {targetWidth}x{targetHeight} ({_scaleFactor}x)...");
        var startTime = DateTime.Now;
        
        // Step 1: Apply Gaussian blur before decimation (anti-aliasing)
        // Kernel size based on scale factor (larger scale = larger blur)
        int kernelSize = Math.Max(3, _scaleFactor * 2 - 1);
        Console.WriteLine($"  Applying Gaussian blur (kernel size: {kernelSize})...");
        var blurred = ApplyGaussianBlur(_data, kernelSize);
        
        // Step 2: Decimate (sample every Nth pixel)
        Console.WriteLine($"  Decimating by factor {_scaleFactor}...");
        var downsampled = new float[targetHeight, targetWidth];
        
        for (int y = 0; y < targetHeight; y++)
        {
            for (int x = 0; x < targetWidth; x++)
            {
                // Sample at center of each original pixel's upsampled region
                int virtualX = x * _scaleFactor + _scaleFactor / 2;
                int virtualY = y * _scaleFactor + _scaleFactor / 2;
                
                virtualX = Math.Clamp(virtualX, 0, _width - 1);
                virtualY = Math.Clamp(virtualY, 0, _height - 1);
                
                downsampled[y, x] = blurred[virtualY, virtualX];
            }
        }
        
        var elapsed = DateTime.Now - startTime;
        Console.WriteLine($"  Downsampling complete in {elapsed.TotalSeconds:F1}s");
        
        return downsampled;
    }
    
    /// <summary>
    /// Bicubic interpolation upsampling for smooth, high-quality results.
    /// Uses cubic convolution for C¹ continuity (smooth gradients).
    /// </summary>
    private static float[,] BicubicUpsample(float[,] source, int targetWidth, int targetHeight)
    {
        int sourceHeight = source.GetLength(0);
        int sourceWidth = source.GetLength(1);
        
        var result = new float[targetHeight, targetWidth];
        
        float scaleX = sourceWidth / (float)targetWidth;
        float scaleY = sourceHeight / (float)targetHeight;
        
        for (int y = 0; y < targetHeight; y++)
        {
            if (y % 500 == 0 && y > 0)
            {
                float progress = (y / (float)targetHeight) * 100f;
                Console.WriteLine($"    Upsampling progress: {progress:F1}%");
            }
            
            for (int x = 0; x < targetWidth; x++)
            {
                // Map to source coordinates
                float srcX = (x + 0.5f) * scaleX - 0.5f;
                float srcY = (y + 0.5f) * scaleY - 0.5f;
                
                result[y, x] = SampleBicubic(source, srcX, srcY, sourceWidth, sourceHeight);
            }
        }
        
        return result;
    }
    
    /// <summary>
    /// Samples a single point using bicubic interpolation.
    /// Uses cubic convolution kernel (Keys' cubic with a=-0.5 for smoothness).
    /// </summary>
    private static float SampleBicubic(float[,] source, float x, float y, int width, int height)
    {
        // Get integer coordinates of the 4x4 sample region
        int x0 = (int)MathF.Floor(x);
        int y0 = (int)MathF.Floor(y);
        
        // Fractional parts
        float fx = x - x0;
        float fy = y - y0;
        
        float result = 0;
        
        // Sample 4x4 neighborhood
        for (int dy = -1; dy <= 2; dy++)
        {
            for (int dx = -1; dx <= 2; dx++)
            {
                int sx = x0 + dx;
                int sy = y0 + dy;
                
                // Clamp to image bounds
                sx = Math.Clamp(sx, 0, width - 1);
                sy = Math.Clamp(sy, 0, height - 1);
                
                float sample = source[sy, sx];
                
                // Cubic convolution weights
                float wx = CubicWeight(dx - fx);
                float wy = CubicWeight(dy - fy);
                
                result += sample * wx * wy;
            }
        }
        
        return result;
    }
    
    /// <summary>
    /// Cubic convolution weight function (Keys' cubic with a=-0.5).
    /// Provides smooth interpolation with C¹ continuity.
    /// </summary>
    private static float CubicWeight(float t)
    {
        t = MathF.Abs(t);
        
        if (t < 1.0f)
        {
            // (a+2)|t|³ - (a+3)|t|² + 1
            return 1.5f * t * t * t - 2.5f * t * t + 1.0f;
        }
        else if (t < 2.0f)
        {
            // a|t|³ - 5a|t|² + 8a|t| - 4a
            return -0.5f * t * t * t + 2.5f * t * t - 4.0f * t + 2.0f;
        }
        else
        {
            return 0.0f;
        }
    }
    
    /// <summary>
    /// Applies Gaussian blur for anti-aliasing before downsampling.
    /// Uses separable filter for efficiency (O(n*k) instead of O(n*k²)).
    /// </summary>
    private static float[,] ApplyGaussianBlur(float[,] source, int kernelSize)
    {
        int height = source.GetLength(0);
        int width = source.GetLength(1);
        
        // Generate 1D Gaussian kernel
        var kernel = GenerateGaussianKernel(kernelSize);
        
        // Horizontal pass
        var temp = new float[height, width];
        int halfKernel = kernelSize / 2;
        
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                float sum = 0;
                float weightSum = 0;
                
                for (int k = 0; k < kernelSize; k++)
                {
                    int sx = x + k - halfKernel;
                    sx = Math.Clamp(sx, 0, width - 1);
                    
                    sum += source[y, sx] * kernel[k];
                    weightSum += kernel[k];
                }
                
                temp[y, x] = sum / weightSum;
            }
        }
        
        // Vertical pass
        var result = new float[height, width];
        
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                float sum = 0;
                float weightSum = 0;
                
                for (int k = 0; k < kernelSize; k++)
                {
                    int sy = y + k - halfKernel;
                    sy = Math.Clamp(sy, 0, height - 1);
                    
                    sum += temp[sy, x] * kernel[k];
                    weightSum += kernel[k];
                }
                
                result[y, x] = sum / weightSum;
            }
        }
        
        return result;
    }
    
    /// <summary>
    /// Generates a 1D Gaussian kernel for blur operations.
    /// </summary>
    private static float[] GenerateGaussianKernel(int size)
    {
        var kernel = new float[size];
        int halfSize = size / 2;
        float sigma = halfSize / 3.0f; // Standard deviation
        float twoSigmaSquared = 2 * sigma * sigma;
        
        float sum = 0;
        
        for (int i = 0; i < size; i++)
        {
            int x = i - halfSize;
            kernel[i] = MathF.Exp(-(x * x) / twoSigmaSquared);
            sum += kernel[i];
        }
        
        // Normalize
        for (int i = 0; i < size; i++)
        {
            kernel[i] /= sum;
        }
        
        return kernel;
    }
    
    /// <summary>
    /// Gets the heightfield data (direct access for algorithms).
    /// </summary>
    public float[,] GetData() => _data;
}
