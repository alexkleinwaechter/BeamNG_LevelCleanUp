namespace GeoTiff2BeamNG
{
    internal class LoggeM
    {
        internal static void WriteLine(string v)
        {
            Console.WriteLine($"[{DateTime.Now.ToString("HH:mm:ss")}] {v}");
        }
    }
}