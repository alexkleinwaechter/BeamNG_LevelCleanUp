namespace BeamNgTerrainPoc.Terrain.Processing;

/// <summary>
/// Provides O(N) average-case selection algorithms for finding the k-th smallest element
/// without fully sorting the data. Used as a replacement for the O(N log N) LINQ
/// <c>.OrderBy().ElementAt()</c> pattern for median computation.
/// </summary>
public static class QuickSelect
{
    /// <summary>
    /// Finds the k-th smallest element in the span using the Quickselect algorithm.
    /// This is an in-place algorithm that partially reorders the input span.
    /// Average case O(N), worst case O(N²) but extremely unlikely with median-of-three pivot.
    /// </summary>
    /// <param name="data">The span of data to select from. Will be partially reordered.</param>
    /// <param name="k">The zero-based index of the element to find (e.g., data.Length/2 for median).</param>
    /// <returns>The k-th smallest element.</returns>
    public static float Select(Span<float> data, int k)
    {
        var left = 0;
        var right = data.Length - 1;

        while (left < right)
        {
            var pivotIndex = MedianOfThreePivot(data, left, right);
            pivotIndex = Partition(data, left, right, pivotIndex);

            if (k == pivotIndex)
                return data[k];
            else if (k < pivotIndex)
                right = pivotIndex - 1;
            else
                left = pivotIndex + 1;
        }

        return data[left];
    }

    /// <summary>
    /// Computes the median of the given span using Quickselect.
    /// This is an in-place algorithm that partially reorders the input span.
    /// </summary>
    /// <param name="data">The span of data. Will be partially reordered.</param>
    /// <returns>The median value.</returns>
    public static float Median(Span<float> data)
    {
        if (data.Length == 0)
            return 0f;
        if (data.Length == 1)
            return data[0];

        return Select(data, data.Length / 2);
    }

    /// <summary>
    /// Computes the median of an array, filtering values with a predicate first.
    /// Allocates a filtered copy to avoid modifying the source array.
    /// Returns <paramref name="defaultValue"/> if no elements pass the filter.
    /// </summary>
    /// <param name="source">Source array.</param>
    /// <param name="predicate">Filter predicate.</param>
    /// <param name="defaultValue">Value to return if no elements pass the filter.</param>
    /// <returns>The median of the filtered values, or <paramref name="defaultValue"/>.</returns>
    public static float FilteredMedian(float[] source, Func<float, bool> predicate, float defaultValue = 0f)
    {
        // Count matching elements first to allocate exactly the right size
        var count = 0;
        for (var i = 0; i < source.Length; i++)
        {
            if (predicate(source[i]))
                count++;
        }

        if (count == 0)
            return defaultValue;

        // Copy matching elements into a working buffer
        var buffer = new float[count];
        var idx = 0;
        for (var i = 0; i < source.Length; i++)
        {
            if (predicate(source[i]))
                buffer[idx++] = source[i];
        }

        return Median(buffer.AsSpan());
    }

    /// <summary>
    /// Computes the median of a 2D array, filtering values with a predicate first.
    /// Allocates a filtered copy to avoid modifying the source array.
    /// Returns <paramref name="defaultValue"/> if no elements pass the filter.
    /// </summary>
    /// <param name="source">Source 2D array.</param>
    /// <param name="predicate">Filter predicate.</param>
    /// <param name="defaultValue">Value to return if no elements pass the filter.</param>
    /// <returns>The median of the filtered values, or <paramref name="defaultValue"/>.</returns>
    public static float FilteredMedian(float[,] source, Func<float, bool> predicate, float defaultValue = 0f)
    {
        var height = source.GetLength(0);
        var width = source.GetLength(1);

        // Count matching elements first to allocate exactly the right size
        var count = 0;
        for (var y = 0; y < height; y++)
        for (var x = 0; x < width; x++)
        {
            if (predicate(source[y, x]))
                count++;
        }

        if (count == 0)
            return defaultValue;

        // Copy matching elements into a working buffer
        var buffer = new float[count];
        var idx = 0;
        for (var y = 0; y < height; y++)
        for (var x = 0; x < width; x++)
        {
            if (predicate(source[y, x]))
                buffer[idx++] = source[y, x];
        }

        return Median(buffer.AsSpan());
    }

    private static int MedianOfThreePivot(Span<float> data, int left, int right)
    {
        var mid = left + (right - left) / 2;

        // Sort left, mid, right and return mid as pivot
        if (data[left] > data[mid])
            (data[left], data[mid]) = (data[mid], data[left]);
        if (data[left] > data[right])
            (data[left], data[right]) = (data[right], data[left]);
        if (data[mid] > data[right])
            (data[mid], data[right]) = (data[right], data[mid]);

        return mid;
    }

    private static int Partition(Span<float> data, int left, int right, int pivotIndex)
    {
        var pivotValue = data[pivotIndex];

        // Move pivot to end
        (data[pivotIndex], data[right]) = (data[right], data[pivotIndex]);

        var storeIndex = left;
        for (var i = left; i < right; i++)
        {
            if (data[i] < pivotValue)
            {
                (data[storeIndex], data[i]) = (data[i], data[storeIndex]);
                storeIndex++;
            }
        }

        // Move pivot to its final place
        (data[storeIndex], data[right]) = (data[right], data[storeIndex]);
        return storeIndex;
    }
}
