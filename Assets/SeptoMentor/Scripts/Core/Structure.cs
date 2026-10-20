using UnityEngine;

namespace SeptoMentor
{
    /// <summary>
    /// Segmentation classes. The integer value IS the model's channel index
    /// (logits [1,16,256,256]) and the value written by the synthetic label pass.
    /// Never reorder without retraining / re-exporting the model.
    /// </summary>
    public enum Structure : byte
    {
        Background = 0,
        SeptalCartilage = 1,
        PerpendicularPlate = 2,   // perpendicular plate of ethmoid
        Vomer = 3,
        MaxillaryCrest = 4,
        MucoperichondrialFlap = 5,
        MucosalTear = 6,
        InferiorTurbinate = 7,
        MiddleTurbinate = 8,
        CaudalSeptum = 9,
        NasalFloor = 10,
        Scalpel = 11,
        FreerElevator = 12,
        ScissorsForceps = 13,
        Suction = 14,
        SinonasalBone = 15,       // PLACEHOLDER class, not a defined anatomical target
    }

    public static class StructureInfo
    {
        public const int Count = 16;

        static readonly string[] Names =
        {
            "Background", "Septal cartilage", "Perpendicular plate (ethmoid)", "Vomer",
            "Maxillary crest", "Mucoperichondrial flap", "Mucosal tear", "Inferior turbinate",
            "Middle turbinate", "Caudal septum", "Nasal floor", "Scalpel", "Freer elevator",
            "Scissors / forceps", "Suction", "Sinonasal bone (placeholder)",
        };

        // Distinct, colour-blind-friendlier palette (overlay only, not tissue colour).
        static readonly Color32[] Colors =
        {
            new Color32(0, 0, 0, 0),
            new Color32(80, 170, 255, 255),   // cartilage - blue
            new Color32(255, 170, 0, 255),    // PPE - amber
            new Color32(200, 120, 255, 255),  // vomer - violet
            new Color32(255, 230, 90, 255),   // maxillary crest - yellow
            new Color32(255, 120, 170, 255),  // flap - pink
            new Color32(255, 30, 30, 255),    // tear - red
            new Color32(60, 220, 120, 255),   // inferior turbinate - green
            new Color32(0, 200, 200, 255),    // middle turbinate - teal
            new Color32(120, 120, 255, 255),  // caudal septum - indigo
            new Color32(180, 140, 100, 255),  // nasal floor - tan
            new Color32(230, 230, 230, 255),  // scalpel
            new Color32(200, 200, 160, 255),  // freer
            new Color32(170, 200, 230, 255),  // scissors / forceps
            new Color32(150, 150, 150, 255),  // suction
            new Color32(128, 128, 128, 255),  // placeholder bone
        };

        public static string DisplayName(Structure s) => Names[(int)s];
        public static Color32 OverlayColor(Structure s) => Colors[(int)s];

        public static bool IsInstrument(Structure s) =>
            s == Structure.Scalpel || s == Structure.FreerElevator ||
            s == Structure.ScissorsForceps || s == Structure.Suction;

        public static bool IsCuttingInstrument(Structure s) =>
            s == Structure.Scalpel || s == Structure.ScissorsForceps;

        /// <summary>Classes that are placeholders by definition, regardless of data source.</summary>
        public static bool IsPlaceholderClass(Structure s) => s == Structure.SinonasalBone;
    }
}
