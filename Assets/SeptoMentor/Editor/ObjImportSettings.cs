using UnityEditor;

namespace SeptoMentor.EditorTools
{
    /// <summary>Import settings for the Slicer mesh: metres, readable (runtime MeshCollider), no materials.</summary>
    public class ObjImportSettings : AssetPostprocessor
    {
        void OnPreprocessModel()
        {
            if (!assetPath.StartsWith("Assets/SeptoMentor/Models/")) return;
            var mi = (ModelImporter)assetImporter;
            mi.globalScale = 1f;            // OBJ is already in metres
            mi.useFileScale = false;
            mi.isReadable = true;
            mi.importNormals = ModelImporterNormals.Calculate;
            mi.normalSmoothingAngle = 60f;
            mi.materialImportMode = ModelImporterMaterialImportMode.None;
            mi.importAnimation = false;
            mi.meshCompression = ModelImporterMeshCompression.Off;
        }
    }
}
