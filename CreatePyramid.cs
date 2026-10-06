using UnityEngine;
using UnityEditor;

public class CreatePyramid : EditorWindow
{
    private float baseWidth = 2.0f;
    private float height = 3.0f;
    private bool addCollider = true;
    private bool addRigidbody = false;

    [MenuItem("GameObject/3D Object/Pyramid (Custom Mesh)", false, 10)]
    public static void ShowWindow()
    {
        GetWindow<CreatePyramid>("Pyramid Generator");
    }

    private void OnGUI()
    {
        GUILayout.Label("Pyramid Settings", EditorStyles.boldLabel);

        baseWidth = EditorGUILayout.FloatField("Base Width", baseWidth);
        height = EditorGUILayout.FloatField("Height", height);

        GUILayout.Space(10);
        addCollider = EditorGUILayout.Toggle("Add Mesh Collider", addCollider);
        addRigidbody = EditorGUILayout.Toggle("Add Rigidbody", addRigidbody);

        GUILayout.Space(20);

        if (GUILayout.Button("Create Pyramid"))
        {
            GeneratePyramidMesh();
            Close();
        }
    }

    private void GeneratePyramidMesh()
    {
        // 1. Setup the GameObject
        GameObject pyramid = new GameObject("Pyramid");
        Undo.RegisterCreatedObjectUndo(pyramid, "Create Pyramid");

        // Ensure the pyramid appears in front of the camera
        if (SceneView.lastActiveSceneView != null)
        {
            pyramid.transform.position = SceneView.lastActiveSceneView.camera.transform.position +
                                         SceneView.lastActiveSceneView.camera.transform.forward * 5f;
        }

        // 2. Add required components
        MeshFilter meshFilter = pyramid.AddComponent<MeshFilter>();
        pyramid.AddComponent<MeshRenderer>();

        // 3. Create the Mesh
        Mesh mesh = new Mesh();
        mesh.name = "PyramidMesh";

        float halfWidth = baseWidth / 2.0f;

        // Vertices (5 total: 4 for the base, 1 for the apex)
        Vector3 apex = new Vector3(0, height, 0);

        Vector3[] vertices = new Vector3[]
        {
            // Base vertices (at Y=0)
            new Vector3(-halfWidth, 0, -halfWidth), // 0: Bottom-Left (BL)
            new Vector3( halfWidth, 0, -halfWidth), // 1: Bottom-Right (BR)
            new Vector3( halfWidth, 0,  halfWidth), // 2: Top-Right (TR)
            new Vector3(-halfWidth, 0,  halfWidth), // 3: Top-Left (TL)
            
            // Apex vertex (at Y=height)
            apex                                    // 4: Apex (A)
        };

        // Triangles (Total of 6: 2 for the base, 4 for the sides = 18 indices)
        int[] triangles = new int[]
        {
            // Base (Two triangles making a square)
            0, 3, 2, // BL, TL, TR (Clockwise for the face pointing up/out)
            0, 2, 1, // BL, TR, BR
            
            // Side 1: Back (BL -> BR -> A)
            0, 1, 4,
            
            // Side 2: Right (BR -> TR -> A)
            1, 2, 4,
            
            // Side 3: Front (TR -> TL -> A)
            2, 3, 4,
            
            // Side 4: Left (TL -> BL -> A)
            3, 0, 4
        };

        // 4. Assign data to mesh
        mesh.vertices = vertices;
        mesh.triangles = triangles;
        mesh.RecalculateNormals(); // Crucial for lighting
        mesh.RecalculateBounds();

        meshFilter.mesh = mesh;

        // 5. Add components based on user selection
        if (addCollider)
        {
            pyramid.AddComponent<MeshCollider>().sharedMesh = mesh;
            pyramid.GetComponent<MeshCollider>().convex = true; // Recommend convex for physics interaction
        }

        if (addRigidbody)
        {
            pyramid.AddComponent<Rigidbody>();
        }

        // 6. Set a default material (or user defined)
        Renderer renderer = pyramid.GetComponent<Renderer>();
        renderer.sharedMaterial = new Material(Shader.Find("Standard"));
        renderer.sharedMaterial.color = Color.yellow;

        // 7. Auto-Select and Focus in Scene View (The added refinement)
        Selection.activeObject = pyramid;
        if (SceneView.lastActiveSceneView != null)
        {
            SceneView.lastActiveSceneView.FrameSelected();
        }
    }
}
