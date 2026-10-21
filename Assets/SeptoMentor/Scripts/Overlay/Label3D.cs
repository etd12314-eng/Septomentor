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
        float _wrapWidth;
        string _lastRaw;

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
                _legacy.fontSize = 128;                      // larger glyph atlas = sharper text in the headset
                _legacy.characterSize = fontSize * 0.008f;
                _legacy.anchor = align == TextAlignmentOptions.Left || align == TextAlignmentOptions.TopLeft ? TextAnchor.UpperLeft : TextAnchor.MiddleCenter;
                _legacy.alignment = _legacy.anchor == TextAnchor.UpperLeft ? TextAlignment.Left : TextAlignment.Center;
            }
        }

        /// <summary>Box (local units) the text wraps to; the legacy fallback wraps to the width.</summary>
        public void SetBox(float width, float height)
        {
            _wrapWidth = width;
            if (_tmp != null) _tmp.rectTransform.sizeDelta = new Vector2(width, height);
        }

        public void Set(string text, Color color)
        {
            if (_tmp != null) { _tmp.text = text; _tmp.color = color; return; }
            _legacy.color = color;
            if (_lastRaw == text) return;
            _lastRaw = text;
            _legacy.text = Wrap(text ?? "");
        }

        /// <summary>Greedy word wrap for TextMesh, which has none. Glyph advances are in font
        /// pixels at fontSize; TextMesh draws them at characterSize / 10 local units per pixel.</summary>
        string Wrap(string text)
        {
            if (_wrapWidth <= 0 || text.Length == 0) return text;
            var font = _legacy.font;
            font.RequestCharactersInTexture(text, _legacy.fontSize);
            float unitsPerPx = _legacy.characterSize * 0.1f;
            float space = Advance(font, ' ') * unitsPerPx;
            var sb = new System.Text.StringBuilder(text.Length + 8);
            var paras = text.Split('\n');
            for (int i = 0; i < paras.Length; i++)
            {
                var para = paras[i];
                if (i > 0) sb.Append('\n');
                float x = 0;
                foreach (var word in para.Split(' '))
                {
                    float w = 0;
                    foreach (char ch in word) w += Advance(font, ch) * unitsPerPx;
                    if (x > 0 && x + space + w > _wrapWidth) { sb.Append('\n'); x = 0; }
                    else if (x > 0) { sb.Append(' '); x += space; }
                    sb.Append(word);
                    x += w;
                }
            }
            return sb.ToString();
        }

        float Advance(Font font, char ch) =>
            font.GetCharacterInfo(ch, out var info, _legacy.fontSize) ? info.advance : _legacy.fontSize * 0.55f;

        public void SetActive(bool on) { if (go.activeSelf != on) go.SetActive(on); }
    }
}
