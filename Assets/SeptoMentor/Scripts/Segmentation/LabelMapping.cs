using System;
using System.Collections.Generic;
using UnityEngine;

namespace SeptoMentor
{
    /// <summary>
    /// Maps sub-objects of the imported 3D Slicer OBJ (by name substring) to classes.
    /// OPEN ITEM 1: the default rules below are GUESSES. Slicer exports one OBJ group per
    /// segment (e.g. "Segment_1", "Segment_2"); confirm what each one is, then edit the
    /// rules (inspector or Resources/label_mapping.json) and set confirmed = true.
    /// </summary>
    [Serializable]
    public class LabelMapping
    {
        [Serializable]
        public class Rule
        {
            public string nameContains;
            public Structure structure;
            public bool confirmed;
        }

        public List<Rule> rules = new List<Rule>
        {
            new Rule { nameContains = "cartilage", structure = Structure.SeptalCartilage },
            new Rule { nameContains = "perpendicular", structure = Structure.PerpendicularPlate },
            new Rule { nameContains = "ethmoid", structure = Structure.PerpendicularPlate },
            new Rule { nameContains = "vomer", structure = Structure.Vomer },
            new Rule { nameContains = "crest", structure = Structure.MaxillaryCrest },
            new Rule { nameContains = "turbinate", structure = Structure.InferiorTurbinate },
            new Rule { nameContains = "floor", structure = Structure.NasalFloor },
            // UNCONFIRMED placeholders for unnamed Slicer segments:
            new Rule { nameContains = "Segment_1", structure = Structure.SeptalCartilage },
            new Rule { nameContains = "Segment_2", structure = Structure.SinonasalBone },
        };

        public Structure fallback = Structure.SinonasalBone;

        public Structure Resolve(string objectName, out bool confirmed)
        {
            foreach (var r in rules)
            {
                if (string.IsNullOrEmpty(r.nameContains)) continue;
                if (objectName.IndexOf(r.nameContains, StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    confirmed = r.confirmed;
                    return r.structure;
                }
            }
            confirmed = false;
            return fallback;
        }

        /// <summary>Loads Resources/label_mapping.json if present (same shape as this class).</summary>
        public static LabelMapping LoadOrDefault()
        {
            var ta = Resources.Load<TextAsset>("label_mapping");
            if (ta == null) return new LabelMapping();
            try { return JsonUtility.FromJson<LabelMapping>(ta.text) ?? new LabelMapping(); }
            catch (Exception e) { Debug.LogWarning($"[SeptoMentor] label_mapping.json invalid: {e.Message}"); return new LabelMapping(); }
        }
    }
}
