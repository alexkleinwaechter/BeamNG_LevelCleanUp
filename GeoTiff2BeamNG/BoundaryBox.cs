using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace GeoTiff2BeamNG
{
    public interface IBoundaryBox
    {
        CorePoint LowerLeft { get; }
        CorePoint UpperRight { get; }
    }
    public class BoundaryBox : IBoundaryBox
    {
        public BoundaryBox(CorePoint lowerLeft, CorePoint upperRight, string? format = null) //format should be requried eventually...
        {
            LowerLeft = lowerLeft;
            UpperRight = upperRight;
            Height = upperRight.Latitude - lowerLeft.Latitude;
            Width = upperRight.Longitude - lowerLeft.Longitude;
            Format = format;

            MinimumLongitude = lowerLeft.Longitude;
            MinimumLatitude = lowerLeft.Latitude;
            MaximumLongitude = upperRight.Longitude;
            MaximumLatitude = upperRight.Latitude;

            FileNameString = $"{MinimumLongitude}-{MinimumLatitude}-{MaximumLongitude}-{MaximumLatitude}";
        }

        public CorePoint LowerLeft { get; }

        public CorePoint UpperRight { get; }
        public decimal Height { get; }
        public decimal Width { get; }
        public string? Format { get; }
        public decimal MinimumLongitude { get; }
        public decimal MinimumLatitude { get; }
        public decimal MaximumLongitude { get; }
        public decimal MaximumLatitude { get; }
        public string FileNameString { get; }

        internal bool PointIsWithin(CorePoint point)
        {
            if (point.Longitude < this.MinimumLongitude) return false;
            if (point.Latitude < this.MinimumLatitude) return false;
            if (point.Longitude > this.MaximumLongitude) return false;
            if (point.Latitude > this.MaximumLatitude) return false;
            return true;
        }

        internal BoundaryBox Local()
        {
            return new(new(0, 0), new(Width - 1, Height - 1));
        }


    }
}
