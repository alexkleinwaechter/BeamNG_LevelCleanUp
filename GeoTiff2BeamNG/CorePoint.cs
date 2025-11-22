using GeoTiff2BeamNG;
using System.Drawing;

namespace GeoTiff2BeamNG
{
    public interface ICorePoint
    {
        public decimal Longitude { get; }
        public decimal Latitude { get; }
        public decimal Altitude { get; }
    }
    public class CorePoint : ICorePoint
    {
        public CorePoint(decimal longitude, decimal latitude, decimal? altitude = null)
        {
            Longitude = longitude;
            Latitude = latitude;
            if (altitude != null) Altitude = (decimal)altitude;
            PointFTopView = new((float)longitude, (float)latitude);
        }

        public decimal Longitude { get; }
        public decimal Latitude { get; }
        public decimal Altitude { get; }
        public PointF PointFTopView { get; internal set; }

        internal CorePoint GridPoint()
        {
            return new(Math.Floor(Longitude), Math.Floor(Latitude), Altitude);
        }

        public List<CorePoint> Neighbours(int radius, int maxSize) //This assumes perfectly square input data //might not belong here??
        {

            var neighborList = new List<CorePoint>();

            var column = -1 * radius;
            var row = -1 * radius;

            while (row <= radius)
            {
                neighborList.Add(new CorePoint(Longitude + column, Latitude + row));

                if (column == radius)
                {
                    column = -1 * radius;
                    row++;
                }
                column++;
            }

            var removeList = new List<CorePoint>();

            foreach (var neighbor in neighborList)
            {
                var remove = false;
                if (neighbor.Longitude < 0) remove = true;
                if (neighbor.Latitude < 0) remove = true;
                if (neighbor.Longitude > maxSize - 1) remove = true;
                if (neighbor.Latitude > maxSize - 1) remove = true;
                if (neighbor == this) remove = true;
                if (!remove) continue;
                removeList.Add(neighbor);
            }

            foreach (var remove in removeList)
            {
                neighborList.Remove(remove);
            }
            return neighborList;
        }
    }
    public class OldUTMPoint : ICorePoint
    {
        public OldUTMPoint(decimal longitude, decimal latitude, decimal altitude)
        {
            Longitude = longitude;
            Latitude = latitude;
            Altitude = altitude;
            PointFTopView = new((float)longitude, (float)latitude);
        }

        public decimal Longitude { get; }
        public decimal Latitude { get; }
        public decimal Altitude { get; }
        public PointF PointFTopView { get; internal set; }

        public List<CorePoint> Neighbours(int radius, int maxSize)
        {
            throw new NotImplementedException();
        }

        internal CorePoint CorePoint(BoundaryBox boundaryBox)
        {
            var coreLongitude = Longitude - boundaryBox.MinimumLongitude;
            var coreLatitude = Latitude - boundaryBox.MinimumLatitude;
            return new CorePoint(coreLongitude, coreLatitude, this.Altitude);
        }
    }
}