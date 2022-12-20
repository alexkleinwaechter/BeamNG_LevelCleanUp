using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace BeamNG_LevelCleanUp.Objects
{
    public class ManagedDecalData
    {
        public string Name { get; set; }
        public string Class { get; set; }
        public string PersistentId { get; set; }
        public int FadeEndPixelSize { get; set; }
        public int FadeStartPixelSize { get; set; }
        public int Frame { get; set; }
        public string Material { get; set; }
        public bool Randomize { get; set; }
        public int RenderPriority { get; set; }
        public decimal Size { get; set; }
        public int TexCols { get; set; }
        public int TexRows { get; set; }
        public int TextureCoordCount { get; set; }
        public List<List<decimal>> TextureCoords { get; set; }

    }
}
