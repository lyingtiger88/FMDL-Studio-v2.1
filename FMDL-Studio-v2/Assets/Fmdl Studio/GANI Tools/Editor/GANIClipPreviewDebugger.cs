using UnityEngine;
using UnityEditor;
using System;
using System.Text;

/// <summary>
/// Unity 2018.3 compatible read-only AnimationClip preview/debug tool.
/// It does not modify the AnimationClip or the model.
/// It samples the clip directly in AnimationMode, so no Animator Controller is required.
/// </summary>
public class GANIClipPreviewDebugger : EditorWindow
{
    private GameObject root;
    private AnimationClip clip;
    private float previewTime = 0.0f;
    private bool playing = false;
    private double lastEditorTime = 0.0;
    private Vector2 scroll;
    private string diagnostics = "";

    [MenuItem("FMDL Studio/GANI Tools/Animation Clip Preview Debugger")]
    public static void ShowWindow()
    {
        GANIClipPreviewDebugger window =
            GetWindow<GANIClipPreviewDebugger>("GANI Clip Preview");

        window.minSize = new Vector2(760, 600);
    }

    private void OnEnable()
    {
        EditorApplication.update += EditorUpdate;
        lastEditorTime = EditorApplication.timeSinceStartup;
    }

    private void OnDisable()
    {
        EditorApplication.update -= EditorUpdate;
        StopPreview();
    }

    private void OnGUI()
    {
        GUILayout.Space(10);
        GUILayout.Label("GANI AnimationClip Preview Debugger", EditorStyles.boldLabel);

        EditorGUILayout.HelpBox(
            "This tool directly samples a Unity AnimationClip on the selected imported FMDL root. " +
            "No Animator Controller or Animation component is required. Source assets are not modified.",
            MessageType.Info
        );

        GUILayout.Space(8);

        root = (GameObject)EditorGUILayout.ObjectField(
            "FMDL Root",
            root,
            typeof(GameObject),
            true
        );

        EditorGUILayout.BeginHorizontal();
        if (GUILayout.Button("Use Selected GameObject", GUILayout.Height(28)))
        {
            if (Selection.activeGameObject != null)
            {
                root = Selection.activeGameObject;
                diagnostics = "";
            }
            else
            {
                EditorUtility.DisplayDialog(
                    "GANI Clip Preview",
                    "Select the imported FMDL root GameObject in the Hierarchy first.",
                    "OK"
                );
            }
        }

        if (GUILayout.Button("Use Parent Root", GUILayout.Height(28)))
        {
            if (root != null && root.transform.parent != null)
            {
                root = root.transform.parent.gameObject;
                diagnostics = "";
            }
        }
        EditorGUILayout.EndHorizontal();

        GUILayout.Space(8);

        clip = (AnimationClip)EditorGUILayout.ObjectField(
            "AnimationClip",
            clip,
            typeof(AnimationClip),
            false
        );

        if (GUILayout.Button("Use Selected AnimationClip", GUILayout.Height(28)))
        {
            AnimationClip selectedClip = Selection.activeObject as AnimationClip;

            if (selectedClip != null)
            {
                clip = selectedClip;
                previewTime = 0.0f;
                diagnostics = "";
            }
            else
            {
                EditorUtility.DisplayDialog(
                    "GANI Clip Preview",
                    "Select the generated .anim asset in the Project window first.",
                    "OK"
                );
            }
        }

        GUILayout.Space(10);

        if (clip != null)
        {
            EditorGUILayout.LabelField("Clip Length", clip.length.ToString("0.000") + " sec");
            EditorGUILayout.LabelField("Frame Rate", clip.frameRate.ToString("0.###"));

            float maxTime = Mathf.Max(0.0001f, clip.length);

            float newTime = EditorGUILayout.Slider(
                "Preview Time",
                previewTime,
                0.0f,
                maxTime
            );

            if (Mathf.Abs(newTime - previewTime) > 0.000001f)
            {
                previewTime = newTime;
                if (root != null)
                {
                    SampleCurrentTime();
                }
            }
        }

        GUILayout.Space(10);

        EditorGUILayout.BeginHorizontal();

        GUI.enabled = root != null && clip != null;

        if (GUILayout.Button("Sample 0%", GUILayout.Height(32)))
        {
            playing = false;
            previewTime = 0.0f;
            SampleCurrentTime();
        }

        if (GUILayout.Button("Sample 25%", GUILayout.Height(32)))
        {
            playing = false;
            previewTime = clip.length * 0.25f;
            SampleCurrentTime();
        }

        if (GUILayout.Button("Sample 50%", GUILayout.Height(32)))
        {
            playing = false;
            previewTime = clip.length * 0.50f;
            SampleCurrentTime();
        }

        if (GUILayout.Button("Sample 75%", GUILayout.Height(32)))
        {
            playing = false;
            previewTime = clip.length * 0.75f;
            SampleCurrentTime();
        }

        GUI.enabled = true;
        EditorGUILayout.EndHorizontal();

        GUILayout.Space(6);

        EditorGUILayout.BeginHorizontal();
        GUI.enabled = root != null && clip != null;

        if (!playing)
        {
            if (GUILayout.Button("PLAY LOOP", GUILayout.Height(38)))
            {
                StartPlay();
            }
        }
        else
        {
            if (GUILayout.Button("PAUSE", GUILayout.Height(38)))
            {
                playing = false;
            }
        }

        if (GUILayout.Button("STOP + RESTORE", GUILayout.Height(38)))
        {
            StopPreview();
        }

        GUI.enabled = true;
        EditorGUILayout.EndHorizontal();

        GUILayout.Space(12);

        if (GUILayout.Button("Analyze Clip Bindings", GUILayout.Height(36)))
        {
            AnalyzeBindings();
        }

        GUILayout.Space(10);

        scroll = EditorGUILayout.BeginScrollView(scroll);
        EditorGUILayout.TextArea(
            string.IsNullOrEmpty(diagnostics)
                ? "Click 'Analyze Clip Bindings' to verify whether the generated curves resolve under the chosen FMDL root."
                : diagnostics,
            GUILayout.ExpandHeight(true)
        );
        EditorGUILayout.EndScrollView();
    }

    private void StartPlay()
    {
        if (!ValidatePreviewInputs())
        {
            return;
        }

        playing = true;
        lastEditorTime = EditorApplication.timeSinceStartup;
        SampleCurrentTime();
    }

    private void EditorUpdate()
    {
        double now = EditorApplication.timeSinceStartup;
        double delta = now - lastEditorTime;
        lastEditorTime = now;

        if (!playing || root == null || clip == null)
        {
            return;
        }

        float length = clip.length;

        if (length <= 0.000001f)
        {
            playing = false;
            return;
        }

        previewTime += (float)delta;

        while (previewTime > length)
        {
            previewTime -= length;
        }

        SampleCurrentTime();
        Repaint();
    }

    private bool ValidatePreviewInputs()
    {
        if (root == null)
        {
            EditorUtility.DisplayDialog(
                "GANI Clip Preview",
                "Select the imported FMDL root GameObject.",
                "OK"
            );
            return false;
        }

        if (clip == null)
        {
            EditorUtility.DisplayDialog(
                "GANI Clip Preview",
                "Select the generated .anim AnimationClip.",
                "OK"
            );
            return false;
        }

        return true;
    }

    private void SampleCurrentTime()
    {
        if (!ValidatePreviewInputs())
        {
            playing = false;
            return;
        }

        try
        {
            if (!AnimationMode.InAnimationMode())
            {
                AnimationMode.StartAnimationMode();
            }

            AnimationMode.BeginSampling();
            AnimationMode.SampleAnimationClip(
                root,
                clip,
                Mathf.Clamp(previewTime, 0.0f, clip.length)
            );
            AnimationMode.EndSampling();

            SceneView.RepaintAll();
        }
        catch (Exception ex)
        {
            try
            {
                AnimationMode.EndSampling();
            }
            catch
            {
            }

            playing = false;
            UnityEngine.Debug.LogError("GANI clip preview failed:\n" + ex);

            EditorUtility.DisplayDialog(
                "GANI Clip Preview Error",
                ex.Message,
                "OK"
            );
        }
    }

    private void StopPreview()
    {
        playing = false;
        previewTime = 0.0f;

        if (AnimationMode.InAnimationMode())
        {
            AnimationMode.StopAnimationMode();
        }

        SceneView.RepaintAll();
        Repaint();
    }

    private void AnalyzeBindings()
    {
        if (!ValidatePreviewInputs())
        {
            return;
        }

        EditorCurveBinding[] bindings =
            AnimationUtility.GetCurveBindings(clip);

        StringBuilder sb = new StringBuilder();

        sb.AppendLine("GANI ANIMATION CLIP BINDING DIAGNOSTICS");
        sb.AppendLine("============================================================");
        sb.AppendLine("Root : " + GetHierarchyPath(root.transform));
        sb.AppendLine("Clip : " + clip.name);
        sb.AppendLine("Length: " + clip.length.ToString("0.000000") + " sec");
        sb.AppendLine("FrameRate: " + clip.frameRate.ToString("0.###"));
        sb.AppendLine("Curve bindings: " + bindings.Length);
        sb.AppendLine();

        int transformBindings = 0;
        int resolvedBindings = 0;
        int unresolvedBindings = 0;
        int varyingCurves = 0;
        int constantCurves = 0;

        for (int i = 0; i < bindings.Length; i++)
        {
            EditorCurveBinding binding = bindings[i];

            if (binding.type != typeof(Transform))
            {
                continue;
            }

            transformBindings++;

            Transform target = ResolvePath(root.transform, binding.path);
            bool resolved = target != null;

            if (resolved)
            {
                resolvedBindings++;
            }
            else
            {
                unresolvedBindings++;
            }

            AnimationCurve curve = AnimationUtility.GetEditorCurve(clip, binding);
            bool varying = CurveVaries(curve);

            if (varying)
            {
                varyingCurves++;
            }
            else
            {
                constantCurves++;
            }

            sb.Append("[");
            sb.Append(i.ToString("D3"));
            sb.Append("] ");
            sb.Append(resolved ? "OK  " : "MISS");
            sb.Append(" | ");
            sb.Append(varying ? "VARY " : "CONST");
            sb.Append(" | path='");
            sb.Append(binding.path);
            sb.Append("' | ");
            sb.Append(binding.propertyName);

            if (curve != null)
            {
                sb.Append(" | keys=");
                sb.Append(curve.length);

                if (curve.length > 0)
                {
                    float first = curve.keys[0].value;
                    float mid = curve.Evaluate(clip.length * 0.5f);
                    float last = curve.keys[curve.length - 1].value;

                    sb.Append(" | first=");
                    sb.Append(first.ToString("G7"));
                    sb.Append(" mid=");
                    sb.Append(mid.ToString("G7"));
                    sb.Append(" last=");
                    sb.Append(last.ToString("G7"));
                }
            }

            sb.AppendLine();
        }

        sb.AppendLine();
        sb.AppendLine("SUMMARY");
        sb.AppendLine("-------");
        sb.AppendLine("Transform bindings : " + transformBindings);
        sb.AppendLine("Resolved bindings  : " + resolvedBindings);
        sb.AppendLine("Unresolved bindings: " + unresolvedBindings);
        sb.AppendLine("Varying curves     : " + varyingCurves);
        sb.AppendLine("Constant curves    : " + constantCurves);
        sb.AppendLine();

        if (bindings.Length == 0)
        {
            sb.AppendLine("RESULT: The .anim contains no curves. The builder failed before/while binding curves.");
        }
        else if (unresolvedBindings > 0 && resolvedBindings == 0)
        {
            sb.AppendLine("RESULT: The clip has curves, but NONE of its transform paths resolve under this root.");
            sb.AppendLine("Most likely the clip was built relative to a different hierarchy root.");
        }
        else if (unresolvedBindings > 0)
        {
            sb.AppendLine("RESULT: Some paths resolve and some do not. Root selection/path mapping needs correction.");
        }
        else if (varyingCurves == 0)
        {
            sb.AppendLine("RESULT: All paths resolve, but every curve is constant. The decoded/mapped tracks contain no visible motion in this clip.");
        }
        else
        {
            sb.AppendLine("RESULT: Paths resolve and the clip contains varying curves. Direct preview should visibly animate at least the mapped bones.");
        }

        diagnostics = sb.ToString();
        UnityEngine.Debug.Log(diagnostics);
    }

    private bool CurveVaries(AnimationCurve curve)
    {
        if (curve == null || curve.length <= 1)
        {
            return false;
        }

        float min = curve.keys[0].value;
        float max = min;

        for (int i = 1; i < curve.length; i++)
        {
            float v = curve.keys[i].value;
            if (v < min) min = v;
            if (v > max) max = v;
        }

        return Mathf.Abs(max - min) > 0.000001f;
    }

    private Transform ResolvePath(Transform rootTransform, string path)
    {
        if (rootTransform == null)
        {
            return null;
        }

        if (string.IsNullOrEmpty(path))
        {
            return rootTransform;
        }

        return rootTransform.Find(path);
    }

    private string GetHierarchyPath(Transform t)
    {
        if (t == null)
        {
            return "<null>";
        }

        string path = t.name;
        Transform p = t.parent;

        while (p != null)
        {
            path = p.name + "/" + path;
            p = p.parent;
        }

        return path;
    }
}
