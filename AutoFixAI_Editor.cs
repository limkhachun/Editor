using SimpleJSON;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.AI;
using UnityEngine.Networking;
using UnityEngine.SceneManagement;
using Application = UnityEngine.Application;

public class AutoFixAI_Editor : EditorWindow
{
    private const string ServerUrlBase = "http://localhost:5000";
    private readonly string serverUrl = $"{ServerUrlBase}/analyze-physics";

    private static readonly string[] PrimitiveNames = { "Cube", "Sphere", "Capsule", "Cylinder", "Pyramid" };

    private static readonly Dictionary<string, Color> BugColorMap = new Dictionary<string, Color>
    {
        {"Missing Rigidbody", Color.red},
        {"Kinematic with Gravity", Color.yellow},
        {"Physics Mass Anomaly", Color.cyan},
        {"Physics Drag Anomaly", Color.magenta},
        {"Physics Angular Drag Anomaly", new Color(1f, 0.5f, 0f)},
        {"Mesh Convex Collider Bug", Color.green},
        {"Mismatch Collider", Color.blue},
        {"Missing Collider", Color.white}
    };

    private UnityWebRequest ongoingRequest;
    private Vector2 scrollPosition;
    private string bugReportStatus = "Click 'Scan Scene' to find bugs.";
    private List<BugReport> currentBugs = new List<BugReport>();
    private string selectedObjectName = "";
    private BugReport selectedBug = null;
    private List<ActionTakenReference> actionsTaken = new List<ActionTakenReference>();
    private List<string> availableTags;
    private int tagFilterMask = -1;
    private string[] tagOptions;
    private bool isReportExported = false;
    private bool scanForMissingCollider = true;
    private bool scanForMissingRigidbody = true;
    private bool scanForKinematicGravity = true;
    private bool scanForMassAnomaly = true;
    private bool scanForDragAnomaly = true;
    private bool scanForAngularDragAnomaly = true;
    private bool scanForMeshConvexCollider = true;
    private bool scanForMismatchCollider = true;
    private GUIStyle bugItemButtonStyle;
    


    [MenuItem("Tools/AutoFixAI")]
    public static void ShowWindow()
    {
        GetWindow<AutoFixAI_Editor>("AutoFixAI");
    }

    private void OnEnable()
    {
        SceneView.duringSceneGui += OnSceneGUI;
        PopulateAvailableTags();
    }

    private void OnDisable()
    {
        SceneView.duringSceneGui -= OnSceneGUI;
    }

    private void OnDestroy()
    {
        SceneView.duringSceneGui -= OnSceneGUI;

        if ((currentBugs.Count > 0 || actionsTaken.Count > 0) && !isReportExported)
        {
            PromptForReportGeneration();
        }
    }

    private void PopulateAvailableTags()
    {
        availableTags = UnityEditorInternal.InternalEditorUtility.tags.ToList();

        if (!availableTags.Contains("Untagged"))
        {
            availableTags.Insert(0, "Untagged");
        }

        tagOptions = availableTags.ToArray();
    }

    private void PromptForReportGeneration()
    {
        int result = EditorUtility.DisplayDialogComplex(
            "Generate Bug Report?",
            "Do you want to generate a final bug report before closing the window?",
            "Yes, Generate Report", 
            "No, Close Window",  
            ""                   
        );

        if (result == 0)
        {
            ExportBugReport();
        }
     
    }

    private static void ShowTagColliderMapEditor()
    {
        TagColliderMap_Editor.ShowWindow();
    }




    private void OnGUI()
    {
        if (bugItemButtonStyle == null)
        {
            bugItemButtonStyle = new GUIStyle(GUI.skin.button);
            bugItemButtonStyle.wordWrap = true;
            bugItemButtonStyle.richText = true;
            bugItemButtonStyle.padding = new RectOffset(5, 5, 5, 5);
            bugItemButtonStyle.alignment = TextAnchor.MiddleLeft;
            bugItemButtonStyle.normal.textColor = Color.white;
        }

        scrollPosition = GUILayout.BeginScrollView(scrollPosition, GUILayout.ExpandWidth(true));

        GUILayout.Label("AutoFixAI - Intelligent Physics Debugger", EditorStyles.boldLabel);
        GUILayout.Space(10);

        EditorGUILayout.BeginVertical(EditorStyles.helpBox);
        GUILayout.Label("Object Filter", EditorStyles.miniBoldLabel);
        tagFilterMask = EditorGUILayout.MaskField(new GUIContent("Tags to Scan"), tagFilterMask, tagOptions, GUILayout.ExpandWidth(true));

        if (GUILayout.Button("Tag Collider Map", GUILayout.Height(EditorGUIUtility.singleLineHeight), GUILayout.ExpandWidth(true)))
        {
            ShowTagColliderMapEditor();
        }
        EditorGUILayout.EndVertical();
        GUILayout.Space(5);

        EditorGUILayout.BeginVertical(EditorStyles.helpBox);
        GUILayout.Label("Scan Settings", EditorStyles.miniBoldLabel);

        GUIContent content;

        content = new GUIContent("Missing Collider", "Check for GameObjects missing any Collider component.");
        scanForMissingCollider = EditorGUILayout.Toggle(content, scanForMissingCollider);

        content = new GUIContent("Missing RB", "Check for GameObjects that should have a Rigidbody but don't.");
        scanForMissingRigidbody = EditorGUILayout.Toggle(content, scanForMissingRigidbody);

        content = new GUIContent("Mesh Collider Convex", "Check for dynamic physics objects using non-convex Mesh Colliders.");
        scanForMeshConvexCollider = EditorGUILayout.Toggle(content, scanForMeshConvexCollider);

        content = new GUIContent("Mismatch Collider", "Check for objects using a non-optimal primitive collider type.");
        scanForMismatchCollider = EditorGUILayout.Toggle(content, scanForMismatchCollider);

        content = new GUIContent("Kinematic/Gravity Conflict", "Check for Rigidbodies set to Kinematic while having Use Gravity enabled.");
        scanForKinematicGravity = EditorGUILayout.Toggle(content, scanForKinematicGravity);

        content = new GUIContent("Mass Anomaly", "Check for objects with anomalous mass values (e.g., too high or too low).");
        scanForMassAnomaly = EditorGUILayout.Toggle(content, scanForMassAnomaly);

        content = new GUIContent("Linear Drag Anomaly", "Check for objects with anomalous linear drag values.");
        scanForDragAnomaly = EditorGUILayout.Toggle(content, scanForDragAnomaly);

        content = new GUIContent("Angular Drag Anomaly", "Check for objects with anomalous angular drag values.");
        scanForAngularDragAnomaly = EditorGUILayout.Toggle(content, scanForAngularDragAnomaly);

        EditorGUILayout.EndVertical();

        GUILayout.Space(10);

        EditorGUILayout.BeginHorizontal();
        if (GUILayout.Button("Scan Scene", GUILayout.Height(30), GUILayout.ExpandWidth(true)))
        {
            SendPhysicsDataToAI();
        }
        if (GUILayout.Button("Export Bug Report", GUILayout.Height(30), GUILayout.ExpandWidth(true)))
        {
            ExportBugReport();
        }
        EditorGUILayout.EndHorizontal();

        GUILayout.Space(10);
        GUILayout.Label($"Status: {bugReportStatus}", EditorStyles.miniLabel);

        if (currentBugs.Count == 0 && bugReportStatus.Contains("Found 0"))
        {
            EditorGUILayout.HelpBox("No bugs detected in the scene.", MessageType.Info);
        }
        else if (currentBugs.Count > 0)
        {
            GUILayout.Label("Detected Physics Issues", EditorStyles.miniBoldLabel);


            var groupedBugs = currentBugs.GroupBy(b => new { b.objectPath, b.objectName, b.objectTag })
                                        .ToDictionary(g => g.Key, g => g.ToList());

            var orderedGroups = groupedBugs.OrderBy(g => g.Key.objectPath);

            foreach (var group in orderedGroups)
            {
                string objectPathKey = group.Key.objectPath;
                string displayLabel = $"{group.Key.objectName} (Tag: {group.Key.objectTag}, Path: {objectPathKey})";
                List<BugReport> bugs = group.Value;

                bool isCurrentlyExpanded = (selectedObjectName == objectPathKey);
                bool newExpandedState = isCurrentlyExpanded;

                EditorGUILayout.BeginHorizontal();
                newExpandedState = EditorGUILayout.Foldout(isCurrentlyExpanded, GUIContent.none, true, EditorStyles.foldout);

                GUILayout.Label($"{displayLabel} ({bugs.Count} bugs found)", EditorStyles.boldLabel, GUILayout.ExpandWidth(true));

                EditorGUILayout.EndHorizontal();

                if (newExpandedState != isCurrentlyExpanded)
                {
                    selectedObjectName = newExpandedState ? objectPathKey : "";

                }
                if (selectedObjectName == objectPathKey)
                {
                    EditorGUI.indentLevel++;

                    EditorGUILayout.BeginVertical();

                    foreach (var bug in bugs)
                    {
                        string displayString = $"<b>[{bug.bugType}]</b>:\n{bug.suggestion}";

                        if (GUILayout.Button(displayString, bugItemButtonStyle, GUILayout.ExpandWidth(true), GUILayout.Height(EditorGUIUtility.singleLineHeight * 3)))
                        {
                            selectedBug = bug;
                        }
                    }

                    EditorGUILayout.EndVertical();

                    EditorGUI.indentLevel--;

                }

            }
        }

        if (selectedBug != null && !currentBugs.Contains(selectedBug))
        {
            selectedBug = null;
        }

        if (selectedBug != null)
        {
            EditorGUILayout.Space(5);
            EditorGUILayout.LabelField("Selected Bug Details", EditorStyles.miniBoldLabel);
            EditorGUILayout.LabelField("Object:", selectedBug.objectName);
            EditorGUILayout.LabelField("Type:", selectedBug.bugType);
            EditorGUILayout.HelpBox($"Suggestion: {selectedBug.suggestion}", MessageType.Warning);

            EditorGUILayout.BeginHorizontal();

            if (GUILayout.Button("Focus Object", GUILayout.ExpandWidth(true)))
            {
                FocusObject(selectedBug);
            }
            if (GUILayout.Button("Apply Fix", GUILayout.ExpandWidth(true)))
            {
                ApplyFix(selectedBug);
            }
            if (GUILayout.Button("Ignore", GUILayout.ExpandWidth(true)))
            {
                SendIgnoreSignal(selectedBug);
            }
            EditorGUILayout.EndHorizontal();
        }

        GUILayout.EndScrollView();

        if (ongoingRequest != null && ongoingRequest.isDone)
        {
            ProcessAIResponse(ongoingRequest);
            ongoingRequest = null;
        }

        if (ongoingRequest != null || EditorApplication.isUpdating)
        {
            Repaint();
        }
    }

    private void FocusObject(BugReport bug)
    {
        GameObject target = FindObjectByPath(bug.objectPath);

        if (target != null)
        {
            EditorGUIUtility.PingObject(target);
            Selection.activeGameObject = target;
            if (SceneView.lastActiveSceneView != null)

            {
                SceneView.lastActiveSceneView.FrameSelected();
            }

        }
    }

    private string GetObjectPath(GameObject go)
    {
        string path = "/" + go.name;
        Transform current = go.transform.parent;
        while (current != null)
        {
            path = "/" + current.name + path;
            current = current.parent;
        }
        return path;
    }

    private GameObject FindObjectByPath(string path)
    {
        foreach (GameObject go in FindObjectsByType<GameObject>(FindObjectsSortMode.None))
        {
            if (GetObjectPath(go) == path)
            {
                return go;
            }
        }
        return null;
    }

    private void SendPhysicsDataToAI()
    {
        List<string> selectedTags = new List<string>();

        if (tagFilterMask == 0)
        {
            EditorUtility.DisplayDialog("Tag Selection Required",
                "You have not selected any tags in the 'Object Filter'. Please select one or more tags to scan.",
                "OK");
            return; 
        }
        for (int i = 0; i < tagOptions.Length; i++)
        {
            if ((tagFilterMask & (1 << i)) != 0)
            {
                selectedTags.Add(tagOptions[i]);
            }
        }

        if ((currentBugs.Count > 0 || actionsTaken.Count > 0) && !isReportExported)
        {
            int rescanResult = EditorUtility.DisplayDialogComplex("Rescan Warning",
                "A previous scan session is active with unsaved bug data. Do you want to export a report before running a new scan?",
                "Yes, Export Report", "No, Continue Rescan", "Cancel Rescan");

            if (rescanResult == 0) 
            {
                ExportBugReport();
            }
            else if (rescanResult == 1)
            {
                
            }
            else if (rescanResult == 2)
            {
                return; 
            }
        }

        currentBugs.Clear();
        selectedBug = null;
        selectedObjectName = "";
        isReportExported = false;

        List<GameObjectData> objectDataList = new List<GameObjectData>();

        foreach (GameObject go in FindObjectsByType<GameObject>(FindObjectsSortMode.None))
        {
            if (go.hideFlags != HideFlags.None || !go.activeInHierarchy || go.name.Contains("AutoFixAI")) continue;

            Rigidbody rb = go.GetComponent<Rigidbody>();
            Collider coll = go.GetComponent<Collider>();
            Renderer ren = go.GetComponent<Renderer>();

            UnityEngine.Component[] allComponents = go.GetComponents<UnityEngine.Component>();
            List<string> componentNames = new List<string>();
            foreach (UnityEngine.Component comp in allComponents)
            {
                if (comp != null)
                {
                    componentNames.Add(comp.GetType().Name);
                }
            }

            string objType = "Unknown";
            foreach (string primitive in PrimitiveNames)
            {
                if (go.name.StartsWith(primitive, System.StringComparison.OrdinalIgnoreCase))
                {
                    objType = primitive;
                    break;
                }
            }
            string collType = "";
            bool isConvex = false;

            if (coll != null)
            {
                if (coll is MeshCollider meshColl)
                {
                    collType = "Mesh";
                    isConvex = meshColl.convex;
                }
                else if (coll is BoxCollider) { collType = "Box"; }
                else if (coll is SphereCollider) { collType = "Sphere"; }
                else if (coll is CapsuleCollider) { collType = "Capsule"; }
            }

            float objectMaxDimension = 1.0f;
            if (ren != null)
            {
                objectMaxDimension = Mathf.Max(ren.bounds.size.x, ren.bounds.size.y, ren.bounds.size.z);
            }

            string uniquePath = GetObjectPath(go);

            GameObjectData data = new GameObjectData
            {
                objectName = go.name,
                objectPath = uniquePath,
                objectTag = go.tag,
                objectType = objType,
                colliderType = collType,
                isMeshColliderConvex = isConvex,
                isKinematic = rb != null ? rb.isKinematic : false,
                useGravity = rb != null ? rb.useGravity : false,
                mass = rb != null ? rb.mass : 1.0f,
                drag = rb != null ? rb.linearDamping : 0.0f,
                angularDrag = rb != null ? rb.angularDamping : 0.05f,
                objectSize = objectMaxDimension,

                componentNames = componentNames,

                scanSettings = new ScanSettingsData
                {
                    scanForMissingRigidbody = this.scanForMissingRigidbody,
                    scanForKinematicGravity = this.scanForKinematicGravity,
                    scanForMassAnomaly = this.scanForMassAnomaly,
                    scanForDragAnomaly = this.scanForDragAnomaly,
                    scanForAngularDragAnomaly = this.scanForAngularDragAnomaly,
                    scanForMeshConvexCollider = this.scanForMeshConvexCollider,
                    scanForMismatchCollider = this.scanForMismatchCollider,
                    scanForMissingCollider = this.scanForMissingCollider
                }
            };
            objectDataList.Add(data);
        }

        DataWrapper wrapper = new DataWrapper(objectDataList, Application.productName, selectedTags);
        string jsonToSend = JsonUtility.ToJson(wrapper);

        ongoingRequest = UnityWebRequest.PostWwwForm(serverUrl, "application/json");
        byte[] bodyRaw = System.Text.Encoding.UTF8.GetBytes(jsonToSend);
        ongoingRequest.uploadHandler = (UploadHandler)new UploadHandlerRaw(bodyRaw);
        ongoingRequest.downloadHandler = (DownloadHandler)new DownloadHandlerBuffer();
        ongoingRequest.SetRequestHeader("Content-Type", "application/json");

        ongoingRequest.SendWebRequest();
        bugReportStatus = "Scanning scene and analyzing data...";
    }

    private void ProcessAIResponse(UnityWebRequest request)
    {
        if (request.result != UnityWebRequest.Result.Success)
        {
            bugReportStatus = $"Error connecting to AI Server: {request.error}";
            return;
        }

        try
        {
            var N = JSON.Parse(request.downloadHandler.text);
            var currentBugsArray = N["current_bugs"].AsArray;
            var fixedBugsArray = N["fixed_bug_references"].AsArray;

            currentBugs.Clear();
            foreach (JSONNode node in currentBugsArray)
            {
                currentBugs.Add(new BugReport
                {
                    objectName = node["object_name"],
                    objectPath = node["object_path"],
                    objectTag = node["object_tag"],
                    bugType = node["bug_type"],
                    suggestion = node["suggestion"],
                    fixAction = node["fix_action"]
                });
            }

            bugReportStatus = $"Scan complete. Found {currentBugs.Count} current bugs. {fixedBugsArray.Count} bugs confirmed fixed.";
        }
        catch (System.Exception e)
        {
            bugReportStatus = $"Error processing AI response: {e.Message}";
        }
        Repaint();
    }

    private void ApplyFix(BugReport bug)
    {
        GameObject target = FindObjectByPath(bug.objectPath);
        if (target == null)
        {
            EditorUtility.DisplayDialog("Error", "Target object not found in scene.", "OK");
            return;
        }

        if (string.IsNullOrEmpty(bug.fixAction) || bug.fixAction == "NONE")
        {
            EditorUtility.DisplayDialog("Fix Not Available", $"The bug '{bug.bugType}' on '{bug.objectName}' does not have an automated fix action.", "OK");
            return;
        }

        Undo.RecordObject(target, $"AutoFixAI: {bug.bugType} fix on {bug.objectName}");

        Rigidbody rb = target.GetComponent<Rigidbody>();
        Renderer ren = target.GetComponent<Renderer>();

        bool fixApplied = true;

        string[] fixParts = bug.fixAction.Split(':');
        string actionType = fixParts[0];
        string actionValue = fixParts.Length > 1 ? fixParts[1] : string.Empty;

        switch (actionType)
        {
            case "ADD_RIGIDBODY":
                Undo.AddComponent<Rigidbody>(target); 
                break;
            case "ADD_BOX_COLLIDER":
            case "ADD_SPHERE_COLLIDER":
            case "ADD_CAPSULE_COLLIDER":
            case "ADD_MESH_COLLIDER":
                if (target.GetComponent<Collider>() == null)
                {
                    if (actionType == "ADD_BOX_COLLIDER") Undo.AddComponent<BoxCollider>(target);
                    else if (actionType == "ADD_SPHERE_COLLIDER") Undo.AddComponent<SphereCollider>(target);
                    else if (actionType == "ADD_CAPSULE_COLLIDER") Undo.AddComponent<CapsuleCollider>(target);
                    else if (actionType == "ADD_MESH_COLLIDER") Undo.AddComponent<MeshCollider>(target);
                }
                else
                {
                    EditorUtility.DisplayDialog("Fix Failed", "Object already has a collider. Fix not applied.", "OK");
                    fixApplied = false;
                }
                break;
            case "SET_MASS":
                if (rb != null && float.TryParse(actionValue, out float targetMass))
                {
                    Undo.RecordObject(rb, "Set Mass");
                    rb.mass = targetMass;
                }
                else fixApplied = false;
                break;
            case "SET_DRAG":
                if (rb != null && float.TryParse(actionValue, out float targetDrag))
                {
                    Undo.RecordObject(rb, "Set Drag");
                    rb.linearDamping = targetDrag;
                }
                else fixApplied = false;
                break;
            case "SET_ANGULAR_DRAG":
                if (rb != null && float.TryParse(actionValue, out float targetAngularDrag))
                {
                    Undo.RecordObject(rb, "Set Angular Drag");
                    rb.angularDamping = targetAngularDrag;
                }
                else fixApplied = false;
                break;
            case "REPLACE_COLLIDER":

                int groupIndex = Undo.GetCurrentGroup();
                Undo.IncrementCurrentGroup();

                string newColliderType = actionValue;
                Collider existingCollider = target.GetComponent<Collider>();

                if (existingCollider != null)
                {
                    Undo.DestroyObjectImmediate(existingCollider);
                }

                if (newColliderType == "None")
                {
                    fixApplied = true;
                }
                else
                {
                    switch (newColliderType)
                    {
                        case "Box": Undo.AddComponent<BoxCollider>(target); break;
                        case "Sphere": Undo.AddComponent<SphereCollider>(target); break;
                        case "Capsule": Undo.AddComponent<CapsuleCollider>(target); break;
                        case "Mesh": Undo.AddComponent<MeshCollider>(target); break;
                        default: fixApplied = false; break;
                    }
                }
                Undo.CollapseUndoOperations(groupIndex);
                Undo.SetCurrentGroupName("Replace Collider");

                break;
            case "DISABLE_GRAVITY":
                if (rb != null)
                {
                    Undo.RecordObject(rb, "Disable Gravity");
                    rb.useGravity = false;
                }
                else fixApplied = false;
                break;
            case "MESH_CONVEX":
                MeshCollider mc = target.GetComponent<MeshCollider>();
                if (mc != null)
                {
                    Undo.RecordObject(mc, "Set Mesh Convex");
                    mc.convex = true;
                }
                else fixApplied = false;
                break;
            default:
                EditorUtility.DisplayDialog("Fix Not Implemented", $"No automated fix for action: {bug.fixAction}.", "OK");
                fixApplied = false;
                break;
        }

        if (fixApplied)
        {
            ReportFixToServer(bug);
            if (ongoingRequest != null)
            {
                ongoingRequest.Abort();
                ongoingRequest = null;
                bugReportStatus = "Local fix applied. Previous scan cancelled.";
            }

            actionsTaken.Add(new ActionTakenReference
            {
                objectName = bug.objectName,
                objectTag = bug.objectTag,
                bugType = bug.bugType,
                actionType = "FIXED",
                timestamp = System.DateTime.Now
            });

            currentBugs.Remove(bug);
            selectedBug = null;

            string objectNameWithTag = $"{bug.objectName} (Tag: {bug.objectTag})";
            if (!currentBugs.Any(b => $"{b.objectName} (Tag: {b.objectTag})" == objectNameWithTag))
            {
                selectedObjectName = "";
            }
            isReportExported = false;

            Undo.SetCurrentGroupName($"AutoFixAI Fix: {bug.bugType} on {bug.objectName}");

            Repaint(); 
        }
        else
        {
            EditorUtility.DisplayDialog("Fix Failed", $"Attempted fix action '{bug.fixAction}' failed for bug '{bug.bugType}' on '{bug.objectName}'. Check console for details.", "OK");
        }
    }


   
    private void SendIgnoreSignal(BugReport bug)
    {
        if (ongoingRequest != null)
        {
            ongoingRequest.Abort();
            ongoingRequest = null;
            bugReportStatus = "Bug ignored. Previous scan cancelled.";
        }

        var ignoreData = new IgnoreSignalData
        {
            objectName = bug.objectName,
            objectPath = bug.objectPath,
            objectTag = bug.objectTag,
            bugType = bug.bugType,
            projectName = Application.productName
        };
        string jsonToSend = JsonUtility.ToJson(ignoreData);

        string ignoreUrl = "http://localhost:5000/ignore-bug";
        UnityWebRequest ignoreRequest = UnityWebRequest.PostWwwForm(ignoreUrl, "application/json");
        byte[] bodyRaw = System.Text.Encoding.UTF8.GetBytes(jsonToSend);
        ignoreRequest.uploadHandler = (UploadHandler)new UploadHandlerRaw(bodyRaw);
        ignoreRequest.downloadHandler = (DownloadHandler)new DownloadHandlerBuffer();
        ignoreRequest.SetRequestHeader("Content-Type", "application/json");

        ignoreRequest.SendWebRequest();

        actionsTaken.Add(new ActionTakenReference
        {
            objectName = bug.objectName,
            objectTag = bug.objectTag,
            bugType = bug.bugType,
            actionType = "IGNORED",
            timestamp = System.DateTime.Now
        });

        currentBugs.Remove(bug);
        selectedBug = null;

        string objectNameWithTag = $"{bug.objectName} (Tag: {bug.objectTag})";
        if (!currentBugs.Any(b => $"{b.objectName} (Tag: {b.objectTag})" == objectNameWithTag))
        {
            selectedObjectName = "";
        }

        isReportExported = false;

        Repaint();
    }

    private void ReportFixToServer(BugReport bug)
    {

        string url = $"{ServerUrlBase}/report-fix";

        var fixData = new IgnoreSignalData
        {
            objectName = bug.objectName,
            objectPath = bug.objectPath,
            objectTag = bug.objectTag,
            bugType = bug.bugType,
            projectName = Application.productName
        };

        string jsonToSend = JsonUtility.ToJson(fixData);

        UnityWebRequest fixRequest = UnityWebRequest.PostWwwForm(url, "application/json");
        byte[] bodyRaw = System.Text.Encoding.UTF8.GetBytes(jsonToSend);
        fixRequest.uploadHandler = (UploadHandler)new UploadHandlerRaw(bodyRaw);
        fixRequest.downloadHandler = (DownloadHandler)new DownloadHandlerBuffer();
        fixRequest.SetRequestHeader("Content-Type", "application/json");

        fixRequest.SendWebRequest();
    }

    private void OnSceneGUI(SceneView sceneView)
    {
        if (currentBugs.Count == 0) return;

        var groupedBugs = currentBugs.GroupBy(b => b.objectPath);

        foreach (var group in groupedBugs)
        {
            GameObject go = GameObject.Find(group.Key);
            if (go == null) continue;

            Renderer ren = go.GetComponent<Renderer>();
            if (ren == null) continue;

            string simpleName = group.First().objectName;

            foreach (var bug in group)
            {
                Handles.color = GetBugColor(bug.bugType);
                Handles.DrawWireCube(ren.bounds.center, ren.bounds.size);
            }

            Vector3 labelPosition = ren.bounds.center + Vector3.up * (ren.bounds.extents.y + 0.3f);
            GUIStyle style = new GUIStyle(EditorStyles.boldLabel);
            style.normal.textColor = Color.red;
            style.alignment = TextAnchor.MiddleCenter;

            Handles.Label(labelPosition, $"[{group.Count()} BUGS] {simpleName}", style);
        }
    }

    private Color GetBugColor(string bugType)
    {
        if (BugColorMap.TryGetValue(bugType, out Color color))
        {
            return color;
        }
        return Color.gray;
    }

    private void ExportBugReport()
    {

        const string ReportFolderName = "AutoFixAI_Reports";
        string projectPath = Application.dataPath;
        string reportFolderPath = Path.Combine(projectPath, ReportFolderName);


        if (!Directory.Exists(reportFolderPath))
        {
            Directory.CreateDirectory(reportFolderPath);
            AssetDatabase.CreateFolder("Assets", ReportFolderName);
        }

        string fileName = $"BugReport_{Application.productName}_{System.DateTime.Now:yyyyMMdd_HHmmss}.txt";
        string fullPath = Path.Combine(reportFolderPath, fileName);

        try
        {
            using (StreamWriter sw = new StreamWriter(fullPath))
            {
                sw.WriteLine($"AutoFixAI Bug Report for Project: {Application.productName}");
                sw.WriteLine($"Scan Date: {System.DateTime.Now}");
                sw.WriteLine("=====================================");

                sw.WriteLine("\n--- Scan Summary ---");
                sw.WriteLine($"Total Active Bugs Found: {currentBugs.Count}");
                sw.WriteLine($"Total Actions Taken Since Last Scan/Report: {actionsTaken.Count}");
                sw.WriteLine("\nActive Bug Breakdown by Type:");

                var bugTypeCounts = currentBugs
                    .GroupBy(b => b.bugType)
                    .ToDictionary(g => g.Key, g => g.Count());

                foreach (var kvp in bugTypeCounts.OrderByDescending(x => x.Value))
                {
                    sw.WriteLine($"  - {kvp.Key}: {kvp.Value}");
                }

                sw.WriteLine("-------------------------------------\n");

                sw.WriteLine("\n--- Active Scan Rules ---");
                sw.WriteLine($"1. Missing Collider: {(scanForMissingCollider ? "ON" : "OFF")}");
                sw.WriteLine($"2. Missing Rigidbody: {(scanForMissingRigidbody ? "ON" : "OFF")}");
                sw.WriteLine($"3. Kinematic/Gravity Conflict: {(scanForKinematicGravity ? "ON" : "OFF")}");
                sw.WriteLine($"4. Mass Anomaly (AD): {(scanForMassAnomaly ? "ON" : "OFF")}");
                sw.WriteLine($"5. Linear Drag Anomaly (AD): {(scanForDragAnomaly ? "ON" : "OFF")}");
                sw.WriteLine($"6. Angular Drag Anomaly (AD): {(scanForAngularDragAnomaly ? "ON" : "OFF")}");
                sw.WriteLine($"7. Mesh Collider Convex (RL): {(scanForMeshConvexCollider ? "ON" : "OFF")}");
                sw.WriteLine($"8. Mismatch Collider (RL): {(scanForMismatchCollider ? "ON" : "OFF")}");
                sw.WriteLine("-------------------------------------\n");

                sw.WriteLine("\n--- Current Active Bugs (Grouped by Unique Object Path) ---");
                if (currentBugs.Count == 0) sw.WriteLine("None.");


                var bugsByGroup = currentBugs.GroupBy(b => new { b.objectPath, b.objectName, b.objectTag })
                    .OrderBy(g => g.Key.objectPath);

                foreach (var group in bugsByGroup)
                {
                    sw.WriteLine($"\nOBJECT PATH: {group.Key.objectPath}");
                    sw.WriteLine($"  Object Name: {group.Key.objectName} | Tag: {group.Key.objectTag} ({group.Count()} bugs)");

                    foreach (var b in group)
                    {
                        sw.WriteLine("\n  ----------------------------------------------------------------");
                        sw.WriteLine($"  --- BUG TYPE: {b.bugType} ---");
                        sw.WriteLine($"  Finding: {b.suggestion}");

                        string fixActionLine = b.fixAction != "NONE" && !string.IsNullOrEmpty(b.fixAction)
                                    ? $"  Recommended Action: {b.fixAction}" : "  Recommended Action: None (Manual Review Required)";

                        sw.WriteLine(fixActionLine);
                        sw.WriteLine("  ----------------------------------------------------------------");
                    }
                }

                sw.WriteLine("\n--- Actions Taken Since Last Scan (Fixes & Ignores) ---");
                if (actionsTaken.Count == 0) sw.WriteLine("None.");

                var actionsByObject = actionsTaken.GroupBy(a => a.objectPath)
                    .OrderBy(g => g.Key);

                foreach (var group in actionsByObject)
                {
                    sw.WriteLine($"\nOBJECT PATH: {group.Key}");
                    foreach (var action in group.OrderBy(a => a.timestamp))
                    {
                        sw.WriteLine($"  - [{action.actionType}] {action.bugType} on '{action.objectName}' (Tag: {action.objectTag}) at {action.timestamp.ToShortTimeString()}");
                    }
                }

                sw.WriteLine("\n=====================================");
                sw.WriteLine("End of Report.");

                actionsTaken.Clear();

                isReportExported = true;
            }

            EditorUtility.DisplayDialog("Report Exported", $"Bug report successfully saved to:\nAssets/{ReportFolderName}/{fileName}", "OK");
            bugReportStatus = $"Report exported successfully to: Assets/{ReportFolderName}/{fileName}";
            Repaint();
        }
        catch (System.Exception ex)
        {
            bugReportStatus = $"Report Export Failed: {ex.Message}";
            Repaint();
        }
    }

    [System.Serializable]
    private class GameObjectData
    {
        public string objectName;
        public string objectTag;
        public string objectType;
        public string colliderType;
        public bool isMeshColliderConvex;
        public bool isKinematic;
        public bool useGravity;
        public float mass;
        public float drag;
        public float angularDrag;
        public float objectSize;
        public string objectPath;

        public List<string> componentNames;

        public ScanSettingsData scanSettings;
    }

    [System.Serializable]
    private class DataWrapper
    {
        public string projectName;
        public List<GameObjectData> objects;
        public List<string> selectedTags;

        public DataWrapper(List<GameObjectData> list, string name, List<string> tags)
        {
            objects = list;
            projectName = name;
            selectedTags = tags;
        }
    }

    [System.Serializable]
    private class ScanSettingsData
    {
        public bool scanForMissingRigidbody;
        public bool scanForKinematicGravity;
        public bool scanForMassAnomaly;
        public bool scanForDragAnomaly;
        public bool scanForAngularDragAnomaly;
        public bool scanForMeshConvexCollider;
        public bool scanForMismatchCollider;
        public bool scanForMissingCollider;
    }

    [System.Serializable]
    private class BugReport
    {
        public string objectName;
        public string objectPath;
        public string objectTag;
        public string bugType;
        public string suggestion;
        public string fixAction;
    }

    [System.Serializable]
    private class IgnoreSignalData
    {
        public string objectName;
        public string objectPath;
        public string objectTag;
        public string bugType;
        public string projectName;
    }

    private class ActionTakenReference
    {
        public string objectName;
        public string objectPath;
        public string bugType;
        public string objectTag;
        public string actionType;
        public System.DateTime timestamp;
    }

}