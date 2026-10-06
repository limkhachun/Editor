using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;
using Application = UnityEngine.Application;

public class TagColliderMap_Editor : EditorWindow
{
    private const string MapFilePath = "TagColliderMap.json";

    private List<string> availableTags;

    private TagColliderMapData tagMapData = new TagColliderMapData();

    private static readonly string[] ColliderOptions = { "None", "Box", "Sphere", "Capsule", "Mesh" };

    private Vector2 scrollPosition;
    private bool changesMade = false;

    public static void ShowWindow()
    {
        GetWindow<TagColliderMap_Editor>("Tag Collider Map Editor");
    }

    private void OnEnable()
    {
        if (!Directory.Exists(Path.Combine(Application.dataPath, "Editor")))
        {
            Directory.CreateDirectory(Path.Combine(Application.dataPath, "Editor"));
        }

        PopulateAvailableTags();
        LoadTagMap();
    }

    private void PopulateAvailableTags()
    {
        availableTags = UnityEditorInternal.InternalEditorUtility.tags.ToList();
        if (!availableTags.Contains("Untagged"))
        {
            availableTags.Insert(0, "Untagged");
        }

        foreach (string tag in availableTags)
        {
            if (!tagMapData.tagToColliderMap.ContainsKey(tag))
            {
                tagMapData.tagToColliderMap.Add(tag, "None");
            }
            // NEW: Initialize isStatic status
            if (!tagMapData.tagIsStaticMap.ContainsKey(tag))
            {
                tagMapData.tagIsStaticMap.Add(tag, false);
            }
        }

        var tagsToRemove = tagMapData.tagToColliderMap.Keys.Except(availableTags).ToList();
        foreach (var tag in tagsToRemove)
        {
            tagMapData.tagToColliderMap.Remove(tag);
            tagMapData.tagIsStaticMap.Remove(tag); // NEW: Remove from static map
        }
    }

    private void OnGUI()
    {
        GUILayout.Label("Tag-Specific Recommended Collider Map", EditorStyles.boldLabel);
        EditorGUILayout.HelpBox("Use this tool to define the optimal Collider Type and Static status for GameObjects based on their Tag, which improves bug detection accuracy.", MessageType.Info);
        GUILayout.Space(10);

        EditorGUILayout.BeginHorizontal();
        if (GUILayout.Button("Reload Tags", GUILayout.Width(120)))
        {
            PopulateAvailableTags();
            changesMade = true;
        }
        if (GUILayout.Button("Save Map", GUILayout.Width(120)))
        {
            SaveTagMap();
        }
        EditorGUILayout.EndHorizontal();

        GUILayout.Space(10);

        EditorGUILayout.BeginVertical(EditorStyles.helpBox);
        GUILayout.Label("Recommended Collider by Tag", EditorStyles.miniBoldLabel);

        // Header for the columns
        EditorGUILayout.BeginHorizontal();
        GUILayout.Label("Tag", EditorStyles.boldLabel, GUILayout.Width(100));
        GUILayout.Label("Collider Type", EditorStyles.boldLabel, GUILayout.Width(100));
        GUILayout.Label("Is Static?", EditorStyles.boldLabel);
        EditorGUILayout.EndHorizontal();


        scrollPosition = EditorGUILayout.BeginScrollView(scrollPosition, GUILayout.MinHeight(150));

        var sortedTags = tagMapData.tagToColliderMap.Keys.OrderBy(tag => tag).ToList();

        foreach (string tag in sortedTags)
        {
            EditorGUILayout.BeginHorizontal();
            GUILayout.Label($"{tag}", GUILayout.Width(100));

            // Collider Type Popup (Existing logic)
            string currentCollider = tagMapData.tagToColliderMap[tag];
            int currentIndex = System.Array.IndexOf(ColliderOptions, currentCollider);
            if (currentIndex == -1) currentIndex = 0; // Default to 'None' if somehow invalid

            int newIndex = EditorGUILayout.Popup(currentIndex, ColliderOptions, GUILayout.Width(100));

            if (newIndex != currentIndex)
            {
                tagMapData.tagToColliderMap[tag] = ColliderOptions[newIndex];
                changesMade = true;
                SaveTagMap();
            }

            // NEW: isStatic Toggle
            bool currentIsStatic = tagMapData.tagIsStaticMap[tag];
            bool newIsStatic = EditorGUILayout.Toggle(currentIsStatic);

            if (newIsStatic != currentIsStatic)
            {
                tagMapData.tagIsStaticMap[tag] = newIsStatic;
                changesMade = true;
                SaveTagMap();
            }

            EditorGUILayout.EndHorizontal();
        }

        EditorGUILayout.EndScrollView();
        EditorGUILayout.EndVertical();

        GUILayout.Space(10);
        string status = changesMade ? "Status: Unsaved Changes (Auto-save on change is active)" : "Status: Map is saved and up-to-date.";
        EditorGUILayout.LabelField(status, changesMade ? EditorStyles.boldLabel : EditorStyles.label);
        EditorGUILayout.LabelField($"Map File Location: {MapFilePath}", EditorStyles.miniLabel);
    }

    private void LoadTagMap()
    {
        string fullPath = Path.Combine(Application.dataPath, "Editor", MapFilePath);

        if (File.Exists(fullPath))
        {
            try
            {
                TagColliderMapData loadedData = new TagColliderMapData();
                JsonUtility.FromJsonOverwrite(File.ReadAllText(fullPath), loadedData);

                loadedData.OnAfterDeserialize();

                tagMapData = loadedData;
                changesMade = false;
                PopulateAvailableTags();
            }
            catch (System.Exception e)
            {
                UnityEngine.Debug.LogError($"Error loading Tag Collider Map: {e.Message}");
                tagMapData = new TagColliderMapData();
                PopulateAvailableTags();
            }
        }
        else
        {
            tagMapData = new TagColliderMapData();
            PopulateAvailableTags();
        }
    }

    private void SaveTagMap()
    {
        tagMapData.OnBeforeSerialize();

        string json = JsonUtility.ToJson(tagMapData, true);
        string fullPath = Path.Combine(Application.dataPath, "Editor", MapFilePath);

        try
        {
            File.WriteAllText(fullPath, json);
            AssetDatabase.Refresh();
            changesMade = false;
        }
        catch (System.Exception e)
        {
            UnityEngine.Debug.LogError($"Error saving Tag Collider Map: {e.Message}");
        }
    }

    [System.Serializable]
    public class TagColliderMapData : ISerializationCallbackReceiver
    {
        [SerializeField]
        public List<TagColliderEntry> entries = new List<TagColliderEntry>();

        public Dictionary<string, string> tagToColliderMap = new Dictionary<string, string>();
        public Dictionary<string, bool> tagIsStaticMap = new Dictionary<string, bool>(); // NEW: Static status map

        public void OnBeforeSerialize()
        {
            entries.Clear();
            foreach (var tag in tagToColliderMap.Keys)
            {
                string collider = tagToColliderMap.ContainsKey(tag) ? tagToColliderMap[tag] : "None";
                bool isStatic = tagIsStaticMap.ContainsKey(tag) ? tagIsStaticMap[tag] : false; // Use static map

                entries.Add(new TagColliderEntry(tag, collider, isStatic)); // NEW: Pass isStatic
            }
        }

        public void OnAfterDeserialize()
        {
            tagToColliderMap.Clear();
            tagIsStaticMap.Clear(); // Clear static map
            foreach (var entry in entries)
            {
                if (!tagToColliderMap.ContainsKey(entry.tag))
                {
                    tagToColliderMap.Add(entry.tag, entry.colliderType);
                    tagIsStaticMap.Add(entry.tag, entry.isStatic); // Populate static map
                }
            }
        }
    }

    [System.Serializable]
    public class TagColliderEntry
    {
        public string tag;
        public string colliderType;
        public bool isStatic; // NEW FIELD

        public TagColliderEntry(string t, string c, bool s) // NEW CONSTRUCTOR
        {
            tag = t;
            colliderType = c;
            isStatic = s;
        }
    }
}