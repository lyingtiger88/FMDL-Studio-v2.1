using UnityEngine;
using UnityEditor;

using System;
using System.IO;
using System.Text;
using System.Collections.Generic;

/// <summary>
/// MGSV Type2/Mini-GANI full-body rotation AnimationClip builder.
/// Unity 2018.3 compatible editor tool.
///
/// CONFIRMED / CONSERVATIVE BASE:
///   U01 -> SKL_000_WAIST rotation (+ optional translation, still experimental)
///   U02 -> SKL_001_SPINE rotation
///   U03 -> SKL_002_CHEST rotation
///   U04 -> SKL_003_NECK rotation
///   U05 -> SKL_004_HEAD rotation
///   U16 -> 16 left-finger rotations
///   U17 -> 16 right-finger rotations
///
/// ROTATION-ONLY LIMB MAPPING (all four groups observed to animate independently):
///   U06.S0 -> SKL_011_LUARM, U07.S0 -> SKL_012_LFARM, U06.S2 -> SKL_013_LHAND
///   U08.S0 -> SKL_021_RUARM, U09.S0 -> SKL_022_RFARM, U08.S2 -> SKL_023_RHAND
///   U10.S1 -> SKL_030_LTHIGH, U11.S0 -> SKL_031_LLEG, U14.S0 -> SKL_032_LFOOT
///   U12.S1 -> SKL_040_RTHIGH, U13.S0 -> SKL_041_RLEG, U15.S0 -> SKL_042_RFOOT
///
/// U06/U08/U10/U12 Vector3 tracks are NOT applied because their coordinate space is not verified.
/// U00 root-diff is NOT applied. All limb groups default ON, but remain individually switchable for debugging.
/// Source FMDL/GANI/TRK files are never modified.
/// </summary>
public class GANIType2FullBodyRotationAxisFix : EditorWindow
{
    private const uint TrkNodeSignature = 0x4FBDAAEF;

    private const byte UnitFlagStatic = 0x04;

    private const byte SegmentQuat = 0;
    private const byte SegmentFloat = 1;
    private const byte SegmentVector2 = 2;
    private const byte SegmentVector3 = 3;
    private const byte SegmentVector4 = 4;
    private const byte SegmentQuatDiff = 5;
    private const byte SegmentVectorDiff = 6;

    private string ganiPath = "";
    private string trkPath = "";
    private GameObject skeletonRoot = null;

    private bool includeWaistTranslation = false;
    private bool convertFoxToUnityFlipX = true;

    // Experimental rotation-only limb groups. Default OFF on purpose.
    private bool includeLeftArm = true;
    private bool includeRightArm = true;
    private bool includeLeftLeg = true;
    private bool includeRightLeg = true;

    private float sampleRate = 30.0f;

    private Vector2 scroll;

    private static readonly string[] LeftFingerBones =
    {
        "SKL_101_LF10",
        "SKL_102_LF11",
        "SKL_103_LF12",
        "SKL_104_LF21",
        "SKL_105_LF22",
        "SKL_106_LF23",
        "SKL_107_LF31",
        "SKL_108_LF32",
        "SKL_109_LF33",
        "SKL_110_LF40",
        "SKL_111_LF41",
        "SKL_112_LF42",
        "SKL_113_LF43",
        "SKL_114_LF51",
        "SKL_115_LF52",
        "SKL_116_LF53"
    };

    private static readonly string[] RightFingerBones =
    {
        "SKL_201_RF10",
        "SKL_202_RF11",
        "SKL_203_RF12",
        "SKL_204_RF21",
        "SKL_205_RF22",
        "SKL_206_RF23",
        "SKL_207_RF31",
        "SKL_208_RF32",
        "SKL_209_RF33",
        "SKL_210_RF40",
        "SKL_211_RF41",
        "SKL_212_RF42",
        "SKL_213_RF43",
        "SKL_214_RF51",
        "SKL_215_RF52",
        "SKL_216_RF53"
    };

    [MenuItem("FMDL Studio/GANI Tools/Type2 Full Body Rotation - Axis Fix")]
    public static void ShowWindow()
    {
        GANIType2FullBodyRotationAxisFix window =
            GetWindow<GANIType2FullBodyRotationAxisFix>("GANI Full Body Axis Fix");

        window.minSize = new Vector2(760, 600);
    }

    private void OnGUI()
    {
        GUILayout.Space(10);

        GUILayout.Label(
            "MGSV Type2 / Mini-GANI - Full Body Rotation Axis Fix",
            EditorStyles.boldLabel
        );

        EditorGUILayout.HelpBox(
            "This build fixes a coordinate-basis bug in the previous rotation builder. " +
            "Fox-space vectors were tested with X mirrored, but Fox-space quaternions were written to Unity unchanged. " +
            "With Flip X, a quaternion must be converted as (x, -y, -z, w). Root-diff and limb Vector3/IK tracks still remain ignored.",
            MessageType.Info
        );

        GUILayout.Space(8);

        DrawFileRow(
            "GANI",
            ref ganiPath,
            "gani"
        );

        DrawFileRow(
            "TRK",
            ref trkPath,
            "trk"
        );

        GUILayout.Space(8);

        skeletonRoot = (GameObject)EditorGUILayout.ObjectField(
            "Skeleton Root",
            skeletonRoot,
            typeof(GameObject),
            true
        );

        if (GUILayout.Button(
            "Use Selected GameObject",
            GUILayout.Height(28)))
        {
            if (Selection.activeGameObject != null)
            {
                skeletonRoot = Selection.activeGameObject;
            }
            else
            {
                EditorUtility.DisplayDialog(
                    "GANI Full Body Rotation",
                    "Select the imported FMDL root GameObject in the Hierarchy first.",
                    "OK"
                );
            }
        }

        GUILayout.Space(8);

        sampleRate = EditorGUILayout.FloatField(
            "Clip Sample Rate",
            sampleRate
        );

        if (sampleRate <= 0.0f)
        {
            sampleRate = 30.0f;
        }

        convertFoxToUnityFlipX = EditorGUILayout.ToggleLeft(
            "Convert Fox -> Unity basis (Flip X; quaternion = x,-y,-z,w)",
            convertFoxToUnityFlipX
        );

        EditorGUILayout.HelpBox(
            "IMPORTANT: the previous builders flipped X for motion-point positions but wrote Fox quaternions to Unity unchanged. " +
            "That mixes two coordinate bases. Keep this ON for this test.",
            MessageType.Warning
        );

        includeWaistTranslation = EditorGUILayout.ToggleLeft(
            "EXPERIMENTAL: apply U01 Vector3 to SKL_000_WAIST localPosition",
            includeWaistTranslation
        );

        if (includeWaistTranslation)
        {
            EditorGUILayout.HelpBox(
                "The translation space has NOT been fully verified. Leave this OFF for the first visual test.",
                MessageType.Warning
            );
        }

        GUILayout.Space(10);
        EditorGUILayout.BeginVertical("box");
        GUILayout.Label("Full-body limb rotations (rotation only)", EditorStyles.boldLabel);

        includeLeftArm = EditorGUILayout.ToggleLeft(
            "LEFT ARM: U06/U07 -> LUARM / LFARM / LHAND",
            includeLeftArm
        );

        includeRightArm = EditorGUILayout.ToggleLeft(
            "RIGHT ARM: U08/U09 -> RUARM / RFARM / RHAND",
            includeRightArm
        );

        includeLeftLeg = EditorGUILayout.ToggleLeft(
            "LEFT LEG: U10/U11/U14 -> LTHIGH / LLEG / LFOOT",
            includeLeftLeg
        );

        includeRightLeg = EditorGUILayout.ToggleLeft(
            "RIGHT LEG: U12/U13/U15 -> RTHIGH / RLEG / RFOOT",
            includeRightLeg
        );

        GUILayout.Space(6);

        EditorGUILayout.BeginHorizontal();
        if (GUILayout.Button("All Limbs OFF"))
        {
            includeLeftArm = false;
            includeRightArm = false;
            includeLeftLeg = false;
            includeRightLeg = false;
        }

        if (GUILayout.Button("Both Arms"))
        {
            includeLeftArm = true;
            includeRightArm = true;
            includeLeftLeg = false;
            includeRightLeg = false;
        }

        if (GUILayout.Button("Both Legs"))
        {
            includeLeftArm = false;
            includeRightArm = false;
            includeLeftLeg = true;
            includeRightLeg = true;
        }
        EditorGUILayout.EndHorizontal();

        if (GUILayout.Button("FULL BODY ROTATION (all four limb groups)", GUILayout.Height(26)))
        {
            includeLeftArm = true;
            includeRightArm = true;
            includeLeftLeg = true;
            includeRightLeg = true;
        }

        EditorGUILayout.HelpBox(
            "All four limb groups are ON by default because each group has been visually confirmed to animate in isolation. " +
            "That confirms track activity, but not yet exact game-space correctness. Vector3 tracks in U06/U08/U10/U12 are intentionally ignored until their IK/motion-point space is verified.",
            MessageType.Warning
        );
        EditorGUILayout.EndVertical();

        GUILayout.Space(12);

        if (GUILayout.Button(
            "Build Full Body Axis-Fixed AnimationClip (.anim)",
            GUILayout.Height(42)))
        {
            BuildClip();
        }

        GUILayout.Space(16);

        scroll = EditorGUILayout.BeginScrollView(scroll);

        EditorGUILayout.BeginVertical("box");
        GUILayout.Label("Core mapping", EditorStyles.boldLabel);
        GUILayout.Label("U01 rotation  -> SKL_000_WAIST");
        GUILayout.Label("U02 rotation  -> SKL_001_SPINE");
        GUILayout.Label("U03 rotation  -> SKL_002_CHEST");
        GUILayout.Label("U04 rotation  -> SKL_003_NECK");
        GUILayout.Label("U05 rotation  -> SKL_004_HEAD");
        GUILayout.Label("U16 segments  -> 16 left finger bones");
        GUILayout.Label("U17 segments  -> 16 right finger bones");
        GUILayout.Space(8);
        GUILayout.Label("Limb rotation mapping", EditorStyles.boldLabel);
        GUILayout.Label("U06.S0 -> SKL_011_LUARM | U07.S0 -> SKL_012_LFARM | U06.S2 -> SKL_013_LHAND");
        GUILayout.Label("U08.S0 -> SKL_021_RUARM | U09.S0 -> SKL_022_RFARM | U08.S2 -> SKL_023_RHAND");
        GUILayout.Label("U10.S1 -> SKL_030_LTHIGH | U11.S0 -> SKL_031_LLEG | U14.S0 -> SKL_032_LFOOT");
        GUILayout.Label("U12.S1 -> SKL_040_RTHIGH | U13.S0 -> SKL_041_RLEG | U15.S0 -> SKL_042_RFOOT");
        GUILayout.Space(6);
        GUILayout.Label("Still intentionally ignored:", EditorStyles.boldLabel);
        GUILayout.Label("U00 root QUAT_DIFF / VECTOR_DIFF");
        GUILayout.Label("U06/U08/U10/U12 Vector3 tracks (unverified IK/motion-point space)");
        EditorGUILayout.EndVertical();

        EditorGUILayout.EndScrollView();
    }

    private void DrawFileRow(
        string label,
        ref string value,
        string extension)
    {
        EditorGUILayout.BeginHorizontal();

        value = EditorGUILayout.TextField(
            label,
            value
        );

        if (GUILayout.Button("Browse", GUILayout.Width(80)))
        {
            string initialDir = "";

            if (!string.IsNullOrEmpty(value))
            {
                try
                {
                    initialDir = Path.GetDirectoryName(value);
                }
                catch
                {
                    initialDir = "";
                }
            }

            string selected = EditorUtility.OpenFilePanel(
                "Select " + label,
                initialDir,
                extension
            );

            if (!string.IsNullOrEmpty(selected))
            {
                value = selected;
            }
        }

        EditorGUILayout.EndHorizontal();
    }

    private void BuildClip()
    {
        if (!ValidateInputs())
        {
            return;
        }

        try
        {
            byte[] trkData = File.ReadAllBytes(trkPath);
            byte[] ganiData = File.ReadAllBytes(ganiPath);

            TrkLayout layout = ParseTrk(trkData);
            MiniGani mini = ParseMiniGani(ganiData, layout);

            ValidateKnownHumanLayout(layout, mini);

            Dictionary<string, Transform> bones =
                BuildBoneMap(skeletonRoot.transform);

            ValidateRequiredBones(bones);

            string defaultName =
                Path.GetFileNameWithoutExtension(ganiPath) +
                "_fullbody_axisfix.anim";

            string assetPath =
                EditorUtility.SaveFilePanelInProject(
                    "Save full-body rotation GANI AnimationClip",
                    defaultName,
                    "anim",
                    "Choose a location inside this Unity project's Assets folder."
                );

            if (string.IsNullOrEmpty(assetPath))
            {
                return;
            }

            AnimationClip clip = new AnimationClip();
            clip.name = Path.GetFileNameWithoutExtension(assetPath);
            clip.frameRate = sampleRate;
            clip.wrapMode = WrapMode.Default;

            StringBuilder report = new StringBuilder();
            report.AppendLine("MGSV TYPE2 FULL BODY ROTATION CLIP BUILD");
            report.AppendLine("============================================================");
            report.AppendLine("GANI: " + ganiPath);
            report.AppendLine("TRK : " + trkPath);
            report.AppendLine("Root: " + skeletonRoot.name);
            report.AppendLine("FrameCount: " + mini.FrameCount);
            report.AppendLine("SampleRate: " + sampleRate);
            report.AppendLine("Length seconds: " + ((double)mini.FrameCount / (double)sampleRate).ToString("G9"));
            report.AppendLine("Waist translation enabled: " + includeWaistTranslation);
            report.AppendLine("Left arm enabled : " + includeLeftArm);
            report.AppendLine("Right arm enabled: " + includeRightArm);
            report.AppendLine("Left leg enabled : " + includeLeftLeg);
            report.AppendLine("Right leg enabled: " + includeRightLeg);
            report.AppendLine();

            // Torso / head.
            ApplyUnitRotation(
                clip,
                ganiData,
                layout,
                mini,
                1,
                0,
                bones["SKL_000_WAIST"],
                "U01 rotation -> SKL_000_WAIST",
                report
            );

            if (includeWaistTranslation)
            {
                ApplyUnitVector3(
                    clip,
                    ganiData,
                    layout,
                    mini,
                    1,
                    1,
                    bones["SKL_000_WAIST"],
                    "U01 translation -> SKL_000_WAIST",
                    report
                );
            }

            ApplyUnitRotation(
                clip,
                ganiData,
                layout,
                mini,
                2,
                0,
                bones["SKL_001_SPINE"],
                "U02 -> SKL_001_SPINE",
                report
            );

            ApplyUnitRotation(
                clip,
                ganiData,
                layout,
                mini,
                3,
                0,
                bones["SKL_002_CHEST"],
                "U03 -> SKL_002_CHEST",
                report
            );

            ApplyUnitRotation(
                clip,
                ganiData,
                layout,
                mini,
                4,
                0,
                bones["SKL_003_NECK"],
                "U04 -> SKL_003_NECK",
                report
            );

            ApplyUnitRotation(
                clip,
                ganiData,
                layout,
                mini,
                5,
                0,
                bones["SKL_004_HEAD"],
                "U05 -> SKL_004_HEAD",
                report
            );

            report.AppendLine();
            report.AppendLine("LIMB ROTATIONS");
            report.AppendLine("------------------------------------------------------------");

            if (includeLeftArm)
            {
                ApplyUnitRotation(clip, ganiData, layout, mini, 6, 0, bones["SKL_011_LUARM"], "U06.S0 -> SKL_011_LUARM", report);
                ApplyUnitRotation(clip, ganiData, layout, mini, 7, 0, bones["SKL_012_LFARM"], "U07.S0 -> SKL_012_LFARM", report);
                ApplyUnitRotation(clip, ganiData, layout, mini, 6, 2, bones["SKL_013_LHAND"], "U06.S2 -> SKL_013_LHAND", report);
            }
            else
            {
                report.AppendLine("LEFT ARM: disabled");
            }

            if (includeRightArm)
            {
                ApplyUnitRotation(clip, ganiData, layout, mini, 8, 0, bones["SKL_021_RUARM"], "U08.S0 -> SKL_021_RUARM", report);
                ApplyUnitRotation(clip, ganiData, layout, mini, 9, 0, bones["SKL_022_RFARM"], "U09.S0 -> SKL_022_RFARM", report);
                ApplyUnitRotation(clip, ganiData, layout, mini, 8, 2, bones["SKL_023_RHAND"], "U08.S2 -> SKL_023_RHAND", report);
            }
            else
            {
                report.AppendLine("RIGHT ARM: disabled");
            }

            if (includeLeftLeg)
            {
                ApplyUnitRotation(clip, ganiData, layout, mini, 10, 1, bones["SKL_030_LTHIGH"], "U10.S1 -> SKL_030_LTHIGH", report);
                ApplyUnitRotation(clip, ganiData, layout, mini, 11, 0, bones["SKL_031_LLEG"], "U11.S0 -> SKL_031_LLEG", report);
                ApplyUnitRotation(clip, ganiData, layout, mini, 14, 0, bones["SKL_032_LFOOT"], "U14.S0 -> SKL_032_LFOOT", report);
            }
            else
            {
                report.AppendLine("LEFT LEG: disabled");
            }

            if (includeRightLeg)
            {
                ApplyUnitRotation(clip, ganiData, layout, mini, 12, 1, bones["SKL_040_RTHIGH"], "U12.S1 -> SKL_040_RTHIGH", report);
                ApplyUnitRotation(clip, ganiData, layout, mini, 13, 0, bones["SKL_041_RLEG"], "U13.S0 -> SKL_041_RLEG", report);
                ApplyUnitRotation(clip, ganiData, layout, mini, 15, 0, bones["SKL_042_RFOOT"], "U15.S0 -> SKL_042_RFOOT", report);
            }
            else
            {
                report.AppendLine("RIGHT LEG: disabled");
            }

            // Fingers.
            ApplyFingerUnit(
                clip,
                ganiData,
                layout,
                mini,
                16,
                LeftFingerBones,
                bones,
                "LEFT",
                report
            );

            ApplyFingerUnit(
                clip,
                ganiData,
                layout,
                mini,
                17,
                RightFingerBones,
                bones,
                "RIGHT",
                report
            );

            clip.EnsureQuaternionContinuity();

            AssetDatabase.CreateAsset(
                clip,
                assetPath
            );

            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();

            WriteBuildReport(
                assetPath,
                report.ToString()
            );

            Selection.activeObject = clip;
            EditorGUIUtility.PingObject(clip);

            UnityEngine.Debug.Log(
                "GANI full-body rotation AnimationClip created:\n" +
                assetPath +
                "\n\n" +
                report.ToString()
            );

            EditorUtility.DisplayDialog(
                "GANI Full Body Rotation",
                "Full-body rotation AnimationClip created successfully.\n\n" +
                assetPath +
                "\n\n" +
                "Base mapping: WAIST/SPINE/CHEST/NECK/HEAD + both hands' finger tracks.\n" +
                "Limb rotations were added for the enabled groups (all four are ON by default).\n" +
                "Root motion and limb Vector3/IK coordinates remain untouched.",
                "OK"
            );
        }
        catch (Exception ex)
        {
            UnityEngine.Debug.LogError(
                "GANI Full Body Rotation build failed:\n" +
                ex
            );

            EditorUtility.DisplayDialog(
                "GANI Full Body Rotation Error",
                ex.Message,
                "OK"
            );
        }
    }

    private bool ValidateInputs()
    {
        if (string.IsNullOrEmpty(ganiPath) ||
            !File.Exists(ganiPath))
        {
            EditorUtility.DisplayDialog(
                "GANI Full Body Rotation",
                "Select a valid .gani file.",
                "OK"
            );
            return false;
        }

        if (string.IsNullOrEmpty(trkPath) ||
            !File.Exists(trkPath))
        {
            EditorUtility.DisplayDialog(
                "GANI Full Body Rotation",
                "Select a valid .trk file.",
                "OK"
            );
            return false;
        }

        if (skeletonRoot == null)
        {
            EditorUtility.DisplayDialog(
                "GANI Full Body Rotation",
                "Select the imported FMDL root GameObject.",
                "OK"
            );
            return false;
        }

        return true;
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

        if (baseOffset + 0x14 > data.Length)
        {
            throw new Exception("TRK TrackHeader is truncated.");
        }

        TrkLayout layout = new TrkLayout();

        layout.NodeSignature = signature;
        layout.TrackHeaderOffset = baseOffset;
        layout.UnitCount = ReadInt32(data, baseOffset + 0x00);
        layout.SegmentCount = (int)ReadUInt32(data, baseOffset + 0x04);
        layout.Id = ReadUInt16(data, baseOffset + 0x08);
        layout.UnknownA = data[baseOffset + 0x0A];
        layout.UnknownB = data[baseOffset + 0x0B];
        layout.FrameCount = ReadUInt32(data, baseOffset + 0x0C);
        layout.FrameRate = data[baseOffset + 0x10];

        if (layout.UnitCount <= 0 || layout.UnitCount > 2048)
        {
            throw new Exception(
                "Invalid TRK UnitCount: " +
                layout.UnitCount
            );
        }

        if (layout.SegmentCount <= 0 || layout.SegmentCount > 8192)
        {
            throw new Exception(
                "Invalid TRK SegmentCount: " +
                layout.SegmentCount
            );
        }

        int unitOffsetTable =
            baseOffset + 0x14;

        if (unitOffsetTable +
            layout.UnitCount * 4 >
            data.Length)
        {
            throw new Exception(
                "TRK UnitOffsets table is truncated."
            );
        }

        layout.Units =
            new List<TrkUnit>();

        layout.SegmentsById =
            new TrkSegment[layout.SegmentCount];

        for (int i = 0;
             i < layout.UnitCount;
             i++)
        {
            uint relative =
                ReadUInt32(
                    data,
                    unitOffsetTable + i * 4
                );

            int unitPosition =
                baseOffset + (int)relative;

            if (unitPosition < 0 ||
                unitPosition + 8 > data.Length)
            {
                throw new Exception(
                    "TRK unit " + i +
                    " points outside the file."
                );
            }

            TrkUnit unit =
                new TrkUnit();

            unit.Index = i;
            unit.Position = unitPosition;
            unit.NameHash =
                ReadUInt32(data, unitPosition + 0x00);
            unit.SegmentCount =
                data[unitPosition + 0x04];
            unit.Flags =
                data[unitPosition + 0x05];
            unit.Padding =
                ReadUInt16(data, unitPosition + 0x06);
            unit.Segments =
                new List<TrkSegment>();

            int segmentPosition =
                unitPosition + 8;

            for (int j = 0;
                 j < unit.SegmentCount;
                 j++)
            {
                if (segmentPosition + 8 > data.Length)
                {
                    throw new Exception(
                        "TRK segment table is truncated at unit " +
                        i + "."
                    );
                }

                TrkSegment segment =
                    new TrkSegment();

                segment.UnitIndex = i;
                segment.UnitNameHash = unit.NameHash;
                segment.Position = segmentPosition;
                segment.DataOffset =
                    ReadInt32(
                        data,
                        segmentPosition + 0x00
                    );

                segment.Id =
                    ReadInt16(
                        data,
                        segmentPosition + 0x04
                    );

                byte typeAndNext =
                    data[segmentPosition + 0x06];

                segment.Type =
                    (byte)(typeAndNext & 0x0F);

                segment.NextEntryOffset =
                    (byte)((typeAndNext >> 4) & 0x0F);

                segment.ComponentBitSize =
                    data[segmentPosition + 0x07];

                unit.Segments.Add(segment);

                if (segment.Id >= 0 &&
                    segment.Id < layout.SegmentCount)
                {
                    if (layout.SegmentsById[
                        segment.Id] != null)
                    {
                        throw new Exception(
                            "Duplicate TRK segment ID: " +
                            segment.Id
                        );
                    }

                    layout.SegmentsById[
                        segment.Id] = segment;
                }

                segmentPosition += 8;
            }

            layout.Units.Add(unit);
        }

        for (int i = 0;
             i < layout.SegmentsById.Length;
             i++)
        {
            if (layout.SegmentsById[i] == null)
            {
                throw new Exception(
                    "TRK is missing segment ID " +
                    i + "."
                );
            }
        }

        return layout;
    }

    private MiniGani ParseMiniGani(
        byte[] data,
        TrkLayout layout)
    {
        if (data == null ||
            data.Length < 8)
        {
            throw new Exception(
                "GANI file is too small."
            );
        }

        MiniGani result =
            new MiniGani();

        result.FrameCount =
            ReadUInt32(data, 0x00);

        result.Padding0 =
            data[0x04];

        result.ParamCount =
            data[0x05];

        result.Padding1 =
            ReadUInt16(data, 0x06);

        result.Params =
            new List<GaniParam>();

        int position = 8;

        for (int i = 0;
             i < result.ParamCount;
             i++)
        {
            if (position + 8 > data.Length)
            {
                throw new Exception(
                    "GANI parameter table is truncated."
                );
            }

            GaniParam param =
                new GaniParam();

            param.NameHash =
                ReadUInt32(data, position);

            param.Value =
                ReadSingle(data, position + 4);

            result.Params.Add(param);

            position += 8;
        }

        if (position +
            layout.UnitCount >
            data.Length)
        {
            throw new Exception(
                "GANI UnitFlags are truncated."
            );
        }

        result.UnitFlags =
            new byte[layout.UnitCount];

        for (int i = 0;
             i < layout.UnitCount;
             i++)
        {
            result.UnitFlags[i] =
                data[position + i];
        }

        position += layout.UnitCount;
        position = Align(position, 4);

        result.SegmentHeaderStart =
            position;

        result.SegmentHeaders =
            new GaniSegmentHeader[
                layout.SegmentCount
            ];

        if (position +
            layout.SegmentCount * 4 >
            data.Length)
        {
            throw new Exception(
                "GANI segment-header table is truncated."
            );
        }

        for (int i = 0;
             i < layout.SegmentCount;
             i++)
        {
            int headerPosition =
                position + i * 4;

            uint packed =
                ReadUInt32(
                    data,
                    headerPosition
                );

            GaniSegmentHeader header =
                new GaniSegmentHeader();

            header.Id = i;
            header.Position =
                headerPosition;

            header.ComponentBitSize =
                (byte)(packed & 0xFF);

            header.DataOffset =
                (packed >> 8) &
                0x00FFFFFF;

            if (header.DataOffset != 0)
            {
                header.DataAddress =
                    header.Position +
                    (int)header.DataOffset;

                if (header.DataAddress < 0 ||
                    header.DataAddress >=
                    data.Length)
                {
                    throw new Exception(
                        "GANI segment " +
                        i +
                        " points outside the file."
                    );
                }
            }
            else
            {
                header.DataAddress = -1;
            }

            result.SegmentHeaders[i] =
                header;
        }

        return result;
    }

    private void ValidateKnownHumanLayout(
        TrkLayout layout,
        MiniGani mini)
    {
        if (layout.FrameCount !=
            mini.FrameCount)
        {
            throw new Exception(
                "TRK/GANI FrameCount mismatch. TRK=" +
                layout.FrameCount +
                ", GANI=" +
                mini.FrameCount
            );
        }

        if (layout.UnitCount != 18 ||
            layout.SegmentCount != 56)
        {
            throw new Exception(
                "This alpha builder currently supports the verified 18-unit / 56-segment human Type2 layout only.\n\n" +
                "Found UnitCount=" +
                layout.UnitCount +
                ", SegmentCount=" +
                layout.SegmentCount +
                "."
            );
        }

        if (layout.Units[1].SegmentCount < 2)
        {
            throw new Exception(
                "U01 does not contain the expected rotation + translation segments."
            );
        }

        for (int unit = 2;
             unit <= 5;
             unit++)
        {
            if (layout.Units[unit].SegmentCount < 1)
            {
                throw new Exception(
                    "U" +
                    unit.ToString("D2") +
                    " has no rotation segment."
                );
            }
        }

        // Verified segment structure for the experimental limb slots.
        if (layout.Units[6].SegmentCount != 3 ||
            layout.Units[7].SegmentCount != 1 ||
            layout.Units[8].SegmentCount != 3 ||
            layout.Units[9].SegmentCount != 1 ||
            layout.Units[10].SegmentCount != 2 ||
            layout.Units[11].SegmentCount != 1 ||
            layout.Units[12].SegmentCount != 2 ||
            layout.Units[13].SegmentCount != 1 ||
            layout.Units[14].SegmentCount != 1 ||
            layout.Units[15].SegmentCount != 1)
        {
            throw new Exception(
                "Experimental limb-unit structure does not match the verified 18-unit human layout."
            );
        }

        RequireType(layout.Units[6].Segments[0], SegmentQuat, "U06 segment 0");
        RequireType(layout.Units[6].Segments[1], SegmentVector3, "U06 segment 1");
        RequireType(layout.Units[6].Segments[2], SegmentQuat, "U06 segment 2");
        RequireType(layout.Units[7].Segments[0], SegmentQuat, "U07 segment 0");

        RequireType(layout.Units[8].Segments[0], SegmentQuat, "U08 segment 0");
        RequireType(layout.Units[8].Segments[1], SegmentVector3, "U08 segment 1");
        RequireType(layout.Units[8].Segments[2], SegmentQuat, "U08 segment 2");
        RequireType(layout.Units[9].Segments[0], SegmentQuat, "U09 segment 0");

        RequireType(layout.Units[10].Segments[0], SegmentVector3, "U10 segment 0");
        RequireType(layout.Units[10].Segments[1], SegmentQuat, "U10 segment 1");
        RequireType(layout.Units[11].Segments[0], SegmentQuat, "U11 segment 0");

        RequireType(layout.Units[12].Segments[0], SegmentVector3, "U12 segment 0");
        RequireType(layout.Units[12].Segments[1], SegmentQuat, "U12 segment 1");
        RequireType(layout.Units[13].Segments[0], SegmentQuat, "U13 segment 0");
        RequireType(layout.Units[14].Segments[0], SegmentQuat, "U14 segment 0");
        RequireType(layout.Units[15].Segments[0], SegmentQuat, "U15 segment 0");

        if (layout.Units[16].SegmentCount != 16 ||
            layout.Units[17].SegmentCount != 16)
        {
            throw new Exception(
                "Finger-unit structure does not match the verified layout. Expected U16=16 and U17=16 segments."
            );
        }

        // Validate known segment types before applying anything.
        RequireType(
            layout.Units[1].Segments[0],
            SegmentQuat,
            "U01 segment 0"
        );

        RequireType(
            layout.Units[1].Segments[1],
            SegmentVector3,
            "U01 segment 1"
        );

        for (int unit = 2;
             unit <= 5;
             unit++)
        {
            RequireType(
                layout.Units[unit].Segments[0],
                SegmentQuat,
                "U" +
                unit.ToString("D2") +
                " segment 0"
            );
        }

        for (int i = 0; i < 16; i++)
        {
            RequireType(
                layout.Units[16].Segments[i],
                SegmentQuat,
                "U16 segment " + i
            );

            RequireType(
                layout.Units[17].Segments[i],
                SegmentQuat,
                "U17 segment " + i
            );
        }
    }

    private void RequireType(
        TrkSegment segment,
        byte expected,
        string label)
    {
        if (segment.Type != expected)
        {
            throw new Exception(
                label +
                " has type " +
                segment.Type +
                ", expected " +
                expected +
                ". Refusing to build a possibly incorrect clip."
            );
        }
    }

    private Dictionary<string, Transform> BuildBoneMap(
        Transform root)
    {
        Dictionary<string, Transform> result =
            new Dictionary<string, Transform>(
                StringComparer.Ordinal
            );

        Transform[] all =
            root.GetComponentsInChildren<Transform>(
                true
            );

        for (int i = 0;
             i < all.Length;
             i++)
        {
            Transform t = all[i];

            if (!result.ContainsKey(t.name))
            {
                result.Add(t.name, t);
            }
        }

        return result;
    }

    private void ValidateRequiredBones(
        Dictionary<string, Transform> bones)
    {
        string[] body =
        {
            "SKL_000_WAIST",
            "SKL_001_SPINE",
            "SKL_002_CHEST",
            "SKL_003_NECK",
            "SKL_004_HEAD"
        };

        for (int i = 0;
             i < body.Length;
             i++)
        {
            RequireBone(
                bones,
                body[i]
            );
        }

        if (includeLeftArm)
        {
            RequireBone(bones, "SKL_011_LUARM");
            RequireBone(bones, "SKL_012_LFARM");
            RequireBone(bones, "SKL_013_LHAND");
        }

        if (includeRightArm)
        {
            RequireBone(bones, "SKL_021_RUARM");
            RequireBone(bones, "SKL_022_RFARM");
            RequireBone(bones, "SKL_023_RHAND");
        }

        if (includeLeftLeg)
        {
            RequireBone(bones, "SKL_030_LTHIGH");
            RequireBone(bones, "SKL_031_LLEG");
            RequireBone(bones, "SKL_032_LFOOT");
        }

        if (includeRightLeg)
        {
            RequireBone(bones, "SKL_040_RTHIGH");
            RequireBone(bones, "SKL_041_RLEG");
            RequireBone(bones, "SKL_042_RFOOT");
        }

        for (int i = 0;
             i < LeftFingerBones.Length;
             i++)
        {
            RequireBone(
                bones,
                LeftFingerBones[i]
            );
        }

        for (int i = 0;
             i < RightFingerBones.Length;
             i++)
        {
            RequireBone(
                bones,
                RightFingerBones[i]
            );
        }
    }

    private void RequireBone(
        Dictionary<string, Transform> bones,
        string name)
    {
        if (!bones.ContainsKey(name))
        {
            throw new Exception(
                "Required Transform not found under selected root: " +
                name
            );
        }
    }

    private void ApplyUnitRotation(
        AnimationClip clip,
        byte[] ganiData,
        TrkLayout layout,
        MiniGani mini,
        int unitIndex,
        int segmentIndex,
        Transform target,
        string label,
        StringBuilder report)
    {
        TrkSegment segment =
            layout.Units[unitIndex]
                .Segments[segmentIndex];

        DecodedTrack track =
            DecodeSegment(
                ganiData,
                mini,
                segment
            );

        ApplyQuaternionCurves(
            clip,
            target,
            track
        );

        report.AppendLine(
            label +
            " | segment=" +
            segment.Id +
            " keys=" +
            track.Keys.Count
        );
    }

    private void ApplyUnitVector3(
        AnimationClip clip,
        byte[] ganiData,
        TrkLayout layout,
        MiniGani mini,
        int unitIndex,
        int segmentIndex,
        Transform target,
        string label,
        StringBuilder report)
    {
        TrkSegment segment =
            layout.Units[unitIndex]
                .Segments[segmentIndex];

        DecodedTrack track =
            DecodeSegment(
                ganiData,
                mini,
                segment
            );

        ApplyVector3Curves(
            clip,
            target,
            track
        );

        report.AppendLine(
            label +
            " | segment=" +
            segment.Id +
            " keys=" +
            track.Keys.Count
        );
    }

    private void ApplyFingerUnit(
        AnimationClip clip,
        byte[] ganiData,
        TrkLayout layout,
        MiniGani mini,
        int unitIndex,
        string[] boneNames,
        Dictionary<string, Transform> bones,
        string side,
        StringBuilder report)
    {
        TrkUnit unit =
            layout.Units[unitIndex];

        if (unit.Segments.Count !=
            boneNames.Length)
        {
            throw new Exception(
                "Finger mapping count mismatch at U" +
                unitIndex.ToString("D2") +
                "."
            );
        }

        report.AppendLine();
        report.AppendLine(
            side + " FINGERS / U" +
            unitIndex.ToString("D2")
        );

        for (int i = 0;
             i < boneNames.Length;
             i++)
        {
            TrkSegment segment =
                unit.Segments[i];

            DecodedTrack track =
                DecodeSegment(
                    ganiData,
                    mini,
                    segment
                );

            Transform target =
                bones[boneNames[i]];

            ApplyQuaternionCurves(
                clip,
                target,
                track
            );

            report.AppendLine(
                "  " +
                i.ToString("D2") +
                " S" +
                segment.Id.ToString("D2") +
                " -> " +
                boneNames[i] +
                " keys=" +
                track.Keys.Count
            );
        }
    }

    private DecodedTrack DecodeSegment(
        byte[] ganiData,
        MiniGani mini,
        TrkSegment trkSegment)
    {
        if (trkSegment.Id < 0 ||
            trkSegment.Id >=
            mini.SegmentHeaders.Length)
        {
            throw new Exception(
                "TRK segment ID outside GANI header table: " +
                trkSegment.Id
            );
        }

        GaniSegmentHeader header =
            mini.SegmentHeaders[
                trkSegment.Id
            ];

        if (header.DataAddress < 0)
        {
            throw new Exception(
                "Segment S" +
                trkSegment.Id.ToString("D2") +
                " has no GANI data address."
            );
        }

        if (header.ComponentBitSize !=
            trkSegment.ComponentBitSize)
        {
            throw new Exception(
                "Bit-size mismatch on S" +
                trkSegment.Id.ToString("D2") +
                ": TRK=" +
                trkSegment.ComponentBitSize +
                ", GANI=" +
                header.ComponentBitSize
            );
        }

        byte unitFlags =
            mini.UnitFlags[
                trkSegment.UnitIndex
            ];

        return DecodeTrackDataBlob(
            ganiData,
            header.DataAddress,
            trkSegment.Type,
            header.ComponentBitSize,
            (byte)(
                unitFlags &
                UnitFlagStatic
            ),
            mini.FrameCount
        );
    }

    private DecodedTrack DecodeTrackDataBlob(
        byte[] data,
        int address,
        byte type,
        byte componentBitSize,
        byte staticFlag,
        uint totalFrameCount)
    {
        BitReader reader =
            new BitReader(
                data,
                address * 8
            );

        DecodedTrack result =
            new DecodedTrack();

        result.Type = type;
        result.BitSize =
            componentBitSize;

        SegmentValue first =
            ReadSegmentValue(
                reader,
                type,
                componentBitSize
            );

        result.Keys.Add(
            new DecodedKey(
                0,
                first.Values
            )
        );

        bool isStatic =
            staticFlag != 0;

        if (!isStatic)
        {
            int frameIndex = 0;
            int safety = 0;

            do
            {
                int frameStep =
                    (int)reader.ReadBits(8);

                frameIndex +=
                    frameStep;

                SegmentValue value =
                    ReadSegmentValue(
                        reader,
                        type,
                        componentBitSize
                    );

                result.Keys.Add(
                    new DecodedKey(
                        frameIndex,
                        value.Values
                    )
                );

                safety++;

                if (safety > 100000)
                {
                    throw new Exception(
                        "TrackDataBlob key loop exceeded safety limit."
                    );
                }

                if (frameStep == 0 &&
                    frameIndex <
                    (int)totalFrameCount)
                {
                    throw new Exception(
                        "Zero frame step before FrameCount in segment data."
                    );
                }
            }
            while (frameIndex <
                   (int)totalFrameCount);
        }
        else
        {
            // Duplicate static key at clip end so Unity holds the value cleanly.
            result.Keys.Add(
                new DecodedKey(
                    (int)totalFrameCount,
                    first.Values
                )
            );
        }

        return result;
    }

    private SegmentValue ReadSegmentValue(
        BitReader reader,
        byte type,
        byte bits)
    {
        SegmentValue result =
            new SegmentValue();

        result.Type = type;

        switch (type)
        {
            case SegmentQuat:
            case SegmentQuatDiff:
                if (bits != 12 &&
                    bits != 13 &&
                    bits != 15)
                {
                    throw new Exception(
                        "Unsupported quaternion bit size: " +
                        bits
                    );
                }

                result.Values =
                    ReadCompressedQuaternion(
                        reader,
                        bits
                    );
                break;

            case SegmentFloat:
                result.Values =
                    new double[1];

                result.Values[0] =
                    ReadScalar(
                        reader,
                        bits
                    );
                break;

            case SegmentVector2:
                result.Values =
                    new double[2];

                result.Values[0] =
                    ReadScalar(
                        reader,
                        bits
                    );

                result.Values[1] =
                    ReadScalar(
                        reader,
                        bits
                    );
                break;

            case SegmentVector3:
            case SegmentVectorDiff:
                result.Values =
                    new double[3];

                result.Values[0] =
                    ReadScalar(
                        reader,
                        bits
                    );

                result.Values[1] =
                    ReadScalar(
                        reader,
                        bits
                    );

                result.Values[2] =
                    ReadScalar(
                        reader,
                        bits
                    );
                break;

            case SegmentVector4:
                result.Values =
                    new double[4];

                result.Values[0] =
                    ReadScalar(
                        reader,
                        bits
                    );

                result.Values[1] =
                    ReadScalar(
                        reader,
                        bits
                    );

                result.Values[2] =
                    ReadScalar(
                        reader,
                        bits
                    );

                result.Values[3] =
                    ReadScalar(
                        reader,
                        bits
                    );
                break;

            default:
                throw new Exception(
                    "Unsupported TRK segment type: " +
                    type
                );
        }

        return result;
    }

    private double[] ReadCompressedQuaternion(
        BitReader reader,
        int bitSize)
    {
        double divisor =
            Math.Pow(
                2.0,
                bitSize
            );

        double theta =
            (double)reader.ReadBits(bitSize) /
            divisor;

        double valueY =
            (double)reader.ReadBits(bitSize) /
            divisor;

        double valueZ =
            (double)reader.ReadBits(bitSize) /
            divisor;

        uint signX =
            reader.ReadBits(1);

        uint signY =
            reader.ReadBits(1);

        uint signZ =
            reader.ReadBits(1);

        double valueXBase =
            1.0 -
            valueY -
            valueZ;

        double denominator =
            Math.Sqrt(
                valueY * valueY +
                valueZ * valueZ +
                valueXBase * valueXBase
            );

        double scale = 0.0;

        if (denominator >
            0.0000000001)
        {
            scale =
                Math.Sin(theta) /
                denominator;
        }

        double x =
            valueY * scale;

        double y =
            valueZ * scale;

        double z =
            valueXBase * scale;

        if (signX != 0)
        {
            x = -x;
        }

        if (signY != 0)
        {
            y = -y;
        }

        if (signZ != 0)
        {
            z = -z;
        }

        double w =
            Math.Cos(theta);

        return new double[]
        {
            x,
            y,
            z,
            w
        };
    }

    private double ReadScalar(
        BitReader reader,
        byte bitSize)
    {
        if (reader.BitPosition % 8 != 0)
        {
            throw new Exception(
                "16/32-bit scalar is not byte-aligned at bit " +
                reader.BitPosition
            );
        }

        if (bitSize == 16)
        {
            ushort value =
                (ushort)reader.ReadBits(16);

            return ReadAnimHalf(value);
        }

        if (bitSize == 32)
        {
            uint value =
                reader.ReadBits(32);

            return UInt32ToSingle(value);
        }

        throw new Exception(
            "Unsupported scalar bit size: " +
            bitSize
        );
    }

    private double ReadAnimHalf(
        ushort value)
    {
        uint num1 =
            (uint)(value & 0x7C00);

        if (num1 > 0)
        {
            num1 =
                (num1 + 0x1DC00)
                << 13;
        }

        num1 |=
            ((uint)(value & 0x8000) << 16) |
            ((uint)(value & 0x03FF) << 13);

        return UInt32ToSingle(num1);
    }

    private float UInt32ToSingle(
        uint value)
    {
        byte[] bytes =
            BitConverter.GetBytes(value);

        return BitConverter.ToSingle(
            bytes,
            0
        );
    }

    private void ApplyQuaternionCurves(
        AnimationClip clip,
        Transform target,
        DecodedTrack track)
    {
        if (track.Keys.Count == 0)
        {
            return;
        }

        string path =
            AnimationUtility.CalculateTransformPath(
                target,
                skeletonRoot.transform
            );

        AnimationCurve x =
            new AnimationCurve();

        AnimationCurve y =
            new AnimationCurve();

        AnimationCurve z =
            new AnimationCurve();

        AnimationCurve w =
            new AnimationCurve();

        Quaternion previous =
            Quaternion.identity;

        bool havePrevious = false;

        for (int i = 0;
             i < track.Keys.Count;
             i++)
        {
            DecodedKey key =
                track.Keys[i];

            if (key.Values == null ||
                key.Values.Length != 4)
            {
                throw new Exception(
                    "Quaternion track has invalid value length."
                );
            }

            Quaternion q =
                new Quaternion(
                    (float)key.Values[0],
                    (float)key.Values[1],
                    (float)key.Values[2],
                    (float)key.Values[3]
                );

            q.Normalize();

            if (convertFoxToUnityFlipX)
            {
                // Basis reflection S = diag(-1, 1, 1).
                // For rotations: R_unity = S * R_fox * S.
                // Equivalent quaternion mapping: (x, y, z, w) -> (x, -y, -z, w).
                q = new Quaternion(
                    q.x,
                    -q.y,
                    -q.z,
                    q.w
                );
                q.Normalize();
            }

            if (havePrevious &&
                Quaternion.Dot(
                    previous,
                    q
                ) < 0.0f)
            {
                q.x = -q.x;
                q.y = -q.y;
                q.z = -q.z;
                q.w = -q.w;
            }

            previous = q;
            havePrevious = true;

            float time =
                (float)key.Frame /
                sampleRate;

            x.AddKey(
                new Keyframe(
                    time,
                    q.x
                )
            );

            y.AddKey(
                new Keyframe(
                    time,
                    q.y
                )
            );

            z.AddKey(
                new Keyframe(
                    time,
                    q.z
                )
            );

            w.AddKey(
                new Keyframe(
                    time,
                    q.w
                )
            );
        }

        MakeLinear(x);
        MakeLinear(y);
        MakeLinear(z);
        MakeLinear(w);

        SetTransformCurve(
            clip,
            path,
            "m_LocalRotation.x",
            x
        );

        SetTransformCurve(
            clip,
            path,
            "m_LocalRotation.y",
            y
        );

        SetTransformCurve(
            clip,
            path,
            "m_LocalRotation.z",
            z
        );

        SetTransformCurve(
            clip,
            path,
            "m_LocalRotation.w",
            w
        );
    }

    private void ApplyVector3Curves(
        AnimationClip clip,
        Transform target,
        DecodedTrack track)
    {
        if (track.Keys.Count == 0)
        {
            return;
        }

        string path =
            AnimationUtility.CalculateTransformPath(
                target,
                skeletonRoot.transform
            );

        AnimationCurve x =
            new AnimationCurve();

        AnimationCurve y =
            new AnimationCurve();

        AnimationCurve z =
            new AnimationCurve();

        for (int i = 0;
             i < track.Keys.Count;
             i++)
        {
            DecodedKey key =
                track.Keys[i];

            if (key.Values == null ||
                key.Values.Length != 3)
            {
                throw new Exception(
                    "Vector3 track has invalid value length."
                );
            }

            float time =
                (float)key.Frame /
                sampleRate;

            float vx = (float)key.Values[0];
            float vy = (float)key.Values[1];
            float vz = (float)key.Values[2];

            if (convertFoxToUnityFlipX)
            {
                vx = -vx;
            }

            x.AddKey(
                new Keyframe(
                    time,
                    vx
                )
            );

            y.AddKey(
                new Keyframe(
                    time,
                    vy
                )
            );

            z.AddKey(
                new Keyframe(
                    time,
                    vz
                )
            );
        }

        MakeLinear(x);
        MakeLinear(y);
        MakeLinear(z);

        SetTransformCurve(
            clip,
            path,
            "m_LocalPosition.x",
            x
        );

        SetTransformCurve(
            clip,
            path,
            "m_LocalPosition.y",
            y
        );

        SetTransformCurve(
            clip,
            path,
            "m_LocalPosition.z",
            z
        );
    }

    private void SetTransformCurve(
        AnimationClip clip,
        string path,
        string property,
        AnimationCurve curve)
    {
        EditorCurveBinding binding =
            EditorCurveBinding.FloatCurve(
                path,
                typeof(Transform),
                property
            );

        AnimationUtility.SetEditorCurve(
            clip,
            binding,
            curve
        );
    }

    private void MakeLinear(
        AnimationCurve curve)
    {
        for (int i = 0;
             i < curve.length;
             i++)
        {
            AnimationUtility.SetKeyLeftTangentMode(
                curve,
                i,
                AnimationUtility.TangentMode.Linear
            );

            AnimationUtility.SetKeyRightTangentMode(
                curve,
                i,
                AnimationUtility.TangentMode.Linear
            );
        }
    }

    private void WriteBuildReport(
        string assetPath,
        string text)
    {
        try
        {
            string projectRoot =
                Path.GetDirectoryName(
                    Application.dataPath
                );

            string absoluteAssetPath =
                Path.Combine(
                    projectRoot,
                    assetPath
                );

            string directory =
                Path.GetDirectoryName(
                    absoluteAssetPath
                );

            string filename =
                Path.GetFileNameWithoutExtension(
                    absoluteAssetPath
                );

            string reportPath =
                Path.Combine(
                    directory,
                    filename +
                    "_build_report.txt"
                );

            File.WriteAllText(
                reportPath,
                text,
                Encoding.UTF8
            );

            AssetDatabase.Refresh();
        }
        catch (Exception ex)
        {
            UnityEngine.Debug.LogWarning(
                "Clip was created, but the build report could not be written: " +
                ex.Message
            );
        }
    }

    private int Align(
        int value,
        int alignment)
    {
        int remainder =
            value % alignment;

        if (remainder == 0)
        {
            return value;
        }

        return
            value +
            alignment -
            remainder;
    }

    private ushort ReadUInt16(
        byte[] data,
        int offset)
    {
        EnsureRange(
            data,
            offset,
            2
        );

        return (ushort)(
            data[offset] |
            (data[offset + 1] << 8)
        );
    }

    private short ReadInt16(
        byte[] data,
        int offset)
    {
        return unchecked(
            (short)ReadUInt16(
                data,
                offset
            )
        );
    }

    private uint ReadUInt32(
        byte[] data,
        int offset)
    {
        EnsureRange(
            data,
            offset,
            4
        );

        return
            (uint)data[offset] |
            ((uint)data[offset + 1] << 8) |
            ((uint)data[offset + 2] << 16) |
            ((uint)data[offset + 3] << 24);
    }

    private int ReadInt32(
        byte[] data,
        int offset)
    {
        return unchecked(
            (int)ReadUInt32(
                data,
                offset
            )
        );
    }

    private float ReadSingle(
        byte[] data,
        int offset)
    {
        uint bits =
            ReadUInt32(
                data,
                offset
            );

        return UInt32ToSingle(bits);
    }

    private void EnsureRange(
        byte[] data,
        int offset,
        int count)
    {
        if (data == null ||
            offset < 0 ||
            count < 0 ||
            offset + count >
            data.Length)
        {
            throw new EndOfStreamException(
                "Read outside file bounds at 0x" +
                offset.ToString("X") +
                " (" +
                count +
                " bytes)."
            );
        }
    }

    private class BitReader
    {
        private byte[] data;

        public int BitPosition;

        public BitReader(
            byte[] source,
            int bitPosition)
        {
            data = source;
            BitPosition =
                bitPosition;
        }

        public uint ReadBits(
            int count)
        {
            if (count < 0 ||
                count > 32)
            {
                throw new ArgumentOutOfRangeException(
                    "count"
                );
            }

            if (BitPosition +
                count >
                data.Length * 8)
            {
                throw new EndOfStreamException(
                    "Bitstream read outside GANI at bit " +
                    BitPosition
                );
            }

            uint value = 0;

            for (int i = 0;
                 i < count;
                 i++)
            {
                int byteIndex =
                    BitPosition >> 3;

                int bitIndex =
                    BitPosition & 7;

                uint bit =
                    (uint)(
                        (data[byteIndex] >>
                         bitIndex) &
                        1
                    );

                value |=
                    bit << i;

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
        public ushort Id;
        public byte UnknownA;
        public byte UnknownB;
        public uint FrameCount;
        public byte FrameRate;
        public List<TrkUnit> Units;
        public TrkSegment[] SegmentsById;
    }

    private class TrkUnit
    {
        public int Index;
        public int Position;
        public uint NameHash;
        public byte SegmentCount;
        public byte Flags;
        public ushort Padding;
        public List<TrkSegment> Segments;
    }

    private class TrkSegment
    {
        public int UnitIndex;
        public uint UnitNameHash;
        public int Position;
        public int DataOffset;
        public short Id;
        public byte Type;
        public byte NextEntryOffset;
        public byte ComponentBitSize;
    }

    private class MiniGani
    {
        public uint FrameCount;
        public byte Padding0;
        public byte ParamCount;
        public ushort Padding1;
        public List<GaniParam> Params;
        public byte[] UnitFlags;
        public int SegmentHeaderStart;
        public GaniSegmentHeader[] SegmentHeaders;
    }

    private class GaniParam
    {
        public uint NameHash;
        public float Value;
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
        public byte Type;
        public byte BitSize;
        public List<DecodedKey> Keys =
            new List<DecodedKey>();
    }

    private class DecodedKey
    {
        public int Frame;
        public double[] Values;

        public DecodedKey(
            int frame,
            double[] values)
        {
            Frame = frame;
            Values = values;
        }
    }

    private class SegmentValue
    {
        public byte Type;
        public double[] Values;
    }
}
