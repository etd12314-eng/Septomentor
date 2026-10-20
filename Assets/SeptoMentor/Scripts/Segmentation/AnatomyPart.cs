using UnityEngine;

namespace SeptoMentor
{
    /// <summary>
    /// Tags a renderer of the virtual patient (or a virtual instrument) with its class.
    /// The synthetic provider renders this value into the label map.
    /// </summary>
    [RequireComponent(typeof(Renderer))]
    public class AnatomyPart : MonoBehaviour
    {
        public Structure structure = Structure.Background;

        [Tooltip("Endoscope tissue tint (not the overlay colour).")]
        public Color tissueColor = new Color(0.85f, 0.45f, 0.45f);

        [Range(0, 2)] public float wetness = 0.6f;

        [Tooltip("False until an expert has confirmed what this mesh segment is.")]
        public bool mappingConfirmed;

        public Renderer Rend { get; private set; }

        static readonly int ClassIdId = Shader.PropertyToID("_ClassId");
        static readonly int TissueColorId = Shader.PropertyToID("_TissueColor");
        static readonly int WetnessId = Shader.PropertyToID("_Wetness");

        void Awake() => Apply();

        public void Apply()
        {
            Rend = GetComponent<Renderer>();
            var mpb = new MaterialPropertyBlock();
            Rend.GetPropertyBlock(mpb);
            mpb.SetFloat(ClassIdId, (int)structure);
            mpb.SetColor(TissueColorId, tissueColor);
            mpb.SetFloat(WetnessId, wetness);
            Rend.SetPropertyBlock(mpb);
        }

        /// <summary>Plausible endoscopic tint per class (cosmetic only).</summary>
        public static Color DefaultTint(Structure s)
        {
            switch (s)
            {
                case Structure.SeptalCartilage: return new Color(0.82f, 0.80f, 0.86f);      // bluish-white
                case Structure.CaudalSeptum: return new Color(0.85f, 0.72f, 0.74f);
                case Structure.PerpendicularPlate:
                case Structure.Vomer:
                case Structure.MaxillaryCrest:
                case Structure.SinonasalBone: return new Color(0.88f, 0.84f, 0.70f);        // bone
                case Structure.MucoperichondrialFlap: return new Color(0.90f, 0.42f, 0.45f);
                case Structure.MucosalTear: return new Color(0.55f, 0.05f, 0.06f);
                case Structure.InferiorTurbinate:
                case Structure.MiddleTurbinate:
                case Structure.NasalFloor: return new Color(0.88f, 0.48f, 0.50f);           // mucosa
                case Structure.Scalpel:
                case Structure.FreerElevator:
                case Structure.ScissorsForceps:
                case Structure.Suction: return new Color(0.75f, 0.77f, 0.80f);              // steel
                default: return new Color(0.8f, 0.5f, 0.5f);
            }
        }
    }
}
