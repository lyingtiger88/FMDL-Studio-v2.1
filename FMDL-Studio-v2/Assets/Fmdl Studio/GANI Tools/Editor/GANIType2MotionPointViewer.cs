using UnityEngine;
using UnityEditor;

using System;
using System.IO;
using System.Text;
using System.Collections.Generic;

/// <summary>
/// MGSV Type2 / Mini-GANI motion-point inspector for Unity 2018.3.
///
/// Read-only tool:
/// - Parses the verified 18-unit / 56-segment human Type2 layout.
/// - Decodes only the five Vector3 tracks we currently care about:
///     U01.S1 = central/pelvis position reference
///     U06.S1 = arm A position target
///     U08.S1 = arm B position target
///     U10.S0 = leg A position target
///     U12.S0 = leg B position target
/// - Draws them as SceneView markers/trails.
/// - Can optionally sample the generated rotation-only AnimationClip at the same time.
///
/// IMPORTANT:
/// Vector3 coordinates are not written into the skeleton.  This is a visualization/probe only.
/// The recommended first transform is:
///     relative = target - U01
///     Unity point = (-relative.x, relative.y, relative.z)
/// because U01 is approximately the model-space waist height and FMDL left/right X is mirrored
/// relative to the observed motion-point X convention.
/// </summary>
public class GANIType2MotionPointViewer : EditorWindow
{
    private const uint TrkNodeSignature = 0x4FBDAAEF;
    private const byte UnitFlagStatic = 0x04;
    private const byte SegmentVector3 = 3;
    private const byte SegmentVectorDiff = 6;

    private string ganiPath = "";
    private string trkPath = "";
    private GameObject skeletonRoot = null;
    private AnimationClip previewClip = null;

    private bool loaded = false;
    private string status = "Not loaded.";

    private TrkLayout layout = null;
    private MiniGani mini = null;
    private byte[] ganiData = null;

    private DecodedTrack pelvisTrack = null; // U01.S1
    private DecodedTrack armATrack = null;   // U06.S1
    private DecodedTrack armBTrack = null;   // U08.S1
    private DecodedTrack legATrack = null;   // U10.S0
    private DecodedTrack legBTrack = null;   // U12.S0

    private float frame = 0.0f;
    private bool playing = false;
    private double lastEditorTime = 0.0;
    private float playbackSpeed = 1.0f;

    private bool samplePreviewClip = true;
    private bool subtractPelvis = true;
    private bool drawTrails = true;
    private bool drawReferenceBones = true;
    private bool drawRawPelvisMarker = true;
    private float markerScale = 1.0f;

    private AxisPreset axisPreset = AxisPreset.FlipX;

    private Vector2 scroll;

    private enum AxisPreset
    {
        Raw_XYZ = 0,
        FlipX = 1,
        FlipZ = 2,
        FlipXZ = 3,
        SwapXZ = 4,
        SwapXZ_FlipX = 5,
        SwapXZ_FlipZ = 6,
        SwapXZ_FlipXZ = 7
    }

    [MenuItem("FMDL Studio/GANI Tools/Type2 Motion Point Viewer")]
    public static void ShowWindow()
    {
        GANIType2MotionPointViewer window =
            GetWindow<GANIType2MotionPointViewer>("GANI Motion Points");

        window.minSize = new Vector2(820, 680);
    }

    private void OnEnable()
    {
        SceneView.onSceneGUIDelegate += OnSceneGUI;
        EditorApplication.update += EditorUpdate;
        lastEditorTime = EditorApplication.timeSinceStartup;
    }

    private void OnDisable()
    {
        SceneView.onSceneGUIDelegate -= OnSceneGUI;
        EditorApplication.update -= EditorUpdate;
        StopPreview();
    }

    private void OnGUI()
    {
        GUILayout.Space(10);

        GUILayout.Label(
            "MGSV Type2 / Mini-GANI - Motion Point Viewer",
            EditorStyles.boldLabel
        );

        EditorGUILayout.HelpBox(
            "This does NOT apply Vector3 tracks to bones. It only decodes and draws the position tracks in SceneView so we can verify their coordinate space before implementing IK/root motion.",
            MessageType.Info
        );

        GUILayout.Space(8);

        DrawFileRow("GANI", ref ganiPath, "gani");
        DrawFileRow("TRK", ref trkPath, "trk");

        GUILayout.Space(8);

        skeletonRoot = (GameObject)EditorGUILayout.ObjectField(
            "Skeleton Root",
            skeletonRoot,
            typeof(GameObject),
            true
        );

        if (GUILayout.Button("Use Selected GameObject", GUILayout.Height(28)))
        {
            if (Selection.activeGameObject != null)
            {
                skeletonRoot = Selection.activeGameObject;
                SceneView.RepaintAll();
            }
            else
            {
                EditorUtility.DisplayDialog(
                    "GANI Motion Points",
                    "Select the imported FMDL root GameObject in the Hierarchy first.",
                    "OK"
                );
            }
        }

        GUILayout.Space(8);

        previewClip = (AnimationClip)EditorGUILayout.ObjectField(
            "Rotation Preview Clip",
            previewClip,
            typeof(AnimationClip),
            false
        );

        if (GUILayout.Button("Use Selected AnimationClip", GUILayout.Height(28)))
        {
            AnimationClip selected = Selection.activeObject as AnimationClip;

            if (selected != null)
            {
                previewClip = selected;
                SampleCurrentFrame();
            }
            else
            {
                EditorUtility.DisplayDialog(
                    "GANI Motion Points",
                    "Select the generated Full Body Rotation .anim in the Project window first.",
                    "OK"
                );
            }
        }

        samplePreviewClip = EditorGUILayout.ToggleLeft(
            "Sample Rotation Preview Clip together with the markers",
            samplePreviewClip
        );

        GUILayout.Space(10);

        if (GUILayout.Button("LOAD + DECODE MOTION POINTS", GUILayout.Height(38)))
        {
            LoadData();
        }

        EditorGUILayout.HelpBox(status, loaded ? MessageType.Info : MessageType.None);

        GUILayout.Space(10);

        EditorGUILayout.BeginVertical("box");
        GUILayout.Label("Coordinate probe", EditorStyles.boldLabel);

        subtractPelvis = EditorGUILayout.ToggleLeft(
            "Subtract U01 pelvis/central position (recommended)",
            subtractPelvis
        );

        axisPreset = (AxisPreset)EditorGUILayout.EnumPopup(
            "Axis preset",
            axisPreset
        );

        markerScale = EditorGUILayout.FloatField(
            "Vector scale",
            markerScale
        );

        if (markerScale <= 0.000001f)
        {
            markerScale = 1.0f;
        }

        drawTrails = EditorGUILayout.ToggleLeft("Draw motion trails", drawTrails);
        drawReferenceBones = EditorGUILayout.ToggleLeft("Draw lines to current hand/foot bones", drawReferenceBones);
        drawRawPelvisMarker = EditorGUILayout.ToggleLeft("Draw U01 raw/model-space pelvis marker", drawRawPelvisMarker);

        GUILayout.Space(5);
        EditorGUILayout.HelpBox(
            "First test: Subtract U01 = ON, Axis preset = FlipX, Vector scale = 1.  This converts model/ground-space targets into the imported waist-root space without writing anything to the skeleton.",
            MessageType.Warning
        );
        EditorGUILayout.EndVertical();

        GUILayout.Space(10);

        GUI.enabled = loaded;

        float maxFrame = loaded && mini != null
            ? (float)mini.FrameCount
            : 1.0f;

        float newFrame = EditorGUILayout.Slider(
            "GANI Frame",
            frame,
            0.0f,
            Mathf.Max(1.0f, maxFrame)
        );

        if (Mathf.Abs(newFrame - frame) > 0.0001f)
        {
            frame = newFrame;
            playing = false;
            SampleCurrentFrame();
        }

        playbackSpeed = EditorGUILayout.FloatField(
            "Playback Speed",
            playbackSpeed
        );

        if (playbackSpeed <= 0.0001f)
        {
            playbackSpeed = 1.0f;
        }

        EditorGUILayout.BeginHorizontal();

        if (!playing)
        {
            if (GUILayout.Button("PLAY LOOP", GUILayout.Height(34)))
            {
                playing = true;
                lastEditorTime = EditorApplication.timeSinceStartup;
                SampleCurrentFrame();
            }
        }
        else
        {
            if (GUILayout.Button("PAUSE", GUILayout.Height(34)))
            {
                playing = false;
            }
        }

        if (GUILayout.Button("STOP + RESTORE", GUILayout.Height(34)))
        {
            StopPreview();
        }

        EditorGUILayout.EndHorizontal();

        GUILayout.Space(6);

        EditorGUILayout.BeginHorizontal();
        if (GUILayout.Button("0%")) SetFramePercent(0.0f);
        if (GUILayout.Button("25%")) SetFramePercent(0.25f);
        if (GUILayout.Button("50%")) SetFramePercent(0.50f);
        if (GUILayout.Button("75%")) SetFramePercent(0.75f);
        if (GUILayout.Button("100%")) SetFramePercent(1.00f);
        EditorGUILayout.EndHorizontal();

        GUILayout.Space(8);

        if (GUILayout.Button("Save Current-Frame Motion Point Report", GUILayout.Height(32)))
        {
            SaveCurrentFrameReport();
        }

        GUI.enabled = true;

        GUILayout.Space(12);
        scroll = EditorGUILayout.BeginScrollView(scroll);
        EditorGUILayout.BeginVertical("box");
        GUILayout.Label("Markers", EditorStyles.boldLabel);
        GUILayout.Label("U01.S1  central/pelvis reference");
        GUILayout.Label("U06.S1  arm A target");
        GUILayout.Label("U08.S1  arm B target");
        GUILayout.Label("U10.S0  leg A target");
        GUILayout.Label("U12.S0  leg B target");
        GUILayout.Space(6);
        GUILayout.Label("Reference bones used only for distance lines:");
        GUILayout.Label("U06 -> SKL_013_LHAND | U08 -> SKL_023_RHAND");
        GUILayout.Label("U10 -> SKL_032_LFOOT | U12 -> SKL_042_RFOOT");
        GUILayout.Space(6);
        GUILayout.Label("Nothing here modifies the GANI/TRK/FMDL or generated .anim assets.");
        EditorGUILayout.EndVertical();
        EditorGUILayout.EndScrollView();
    }

    private void DrawFileRow(string label, ref string value, string extension)
    {
        EditorGUILayout.BeginHorizontal();
        value = EditorGUILayout.TextField(label, value);

        if (GUILayout.Button("Browse", GUILayout.Width(80)))
        {
            string initial = "";
            if (!string.IsNullOrEmpty(value))
            {
                try { initial = Path.GetDirectoryName(value); }
                catch { initial = ""; }
            }

            string chosen = EditorUtility.OpenFilePanel(
                "Select " + label,
                initial,
                extension
            );

            if (!string.IsNullOrEmpty(chosen))
            {
                value = chosen;
            }
        }

        EditorGUILayout.EndHorizontal();
    }

    private void LoadData()
    {
        try
        {
            if (string.IsNullOrEmpty(ganiPath) || !File.Exists(ganiPath))
            {
                throw new Exception("Select a valid .gani file.");
            }

            if (string.IsNullOrEmpty(trkPath) || !File.Exists(trkPath))
            {
                throw new Exception("Select a valid .trk file.");
            }

            byte[] trkData = File.ReadAllBytes(trkPath);
            ganiData = File.ReadAllBytes(ganiPath);

            layout = ParseTrk(trkData);
            mini = ParseMiniGani(ganiData, layout);
            ValidateHumanMotionPointLayout(layout, mini);

            pelvisTrack = DecodeSegment(ganiData, mini, layout.Units[1].Segments[1]);
            armATrack = DecodeSegment(ganiData, mini, layout.Units[6].Segments[1]);
            armBTrack = DecodeSegment(ganiData, mini, layout.Units[8].Segments[1]);
            legATrack = DecodeSegment(ganiData, mini, layout.Units[10].Segments[0]);
            legBTrack = DecodeSegment(ganiData, mini, layout.Units[12].Segments[0]);

            loaded = true;
            frame = 0.0f;
            playing = false;

            status =
                "Loaded successfully. FrameCount=" + mini.FrameCount +
                ", Units=" + layout.UnitCount +
                ", Segments=" + layout.SegmentCount +
                ". Vector tracks: U01/U06/U08/U10/U12 decoded.";

            SampleCurrentFrame();
            SceneView.RepaintAll();
            Repaint();
        }
        catch (Exception ex)
        {
            loaded = false;
            status = "Load failed: " + ex.Message;
            UnityEngine.Debug.LogError("GANI Motion Point Viewer load failed:\n" + ex);
            EditorUtility.DisplayDialog("GANI Motion Point Viewer Error", ex.Message, "OK");
        }
    }

    private void EditorUpdate()
    {
        double now = EditorApplication.timeSinceStartup;
        double delta = now - lastEditorTime;
        lastEditorTime = now;

        if (!playing || !loaded || mini == null)
        {
            return;
        }

        // The builders currently use 30fps preview clips.  Motion markers themselves are driven by GANI frames.
        frame += (float)(delta * 30.0 * playbackSpeed);

        float max = (float)mini.FrameCount;
        while (frame > max && max > 0.0f)
        {
            frame -= max;
        }

        SampleCurrentFrame();
        Repaint();
        SceneView.RepaintAll();
    }

    private void SetFramePercent(float percent)
    {
        if (!loaded || mini == null)
        {
            return;
        }

        playing = false;
        frame = (float)mini.FrameCount * Mathf.Clamp01(percent);
        SampleCurrentFrame();
        Repaint();
        SceneView.RepaintAll();
    }

    private void SampleCurrentFrame()
    {
        if (!loaded)
        {
            return;
        }

        if (samplePreviewClip && skeletonRoot != null && previewClip != null)
        {
            try
            {
                if (!AnimationMode.InAnimationMode())
                {
                    AnimationMode.StartAnimationMode();
                }

                float normalized = mini != null && mini.FrameCount > 0
                    ? Mathf.Clamp01(frame / (float)mini.FrameCount)
                    : 0.0f;

                float clipTime = previewClip.length * normalized;

                AnimationMode.BeginSampling();
                AnimationMode.SampleAnimationClip(
                    skeletonRoot,
                    previewClip,
                    Mathf.Clamp(clipTime, 0.0f, previewClip.length)
                );
                AnimationMode.EndSampling();
            }
            catch (Exception ex)
            {
                try { AnimationMode.EndSampling(); }
                catch { }

                playing = false;
                UnityEngine.Debug.LogError("Motion-point clip preview failed:\n" + ex);
            }
        }

        SceneView.RepaintAll();
    }

    private void StopPreview()
    {
        playing = false;
        frame = 0.0f;

        if (AnimationMode.InAnimationMode())
        {
            AnimationMode.StopAnimationMode();
        }

        SceneView.RepaintAll();
        Repaint();
    }

    private void OnSceneGUI(SceneView sceneView)
    {
        if (!loaded || skeletonRoot == null || mini == null)
        {
            return;
        }

        try
        {
            DrawMotionPoint(
                "U01 PELVIS REF",
                pelvisTrack,
                null,
                new Color(1.0f, 0.85f, 0.15f, 1.0f),
                true
            );

            DrawMotionPoint(
                "U06 ARM A",
                armATrack,
                FindBone("SKL_013_LHAND"),
                new Color(0.0f, 0.9f, 1.0f, 1.0f),
                false
            );

            DrawMotionPoint(
                "U08 ARM B",
                armBTrack,
                FindBone("SKL_023_RHAND"),
                new Color(1.0f, 0.2f, 0.9f, 1.0f),
                false
            );

            DrawMotionPoint(
                "U10 LEG A",
                legATrack,
                FindBone("SKL_032_LFOOT"),
                new Color(0.2f, 1.0f, 0.25f, 1.0f),
                false
            );

            DrawMotionPoint(
                "U12 LEG B",
                legBTrack,
                FindBone("SKL_042_RFOOT"),
                new Color(1.0f, 0.35f, 0.15f, 1.0f),
                false
            );
        }
        catch (Exception ex)
        {
            Handles.Label(
                skeletonRoot.transform.position,
                "GANI Motion Point draw error: " + ex.Message
            );
        }
    }

    private void DrawMotionPoint(
        string label,
        DecodedTrack track,
        Transform referenceBone,
        Color color,
        bool isPelvis)
    {
        if (track == null || track.Keys.Count == 0)
        {
            return;
        }

        Vector3 raw = EvaluateVector3(track, frame);
        Vector3 local;

        if (isPelvis && drawRawPelvisMarker)
        {
            local = ConvertAxis(raw) * markerScale;
        }
        else
        {
            Vector3 origin = Vector3.zero;
            if (subtractPelvis && pelvisTrack != null)
            {
                origin = EvaluateVector3(pelvisTrack, frame);
            }

            local = ConvertAxis(raw - origin) * markerScale;
        }

        Vector3 world = skeletonRoot.transform.TransformPoint(local);

        Handles.color = color;
        float size = HandleUtility.GetHandleSize(world) * 0.07f;
        Handles.SphereHandleCap(0, world, Quaternion.identity, size, EventType.Repaint);

        string labelText = label + "\nlocal=" + FormatVector(local);

        if (referenceBone != null && drawReferenceBones)
        {
            Handles.DrawLine(world, referenceBone.position);
            float distance = Vector3.Distance(world, referenceBone.position);
            labelText += "\ndist to " + referenceBone.name + "=" + distance.ToString("0.000") + "m";
        }

        Handles.Label(world + Vector3.up * size * 1.2f, labelText);

        if (drawTrails)
        {
            DrawTrackTrail(track, color, isPelvis);
        }
    }

    private void DrawTrackTrail(DecodedTrack track, Color color, bool isPelvis)
    {
        if (track == null || track.Keys.Count < 2)
        {
            return;
        }

        Vector3[] points = new Vector3[track.Keys.Count];

        for (int i = 0; i < track.Keys.Count; i++)
        {
            DecodedKey key = track.Keys[i];
            Vector3 raw = ToVector3(key.Values);
            Vector3 origin = Vector3.zero;

            if (!isPelvis && subtractPelvis && pelvisTrack != null)
            {
                origin = EvaluateVector3(pelvisTrack, (float)key.Frame);
            }

            Vector3 local = ConvertAxis(raw - origin) * markerScale;
            points[i] = skeletonRoot.transform.TransformPoint(local);
        }

        Color trailColor = color;
        trailColor.a = 0.65f;
        Handles.color = trailColor;
        Handles.DrawAAPolyLine(2.0f, points);
    }

    private Transform FindBone(string name)
    {
        if (skeletonRoot == null)
        {
            return null;
        }

        Transform[] all = skeletonRoot.GetComponentsInChildren<Transform>(true);
        for (int i = 0; i < all.Length; i++)
        {
            if (all[i].name == name)
            {
                return all[i];
            }
        }

        return null;
    }

    private Vector3 EvaluateVector3(DecodedTrack track, float targetFrame)
    {
        if (track == null || track.Keys.Count == 0)
        {
            return Vector3.zero;
        }

        if (targetFrame <= track.Keys[0].Frame)
        {
            return ToVector3(track.Keys[0].Values);
        }

        int last = track.Keys.Count - 1;
        if (targetFrame >= track.Keys[last].Frame)
        {
            return ToVector3(track.Keys[last].Values);
        }

        int lo = 0;
        int hi = last;

        while (hi - lo > 1)
        {
            int mid = (lo + hi) / 2;
            if (track.Keys[mid].Frame <= targetFrame)
            {
                lo = mid;
            }
            else
            {
                hi = mid;
            }
        }

        DecodedKey a = track.Keys[lo];
        DecodedKey b = track.Keys[hi];

        float denom = (float)(b.Frame - a.Frame);
        float t = denom > 0.0f
            ? Mathf.Clamp01((targetFrame - (float)a.Frame) / denom)
            : 0.0f;

        return Vector3.Lerp(
            ToVector3(a.Values),
            ToVector3(b.Values),
            t
        );
    }

    private Vector3 ToVector3(double[] values)
    {
        if (values == null || values.Length != 3)
        {
            throw new Exception("Expected a Vector3 decoded value.");
        }

        return new Vector3(
            (float)values[0],
            (float)values[1],
            (float)values[2]
        );
    }

    private Vector3 ConvertAxis(Vector3 v)
    {
        switch (axisPreset)
        {
            case AxisPreset.Raw_XYZ:
                return new Vector3(v.x, v.y, v.z);

            case AxisPreset.FlipX:
                return new Vector3(-v.x, v.y, v.z);

            case AxisPreset.FlipZ:
                return new Vector3(v.x, v.y, -v.z);

            case AxisPreset.FlipXZ:
                return new Vector3(-v.x, v.y, -v.z);

            case AxisPreset.SwapXZ:
                return new Vector3(v.z, v.y, v.x);

            case AxisPreset.SwapXZ_FlipX:
                return new Vector3(-v.z, v.y, v.x);

            case AxisPreset.SwapXZ_FlipZ:
                return new Vector3(v.z, v.y, -v.x);

            case AxisPreset.SwapXZ_FlipXZ:
                return new Vector3(-v.z, v.y, -v.x);
        }

        return v;
    }

    private string FormatVector(Vector3 v)
    {
        return "(" +
            v.x.ToString("0.000") + ", " +
            v.y.ToString("0.000") + ", " +
            v.z.ToString("0.000") + ")";
    }

    private void SaveCurrentFrameReport()
    {
        if (!loaded || mini == null)
        {
            return;
        }

        string output = EditorUtility.SaveFilePanel(
            "Save Motion Point Report",
            !string.IsNullOrEmpty(ganiPath) ? Path.GetDirectoryName(ganiPath) : "",
            Path.GetFileNameWithoutExtension(ganiPath) + "_motionpoints_frame_" + Mathf.RoundToInt(frame),
            "txt"
        );

        if (string.IsNullOrEmpty(output))
        {
            return;
        }

        StringBuilder sb = new StringBuilder();
        sb.AppendLine("MGSV TYPE2 MOTION POINT FRAME REPORT");
        sb.AppendLine("============================================================");
        sb.AppendLine("GANI: " + ganiPath);
        sb.AppendLine("TRK : " + trkPath);
        sb.AppendLine("Frame: " + frame.ToString("0.000") + " / " + mini.FrameCount);
        sb.AppendLine("Subtract U01: " + subtractPelvis);
        sb.AppendLine("Axis preset : " + axisPreset);
        sb.AppendLine("Vector scale: " + markerScale.ToString("0.######"));
        sb.AppendLine("Skeleton: " + (skeletonRoot != null ? skeletonRoot.name : "<none>"));
        sb.AppendLine("Preview clip: " + (previewClip != null ? previewClip.name : "<none>"));
        sb.AppendLine();

        AppendTrackReport(sb, "U01 PELVIS REF", pelvisTrack, null, true);
        AppendTrackReport(sb, "U06 ARM A", armATrack, FindBone("SKL_013_LHAND"), false);
        AppendTrackReport(sb, "U08 ARM B", armBTrack, FindBone("SKL_023_RHAND"), false);
        AppendTrackReport(sb, "U10 LEG A", legATrack, FindBone("SKL_032_LFOOT"), false);
        AppendTrackReport(sb, "U12 LEG B", legBTrack, FindBone("SKL_042_RFOOT"), false);

        File.WriteAllText(output, sb.ToString(), Encoding.UTF8);
        UnityEngine.Debug.Log("GANI motion-point report written to:\n" + output);
        EditorUtility.DisplayDialog("GANI Motion Points", "Report saved.\n\n" + output, "OK");
    }

    private void AppendTrackReport(
        StringBuilder sb,
        string label,
        DecodedTrack track,
        Transform reference,
        bool isPelvis)
    {
        Vector3 raw = EvaluateVector3(track, frame);
        Vector3 origin = Vector3.zero;

        if (!isPelvis && subtractPelvis && pelvisTrack != null)
        {
            origin = EvaluateVector3(pelvisTrack, frame);
        }

        Vector3 relative = raw - origin;
        Vector3 converted = ConvertAxis(relative) * markerScale;

        sb.AppendLine(label);
        sb.AppendLine("  raw       = " + FormatVector(raw));
        sb.AppendLine("  origin    = " + FormatVector(origin));
        sb.AppendLine("  relative  = " + FormatVector(relative));
        sb.AppendLine("  converted = " + FormatVector(converted));

        if (skeletonRoot != null)
        {
            Vector3 world = skeletonRoot.transform.TransformPoint(converted);
            sb.AppendLine("  world     = " + FormatVector(world));

            if (reference != null)
            {
                Vector3 refLocal = skeletonRoot.transform.InverseTransformPoint(reference.position);
                sb.AppendLine("  bone      = " + reference.name);
                sb.AppendLine("  boneLocal = " + FormatVector(refLocal));
                sb.AppendLine("  distance  = " + Vector3.Distance(world, reference.position).ToString("0.000000"));
            }
        }

        sb.AppendLine();
    }

    private void ValidateHumanMotionPointLayout(TrkLayout l, MiniGani g)
    {
        if (l.FrameCount != g.FrameCount)
        {
            throw new Exception(
                "TRK/GANI FrameCount mismatch. TRK=" + l.FrameCount +
                ", GANI=" + g.FrameCount
            );
        }

        if (l.UnitCount != 18 || l.SegmentCount != 56)
        {
            throw new Exception(
                "This viewer currently supports the verified 18-unit / 56-segment human Type2 layout only. Found Units=" +
                l.UnitCount + ", Segments=" + l.SegmentCount + "."
            );
        }

        RequireVector3(l.Units[1].Segments[1], "U01.S1");
        RequireVector3(l.Units[6].Segments[1], "U06.S1");
        RequireVector3(l.Units[8].Segments[1], "U08.S1");
        RequireVector3(l.Units[10].Segments[0], "U10.S0");
        RequireVector3(l.Units[12].Segments[0], "U12.S0");
    }

    private void RequireVector3(TrkSegment segment, string label)
    {
        if (segment.Type != SegmentVector3 && segment.Type != SegmentVectorDiff)
        {
            throw new Exception(
                label + " is type " + segment.Type +
                ", expected Vector3/VectorDiff. Refusing to visualize a mismatched layout."
            );
        }
    }

    private TrkLayout ParseTrk(byte[] data)
    {
        if (data == null || data.Length < 0x24)
        {
            throw new Exception("TRK file is too small.");
        }

        int baseOffset = 0;
        uint signature = ReadUInt32(data, 0);
        if (signature == TrkNodeSignature)
        {
            baseOffset = 0x10;
        }

        TrkLayout result = new TrkLayout();
        result.NodeSignature = signature;
        result.TrackHeaderOffset = baseOffset;
        result.UnitCount = ReadInt32(data, baseOffset + 0x00);
        result.SegmentCount = (int)ReadUInt32(data, baseOffset + 0x04);
        result.FrameCount = ReadUInt32(data, baseOffset + 0x0C);

        if (result.UnitCount <= 0 || result.UnitCount > 2048)
        {
            throw new Exception("Invalid TRK UnitCount: " + result.UnitCount);
        }

        if (result.SegmentCount <= 0 || result.SegmentCount > 8192)
        {
            throw new Exception("Invalid TRK SegmentCount: " + result.SegmentCount);
        }

        int unitOffsetTable = baseOffset + 0x14;
        if (unitOffsetTable + result.UnitCount * 4 > data.Length)
        {
            throw new Exception("TRK unit-offset table is truncated.");
        }

        result.Units = new List<TrkUnit>();
        result.SegmentsById = new TrkSegment[result.SegmentCount];

        for (int i = 0; i < result.UnitCount; i++)
        {
            uint relative = ReadUInt32(data, unitOffsetTable + i * 4);
            int unitPos = baseOffset + (int)relative;

            if (unitPos < 0 || unitPos + 8 > data.Length)
            {
                throw new Exception("TRK unit " + i + " points outside the file.");
            }

            TrkUnit unit = new TrkUnit();
            unit.Index = i;
            unit.NameHash = ReadUInt32(data, unitPos + 0x00);
            unit.SegmentCount = data[unitPos + 0x04];
            unit.Flags = data[unitPos + 0x05];
            unit.Segments = new List<TrkSegment>();

            int segmentPos = unitPos + 8;
            for (int j = 0; j < unit.SegmentCount; j++)
            {
                if (segmentPos + 8 > data.Length)
                {
                    throw new Exception("TRK segment table is truncated at unit " + i + ".");
                }

                TrkSegment segment = new TrkSegment();
                segment.UnitIndex = i;
                segment.Id = ReadInt16(data, segmentPos + 0x04);

                byte typeAndNext = data[segmentPos + 0x06];
                segment.Type = (byte)(typeAndNext & 0x0F);
                segment.ComponentBitSize = data[segmentPos + 0x07];

                unit.Segments.Add(segment);

                if (segment.Id < 0 || segment.Id >= result.SegmentCount)
                {
                    throw new Exception("TRK segment ID outside layout: " + segment.Id);
                }

                result.SegmentsById[segment.Id] = segment;
                segmentPos += 8;
            }

            result.Units.Add(unit);
        }

        for (int i = 0; i < result.SegmentsById.Length; i++)
        {
            if (result.SegmentsById[i] == null)
            {
                throw new Exception("TRK is missing segment ID " + i + ".");
            }
        }

        return result;
    }

    private MiniGani ParseMiniGani(byte[] data, TrkLayout l)
    {
        if (data == null || data.Length < 8)
        {
            throw new Exception("GANI file is too small.");
        }

        MiniGani result = new MiniGani();
        result.FrameCount = ReadUInt32(data, 0x00);
        result.ParamCount = data[0x05];

        int position = 8 + result.ParamCount * 8;

        if (position + l.UnitCount > data.Length)
        {
            throw new Exception("GANI UnitFlags are truncated.");
        }

        result.UnitFlags = new byte[l.UnitCount];
        for (int i = 0; i < l.UnitCount; i++)
        {
            result.UnitFlags[i] = data[position + i];
        }

        position += l.UnitCount;
        position = Align(position, 4);

        result.SegmentHeaders = new GaniSegmentHeader[l.SegmentCount];

        if (position + l.SegmentCount * 4 > data.Length)
        {
            throw new Exception("GANI segment-header table is truncated.");
        }

        for (int i = 0; i < l.SegmentCount; i++)
        {
            int headerPos = position + i * 4;
            uint packed = ReadUInt32(data, headerPos);

            GaniSegmentHeader header = new GaniSegmentHeader();
            header.Id = i;
            header.Position = headerPos;
            header.ComponentBitSize = (byte)(packed & 0xFF);
            header.DataOffset = (packed >> 8) & 0x00FFFFFF;
            header.DataAddress = header.DataOffset != 0
                ? header.Position + (int)header.DataOffset
                : -1;

            if (header.DataAddress >= data.Length)
            {
                throw new Exception("GANI segment " + i + " points outside the file.");
            }

            result.SegmentHeaders[i] = header;
        }

        return result;
    }

    private DecodedTrack DecodeSegment(byte[] data, MiniGani g, TrkSegment trkSegment)
    {
        GaniSegmentHeader header = g.SegmentHeaders[trkSegment.Id];

        if (header.DataAddress < 0)
        {
            throw new Exception("Segment S" + trkSegment.Id + " has no GANI data address.");
        }

        if (header.ComponentBitSize != trkSegment.ComponentBitSize)
        {
            throw new Exception(
                "Bit-size mismatch on S" + trkSegment.Id +
                ": TRK=" + trkSegment.ComponentBitSize +
                ", GANI=" + header.ComponentBitSize
            );
        }

        byte staticFlag = (byte)(g.UnitFlags[trkSegment.UnitIndex] & UnitFlagStatic);

        return DecodeVectorTrack(
            data,
            header.DataAddress,
            trkSegment.Type,
            header.ComponentBitSize,
            staticFlag,
            g.FrameCount
        );
    }

    private DecodedTrack DecodeVectorTrack(
        byte[] data,
        int address,
        byte type,
        byte componentBitSize,
        byte staticFlag,
        uint totalFrameCount)
    {
        if (type != SegmentVector3 && type != SegmentVectorDiff)
        {
            throw new Exception("Motion-point viewer only decodes Vector3/VectorDiff tracks.");
        }

        if (componentBitSize != 16 && componentBitSize != 32)
        {
            throw new Exception("Unsupported Vector3 component bit-size: " + componentBitSize);
        }

        BitReader reader = new BitReader(data, address * 8);
        DecodedTrack result = new DecodedTrack();

        double[] first = ReadVector3(reader, componentBitSize);
        result.Keys.Add(new DecodedKey(0, first));

        bool isStatic = staticFlag != 0;
        if (isStatic)
        {
            result.Keys.Add(new DecodedKey((int)totalFrameCount, first));
            return result;
        }

        int frameIndex = 0;
        int safety = 0;

        do
        {
            int frameStep = (int)reader.ReadBits(8);
            frameIndex += frameStep;
            double[] value = ReadVector3(reader, componentBitSize);
            result.Keys.Add(new DecodedKey(frameIndex, value));

            safety++;
            if (safety > 100000)
            {
                throw new Exception("Vector track key loop exceeded safety limit.");
            }

            if (frameStep == 0 && frameIndex < (int)totalFrameCount)
            {
                throw new Exception("Zero frame step before FrameCount.");
            }
        }
        while (frameIndex < (int)totalFrameCount);

        return result;
    }

    private double[] ReadVector3(BitReader reader, byte bitSize)
    {
        if (reader.BitPosition % 8 != 0)
        {
            throw new Exception("Vector scalar is not byte-aligned at bit " + reader.BitPosition);
        }

        return new double[]
        {
            ReadScalar(reader, bitSize),
            ReadScalar(reader, bitSize),
            ReadScalar(reader, bitSize)
        };
    }

    private double ReadScalar(BitReader reader, byte bitSize)
    {
        if (bitSize == 16)
        {
            return ReadAnimHalf((ushort)reader.ReadBits(16));
        }

        if (bitSize == 32)
        {
            return UInt32ToSingle(reader.ReadBits(32));
        }

        throw new Exception("Unsupported scalar bit-size: " + bitSize);
    }

    private double ReadAnimHalf(ushort value)
    {
        uint num1 = (uint)(value & 0x7C00);
        if (num1 > 0)
        {
            num1 = (num1 + 0x1DC00) << 13;
        }

        num1 |=
            ((uint)(value & 0x8000) << 16) |
            ((uint)(value & 0x03FF) << 13);

        return UInt32ToSingle(num1);
    }

    private float UInt32ToSingle(uint value)
    {
        byte[] bytes = BitConverter.GetBytes(value);
        return BitConverter.ToSingle(bytes, 0);
    }

    private int Align(int value, int alignment)
    {
        return (value + alignment - 1) & ~(alignment - 1);
    }

    private uint ReadUInt32(byte[] data, int offset)
    {
        return
            (uint)data[offset] |
            ((uint)data[offset + 1] << 8) |
            ((uint)data[offset + 2] << 16) |
            ((uint)data[offset + 3] << 24);
    }

    private int ReadInt32(byte[] data, int offset)
    {
        return unchecked((int)ReadUInt32(data, offset));
    }

    private short ReadInt16(byte[] data, int offset)
    {
        return unchecked((short)(
            data[offset] |
            (data[offset + 1] << 8)
        ));
    }

    private class BitReader
    {
        private byte[] data;
        public int BitPosition;

        public BitReader(byte[] source, int bitPosition)
        {
            data = source;
            BitPosition = bitPosition;
        }

        public uint ReadBits(int count)
        {
            if (count < 0 || count > 32)
            {
                throw new ArgumentOutOfRangeException("count");
            }

            if (BitPosition + count > data.Length * 8)
            {
                throw new EndOfStreamException("Bitstream read outside GANI at bit " + BitPosition);
            }

            uint value = 0;
            for (int i = 0; i < count; i++)
            {
                int byteIndex = BitPosition >> 3;
                int bitIndex = BitPosition & 7;
                uint bit = (uint)((data[byteIndex] >> bitIndex) & 1);
                value |= bit << i;
                BitPosition++;
            }

            return value;
        }
    }

    private class TrkLayout
    {
        public uint NodeSignature;
        public int TrackHeaderOffset;
        public int UnitCount;
        public int SegmentCount;
        public uint FrameCount;
        public List<TrkUnit> Units;
        public TrkSegment[] SegmentsById;
    }

    private class TrkUnit
    {
        public int Index;
        public uint NameHash;
        public byte SegmentCount;
        public byte Flags;
        public List<TrkSegment> Segments;
    }

    private class TrkSegment
    {
        public int UnitIndex;
        public short Id;
        public byte Type;
        public byte ComponentBitSize;
    }

    private class MiniGani
    {
        public uint FrameCount;
        public byte ParamCount;
        public byte[] UnitFlags;
        public GaniSegmentHeader[] SegmentHeaders;
    }

    private class GaniSegmentHeader
    {
        public int Id;
        public int Position;
        public byte ComponentBitSize;
        public uint DataOffset;
        public int DataAddress;
    }

    private class DecodedTrack
    {
        public List<DecodedKey> Keys = new List<DecodedKey>();
    }

    private class DecodedKey
    {
        public int Frame;
        public double[] Values;

        public DecodedKey(int frameValue, double[] valuesValue)
        {
            Frame = frameValue;
            Values = valuesValue;
        }
    }
}
