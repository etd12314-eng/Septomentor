using TMPro;
using UnityEngine;

namespace SeptoMentor
{
    /// <summary>
    /// World-space text. Uses TextMeshPro when TMP Essentials are imported (OPEN ITEM 2),
    /// otherwise falls back to legacy TextMesh so the build never shows blank UI.
    /// </summary>
    public class Label3D
    {
        public readonly GameObject go;
        readonly TextMeshPro _tmp;
        readonly TextMesh _legacy;

        public Label3D(Transform parent, string name, float fontSize, TextAlignmentOptions align = TextAlignmentOptions.Center)
        {
            go = new GameObject(name);
            go.transform.SetParent(parent, false);
            var font = TMP_Settings.instance != null ? TMP_Settings.defaultFontAsset : null;
            if (font != null)
            {
                _tmp = go.AddComponent<TextMeshPro>();
                _tmp.font = font;
                _tmp.fontSize = fontSize;
                _tmp.alignment = align;
                _tmp.textWrappingMode = TextWrappingModes.Normal;
                _tmp.rectTransform.sizeDelta = new Vector2(10, 2);
                _tmp.outlineWidth = 0.2f;
                _tmp.outlineColor = new Color32(0, 0, 0, 255);
            }
            else
            {
                _legacy = go.AddComponent<TextMesh>();
                _legacy.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
                go.GetComponent<MeshRenderer>().sharedMaterial = _legacy.font.material;
                _legacy.fontSize = 64;
                _legacy.characterSize = fontSize * 0.016f;
                _legacy.anchor = align == TextAlignmentOptions.Left || align == TextAlignmentOptions.TopLeft ? TextAnchor.UpperLeft : TextAnchor.MiddleCenter;
                _legacy.alignment = _legacy.anchor == TextAnchor.UpperLeft ? TextAlignment.Left : TextAlignment.Center;
            }
        }

        /// <summary>Width (TMP units) for wrapping; ignored by the legacy fallback.</summary>
        public void SetBox(float width, float height)
        {
            if (_tmp != null) _tmp.rectTransform.sizeDelta = new Vector2(width, height);
        }

        public void Set(string text, Color color)
        {
            if (_tmp != null) { _tmp.text = text; _tmp.color = color; }
            else { _legacy.text = text; _legacy.color = color; }
        }

        public void SetActive(bool on) { if (go.activeSelf != on) go.SetActive(on); }
    }
}
