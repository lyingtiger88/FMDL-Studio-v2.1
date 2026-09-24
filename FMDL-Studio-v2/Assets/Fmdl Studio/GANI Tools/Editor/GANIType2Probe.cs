using UnityEngine;
using UnityEditor;

using System;
using System.IO;
using System.Text;
using System.Collections.Generic;

using FmdlStudio.Scripts.Static;

public class GANIType2Probe : EditorWindow
{
    private string ganiPath = "";
    private string trkPath = "";
    private GameObject skeletonRoot;
    private Vector2 scroll;

    private const uint TrkNodeSignature = 0x4FBDAAEF;

    private const byte SegmentQuat = 0;
    private const byte SegmentFloat = 1;
    private const byte SegmentVector2 = 2;
    private const byte SegmentVector3 = 3;
    private const byte SegmentVector4 = 4;
    private const byte SegmentQuatDiff = 5;
    private const byte SegmentVectorDiff = 6;

    private const byte UnitFlagLoop = 0x1;
    private const byte UnitFlagHermite = 0x2;
    private const byte UnitFlagStatic = 0x4;

    [MenuItem("FMDL Studio/GANI Tools/Type2 GANI + TRK Probe")]
    public static void ShowWindow()
    {
        GANIType2Probe window =
            GetWindow<GANIType2Probe>("GANI Type2 Probe");

        window.minSize = new Vector2(820, 620);
    }

    private void OnGUI()
    {
        GUILayout.Space(10);

        GUILayout.Label(
            "MGSV Type2 / Mini GANI + TRK Probe",
            EditorStyles.boldLabel
        );

        GUILayout.Label(
            "Read-only diagnostic tool. It does not modify the GANI, TRK, FMDL, or Batch Converter.",
            EditorStyles.wordWrappedMiniLabel
        );

        GUILayout.Space(12);

        DrawPathField(
            "GANI File",
            ref ganiPath,
            "Select Mini GANI",
            "gani"
        );

        GUILayout.Space(6);

        DrawPathField(
            "TRK Layout",
            ref trkPath,
            "Select TRK Layout",
            "trk"
        );

        GUILayout.Space(10);

        skeletonRoot = (GameObject)EditorGUILayout.ObjectField(
            "Skeleton Root (optional)",
            skeletonRoot,
            typeof(GameObject),
            true
        );

        if (GUILayout.Button("Use Selected GameObject", GUILayout.Height(25)))
        {
            skeletonRoot = Selection.activeGameObject;
        }

        GUILayout.Space(12);

        if (GUILayout.Button("Analyze + Decode", GUILayout.Height(38)))
        {
            Analyze();
        }

        GUILayout.Space(15);

        scroll = EditorGUILayout.BeginScrollView(scroll);

        GUILayout.Label("What this probe checks:", EditorStyles.boldLabel);
        GUILayout.Label("- TRK UnitCount / SegmentCount / FrameCount / FrameRate");
        GUILayout.Label("- Mini GANI header, UnitFlags, segment headers and relative offsets");
        GUILayout.Label("- Segment type + bit-size agreement between GANI and TRK");
        GUILayout.Label("- Full TrackDataBlob key parsing, including packed quaternion streams");
        GUILayout.Label("- Optional StrCode32 matching against Transform names on the selected model");

        EditorGUILayout.EndScrollView();
    }

    private void DrawPathField(
        string label,
        ref string value,
        string dialogTitle,
        string extension)
    {
        EditorGUILayout.BeginHorizontal();

        value = EditorGUILayout.TextField(label, value);

        if (GUILayout.Button("Browse", GUILayout.Width(80)))
        {
            string initialFolder = "";

            if (!string.IsNullOrEmpty(value))
            {
                try
                {
                    initialFolder = Path.GetDirectoryName(value);
                }
                catch
                {
                    initialFolder = "";
                }
            }

            string selected = EditorUtility.OpenFilePanel(
                dialogTitle,
                initialFolder,
                extension
            );

            if (!string.IsNullOrEmpty(selected))
            {
                value = selected;
            }
        }

        EditorGUILayout.EndHorizontal();
    }

    private void Analyze()
    {
        if (string.IsNullOrEmpty(ganiPath) || !File.Exists(ganiPath))
        {
            EditorUtility.DisplayDialog(
                "GANI Type2 Probe",
                "Select a valid .gani file.",
                "OK"
            );
            return;
        }

        if (string.IsNullOrEmpty(trkPath) || !File.Exists(trkPath))
        {
            EditorUtility.DisplayDialog(
                "GANI Type2 Probe",
                "Select a valid .trk file.",
                "OK"
            );
            return;
        }

        try
        {
            byte[] gani = File.ReadAllBytes(ganiPath);
            byte[] trk = File.ReadAllBytes(trkPath);

            TrkLayout layout = ParseTrk(trk);
            MiniGani mini = ParseMiniGani(gani, layout);

            Dictionary<uint, string> transformNames =
                BuildTransformHashMap(skeletonRoot);

            string report = BuildReport(
                gani,
                layout,
                mini,
                transformNames
            );

            string outputPath = EditorUtility.SaveFilePanel(
                "Save GANI/TRK Probe Report",
                Path.GetDirectoryName(ganiPath),
                Path.GetFileNameWithoutExtension(ganiPath) + "_type2_probe",
                "txt"
            );

            if (string.IsNullOrEmpty(outputPath))
            {
                return;
            }

            File.WriteAllText(outputPath, report, Encoding.UTF8);

            UnityEngine.Debug.Log(
                "GANI Type2 probe completed:\n" + outputPath
            );

            EditorUtility.DisplayDialog(
                "GANI Type2 Probe",
                "Analysis completed.\n\n" + outputPath,
                "OK"
            );
        }
        catch (Exception ex)
        {
            UnityEngine.Debug.LogError(
                "GANI Type2 probe failed:\n" + ex
            );

            EditorUtility.DisplayDialog(
                "GANI Type2 Probe Error",
                ex.Message,
                "OK"
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
                "Invalid TRK UnitCount: " + layout.UnitCount
            );
        }

        if (layout.SegmentCount <= 0 || layout.SegmentCount > 8192)
        {
            throw new Exception(
                "Invalid TRK SegmentCount: " + layout.SegmentCount
            );
        }

        int unitOffsetTable = baseOffset + 0x14;

        if (unitOffsetTable + layout.UnitCount * 4 > data.Length)
        {
            throw new Exception("TRK UnitOffsets table is truncated.");
        }

        layout.Units = new List<TrkUnit>();
        layout.SegmentsById = new TrkSegment[layout.SegmentCount];

        for (int i = 0; i < layout.UnitCount; i++)
        {
            uint relative = ReadUInt32(
                data,
                unitOffsetTable + i * 4
            );

            int unitPosition = baseOffset + (int)relative;

            if (unitPosition < 0 || unitPosition + 8 > data.Length)
            {
                throw new Exception(
                    "TRK unit " + i + " points outside the file."
                );
            }

            TrkUnit unit = new TrkUnit();
            unit.Index = i;
            unit.Position = unitPosition;
            unit.NameHash = ReadUInt32(data, unitPosition + 0x00);
            unit.SegmentCount = data[unitPosition + 0x04];
            unit.Flags = data[unitPosition + 0x05];
            unit.Padding = ReadUInt16(data, unitPosition + 0x06);
            unit.Segments = new List<TrkSegment>();

            int segmentPosition = unitPosition + 8;

            for (int j = 0; j < unit.SegmentCount; j++)
            {
                if (segmentPosition + 8 > data.Length)
                {
                    throw new Exception(
                        "TRK segment table is truncated at unit " + i + "."
                    );
                }

                TrkSegment segment = new TrkSegment();
                segment.UnitIndex = i;
                segment.UnitNameHash = unit.NameHash;
                segment.Position = segmentPosition;
                segment.DataOffset = ReadInt32(data, segmentPosition + 0x00);
                segment.Id = ReadInt16(data, segmentPosition + 0x04);

                byte typeAndNext = data[segmentPosition + 0x06];

                segment.Type = (byte)(typeAndNext & 0x0F);
                segment.NextEntryOffset = (byte)((typeAndNext >> 4) & 0x0F);
                segment.ComponentBitSize = data[segmentPosition + 0x07];

                unit.Segments.Add(segment);

                if (segment.Id >= 0 && segment.Id < layout.SegmentCount)
                {
                    if (layout.SegmentsById[segment.Id] != null)
                    {
                        throw new Exception(
                            "Duplicate TRK segment ID: " + segment.Id
                        );
                    }

                    layout.SegmentsById[segment.Id] = segment;
                }

                segmentPosition += 8;
            }

            layout.Units.Add(unit);
        }

        for (int i = 0; i < layout.SegmentsById.Length; i++)
        {
            if (layout.SegmentsById[i] == null)
            {
                throw new Exception(
                    "TRK is missing segment ID " + i + "."
                );
            }
        }

        return layout;
    }

    private MiniGani ParseMiniGani(
        byte[] data,
        TrkLayout layout)
    {
        if (data == null || data.Length < 8)
        {
            throw new Exception("GANI file is too small.");
        }

        MiniGani result = new MiniGani();

        result.FrameCount = ReadUInt32(data, 0x00);
        result.Padding0 = data[0x04];
        result.ParamCount = data[0x05];
        result.Padding1 = ReadUInt16(data, 0x06);
        result.Params = new List<GaniParam>();

        int position = 8;

        for (int i = 0; i < result.ParamCount; i++)
        {
            if (position + 8 > data.Length)
            {
                throw new Exception("GANI parameter table is truncated.");
            }

            GaniParam param = new GaniParam();
            param.NameHash = ReadUInt32(data, position);
            param.Value = ReadSingle(data, position + 4);
            result.Params.Add(param);

            position += 8;
        }

        if (position + layout.UnitCount > data.Length)
        {
            throw new Exception("GANI UnitFlags are truncated.");
        }

        result.UnitFlags = new byte[layout.UnitCount];

        for (int i = 0; i < layout.UnitCount; i++)
        {
            result.UnitFlags[i] = data[position + i];
        }

        position += layout.UnitCount;
        position = Align(position, 4);

        result.SegmentHeaderStart = position;
        result.SegmentHeaders = new GaniSegmentHeader[layout.SegmentCount];

        if (position + layout.SegmentCount * 4 > data.Length)
        {
            throw new Exception("GANI segment-header table is truncated.");
        }

        for (int i = 0; i < layout.SegmentCount; i++)
        {
            int headerPosition = position + i * 4;
            uint packed = ReadUInt32(data, headerPosition);

            GaniSegmentHeader header = new GaniSegmentHeader();
            header.Id = i;
            header.Position = headerPosition;
            header.ComponentBitSize = (byte)(packed & 0xFF);
            header.DataOffset = (packed >> 8) & 0x00FFFFFF;

            if (header.DataOffset != 0)
            {
                // FoxEngineTemplates mtar.bt:
                // FSeek(startof(SegmentHeaders[sgIdx]) + DataOffset)
                header.DataAddress =
                    header.Position +
                    (int)header.DataOffset;

                if (header.DataAddress < 0 ||
                    header.DataAddress >= data.Length)
                {
                    throw new Exception(
                        "GANI segment " + i +
                        " points outside the file."
                    );
                }
            }
            else
            {
                header.DataAddress = -1;
            }

            result.SegmentHeaders[i] = header;
        }

        result.DataSectionMinimum =
            position + layout.SegmentCount * 4 + 16;

        return result;
    }

    private string BuildReport(
        byte[] gani,
        TrkLayout layout,
        MiniGani mini,
        Dictionary<uint, string> transformNames)
    {
        StringBuilder sb = new StringBuilder();

        sb.AppendLine("MGSV TYPE2 / MINI GANI + TRK PROBE");
        sb.AppendLine("============================================================");
        sb.AppendLine();
        sb.AppendLine("GANI: " + ganiPath);
        sb.AppendLine("TRK : " + trkPath);
        sb.AppendLine("Selected skeleton: " +
            (skeletonRoot != null ? skeletonRoot.name : "<none>"));
        sb.AppendLine();

        sb.AppendLine("TRK layout");
        sb.AppendLine("----------");
        sb.AppendLine("Node signature : 0x" + layout.NodeSignature.ToString("X8"));
        sb.AppendLine("TrackHeader    : 0x" + layout.TrackHeaderOffset.ToString("X"));
        sb.AppendLine("UnitCount      : " + layout.UnitCount);
        sb.AppendLine("SegmentCount   : " + layout.SegmentCount);
        sb.AppendLine("FrameCount     : " + layout.FrameCount);
        sb.AppendLine("FrameRate byte : " + layout.FrameRate);
        sb.AppendLine();

        sb.AppendLine("Mini GANI");
        sb.AppendLine("---------");
        sb.AppendLine("File size      : " + gani.Length);
        sb.AppendLine("FrameCount     : " + mini.FrameCount);
        sb.AppendLine("ParamCount     : " + mini.ParamCount);
        sb.AppendLine("Segment table  : 0x" + mini.SegmentHeaderStart.ToString("X"));
        sb.AppendLine("Expected data >= 0x" + mini.DataSectionMinimum.ToString("X"));
        sb.AppendLine();

        int bitSizeMatches = 0;
        int decodedSegments = 0;
        int exactBoundaries = 0;
        int finalFrameMatches = 0;
        int resolvedUnits = 0;

        sb.AppendLine("Units");
        sb.AppendLine("-----");

        for (int unitIndex = 0;
             unitIndex < layout.Units.Count;
             unitIndex++)
        {
            TrkUnit unit = layout.Units[unitIndex];
            byte miniFlags = mini.UnitFlags[unitIndex];

            string resolved = null;

            if (transformNames != null)
            {
                transformNames.TryGetValue(
                    unit.NameHash,
                    out resolved
                );
            }

            if (!string.IsNullOrEmpty(resolved))
            {
                resolvedUnits++;
            }

            sb.AppendLine(
                "Unit " + unitIndex.ToString("D2") +
                "  hash=0x" + unit.NameHash.ToString("X8") +
                "  TRKFlags=0x" + unit.Flags.ToString("X2") +
                "  GANIFlags=0x" + miniFlags.ToString("X2") +
                "  segments=" + unit.SegmentCount +
                "  transform=" +
                (!string.IsNullOrEmpty(resolved) ? resolved : "<unresolved>")
            );
        }

        sb.AppendLine();
        sb.AppendLine("Segments / decoded TrackDataBlob");
        sb.AppendLine("--------------------------------");

        for (int id = 0; id < layout.SegmentCount; id++)
        {
            TrkSegment trkSegment = layout.SegmentsById[id];
            GaniSegmentHeader ganiSegment = mini.SegmentHeaders[id];

            bool bitsMatch =
                trkSegment.ComponentBitSize ==
                ganiSegment.ComponentBitSize;

            if (bitsMatch)
            {
                bitSizeMatches++;
            }

            int nextAddress = FindNextDataAddress(
                mini.SegmentHeaders,
                id,
                gani.Length
            );

            DecodeResult decoded = null;

            if (ganiSegment.DataAddress >= 0)
            {
                byte unitFlags =
                    mini.UnitFlags[trkSegment.UnitIndex];

                decoded = DecodeTrackDataBlob(
                    gani,
                    ganiSegment.DataAddress,
                    trkSegment.Type,
                    ganiSegment.ComponentBitSize,
                    (byte)(unitFlags & UnitFlagStatic),
                    mini.FrameCount
                );

                decodedSegments++;

                if (decoded.EndByte == nextAddress)
                {
                    exactBoundaries++;
                }

                if (decoded.FinalFrame == (int)mini.FrameCount)
                {
                    finalFrameMatches++;
                }
            }

            sb.Append(
                "S" + id.ToString("D2") +
                " U" + trkSegment.UnitIndex.ToString("D2") +
                " " + SegmentTypeName(trkSegment.Type) +
                " bits=" + ganiSegment.ComponentBitSize +
                " trkBits=" + trkSegment.ComponentBitSize +
                " bitsMatch=" + bitsMatch +
                " addr=" +
                (ganiSegment.DataAddress >= 0
                    ? "0x" + ganiSegment.DataAddress.ToString("X")
                    : "<none>")
            );

            if (decoded != null)
            {
                sb.Append(
                    " keys=" + decoded.KeyCount +
                    " finalFrame=" + decoded.FinalFrame +
                    " parsedEnd=0x" + decoded.EndByte.ToString("X") +
                    " expectedEnd=0x" + nextAddress.ToString("X") +
                    " boundary=" + (decoded.EndByte == nextAddress)
                );

                if (decoded.FirstValue != null)
                {
                    sb.Append(" first=" + decoded.FirstValue.ToText());
                }

                if (decoded.LastValue != null)
                {
                    sb.Append(" last=" + decoded.LastValue.ToText());
                }
            }

            sb.AppendLine();
        }

        sb.AppendLine();
        sb.AppendLine("Compatibility summary");
        sb.AppendLine("---------------------");
        sb.AppendLine(
            "FrameCount match        : " +
            (layout.FrameCount == mini.FrameCount) +
            " (TRK=" + layout.FrameCount +
            ", GANI=" + mini.FrameCount + ")"
        );
        sb.AppendLine(
            "Segment bit-size match  : " +
            bitSizeMatches + " / " + layout.SegmentCount
        );
        sb.AppendLine(
            "Decoded data segments   : " + decodedSegments
        );
        sb.AppendLine(
            "Exact next boundaries   : " +
            exactBoundaries + " / " + decodedSegments +
            " (last segment may have outer-file padding)"
        );
        sb.AppendLine(
            "Final frame = FrameCount: " +
            finalFrameMatches + " / " + decodedSegments
        );
        sb.AppendLine(
            "Direct Transform hashes : " +
            resolvedUnits + " / " + layout.UnitCount
        );

        sb.AppendLine();
        sb.AppendLine("Notes");
        sb.AppendLine("-----");
        sb.AppendLine("- Type 0 = quaternion, Type 3 = Vector3, Type 5 = quaternion-diff, Type 6 = vector-diff.");
        sb.AppendLine("- Packed quaternion data is read continuously; there is no byte padding between FirstKey and subsequent frame-step/key records.");
        sb.AppendLine("- Each dynamic segment has FirstKey at frame 0, then [8-bit frame delta + key data] until FrameCount is reached.");
        sb.AppendLine("- This probe decodes values but intentionally does not apply them to bones yet.");
        sb.AppendLine("- Direct Transform hash resolution is diagnostic only; MTAR GANI track units may be Motion Points rather than literal FMDL bone names.");

        if (transformNames != null && transformNames.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("Selected-model Transform StrCode32 hashes");
            sb.AppendLine("----------------------------------------");

            foreach (KeyValuePair<uint, string> pair in transformNames)
            {
                sb.AppendLine(
                    "0x" + pair.Key.ToString("X8") +
                    "  " + pair.Value
                );
            }
        }

        return sb.ToString();
    }

    private Dictionary<uint, string> BuildTransformHashMap(
        GameObject root)
    {
        if (root == null)
        {
            return null;
        }

        Dictionary<uint, string> result =
            new Dictionary<uint, string>();

        Transform[] transforms =
            root.GetComponentsInChildren<Transform>(true);

        for (int i = 0; i < transforms.Length; i++)
        {
            Transform t = transforms[i];

            try
            {
                ulong fullHash =
                    Hashing.HashFileNameLegacy(
                        t.name,
                        false
                    );

                uint hash = (uint)fullHash;

                if (!result.ContainsKey(hash))
                {
                    result.Add(hash, t.name);
                }
            }
            catch (Exception ex)
            {
                UnityEngine.Debug.LogWarning(
                    "Could not hash Transform name '" +
                    t.name + "': " + ex.Message
                );
            }
        }

        return result;
    }

    private DecodeResult DecodeTrackDataBlob(
        byte[] data,
        int address,
        byte type,
        byte componentBitSize,
        byte staticFlag,
        uint totalFrameCount)
    {
        BitReader reader = new BitReader(data, address * 8);
        DecodeResult result = new DecodeResult();

        SegmentValue first = ReadSegmentValue(
            reader,
            type,
            componentBitSize
        );

        result.FirstValue = first;
        result.LastValue = first;
        result.KeyCount = 1;
        result.FinalFrame = 0;

        bool isStatic = staticFlag != 0;

        if (!isStatic)
        {
            int frameIndex = 0;
            int safety = 0;

            do
            {
                int frameStep = (int)reader.ReadBits(8);
                frameIndex += frameStep;

                SegmentValue value = ReadSegmentValue(
                    reader,
                    type,
                    componentBitSize
                );

                result.LastValue = value;
                result.KeyCount++;
                result.FinalFrame = frameIndex;

                safety++;

                if (safety > 100000)
                {
                    throw new Exception(
                        "TrackDataBlob key loop exceeded safety limit."
                    );
                }
            }
            while (frameIndex < (int)totalFrameCount);
        }

        int endByte = (reader.BitPosition + 7) / 8;
        endByte = Align(endByte, 2);

        result.EndByte = endByte;

        return result;
    }

    private SegmentValue ReadSegmentValue(
        BitReader reader,
        byte type,
        byte bits)
    {
        SegmentValue result = new SegmentValue();
        result.Type = type;

        switch (type)
        {
            case SegmentQuat:
            case SegmentQuatDiff:
                if (bits != 12 && bits != 13 && bits != 15)
                {
                    throw new Exception(
                        "Unsupported quaternion bit size: " + bits
                    );
                }

                result.Values = ReadCompressedQuaternion(
                    reader,
                    bits
                );
                break;

            case SegmentFloat:
                result.Values = new double[1];
                result.Values[0] = ReadScalar(reader, bits);
                break;

            case SegmentVector2:
                result.Values = new double[2];
                result.Values[0] = ReadScalar(reader, bits);
                result.Values[1] = ReadScalar(reader, bits);
                break;

            case SegmentVector3:
            case SegmentVectorDiff:
                result.Values = new double[3];
                result.Values[0] = ReadScalar(reader, bits);
                result.Values[1] = ReadScalar(reader, bits);
                result.Values[2] = ReadScalar(reader, bits);
                break;

            case SegmentVector4:
                result.Values = new double[4];
                result.Values[0] = ReadScalar(reader, bits);
                result.Values[1] = ReadScalar(reader, bits);
                result.Values[2] = ReadScalar(reader, bits);
                result.Values[3] = ReadScalar(reader, bits);
                break;

            default:
                throw new Exception(
                    "Unsupported TRK segment type: " + type
                );
        }

        return result;
    }

    private double[] ReadCompressedQuaternion(
        BitReader reader,
        int bitSize)
    {
        double divisor = Math.Pow(2.0, bitSize);

        double theta =
            (double)reader.ReadBits(bitSize) /
            divisor;

        double valueY =
            (double)reader.ReadBits(bitSize) /
            divisor;

        double valueZ =
            (double)reader.ReadBits(bitSize) /
            divisor;

        uint signX = reader.ReadBits(1);
        uint signY = reader.ReadBits(1);
        uint signZ = reader.ReadBits(1);

        double valueXBase =
            1.0 - valueY - valueZ;

        double denominator = Math.Sqrt(
            valueY * valueY +
            valueZ * valueZ +
            valueXBase * valueXBase
        );

        double scale = 0.0;

        if (denominator > 0.0000000001)
        {
            scale = Math.Sin(theta) / denominator;
        }

        double x = valueY * scale;
        double y = valueZ * scale;
        double z = valueXBase * scale;

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

        double w = Math.Cos(theta);

        return new double[] { x, y, z, w };
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
            ushort value = (ushort)reader.ReadBits(16);
            return ReadAnimHalf(value);
        }

        if (bitSize == 32)
        {
            uint value = reader.ReadBits(32);
            return UInt32ToSingle(value);
        }

        throw new Exception(
            "Unsupported scalar bit size: " + bitSize
        );
    }

    private double ReadAnimHalf(ushort value)
    {
        // Matches FoxEngineTemplates/common/anim_common.bt AnimHalf.
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

    private int FindNextDataAddress(
        GaniSegmentHeader[] headers,
        int index,
        int fileLength)
    {
        for (int i = index + 1; i < headers.Length; i++)
        {
            if (headers[i].DataAddress >= 0)
            {
                return headers[i].DataAddress;
            }
        }

        return fileLength;
    }

    private string SegmentTypeName(byte type)
    {
        switch (type)
        {
            case SegmentQuat:
                return "QUAT";
            case SegmentFloat:
                return "FLOAT";
            case SegmentVector2:
                return "VECTOR2";
            case SegmentVector3:
                return "VECTOR3";
            case SegmentVector4:
                return "VECTOR4";
            case SegmentQuatDiff:
                return "QUAT_DIFF";
            case SegmentVectorDiff:
                return "VECTOR_DIFF";
            default:
                return "UNKNOWN(" + type + ")";
        }
    }

    private int Align(int value, int alignment)
    {
        int remainder = value % alignment;

        if (remainder == 0)
        {
            return value;
        }

        return value + alignment - remainder;
    }

    private ushort ReadUInt16(byte[] data, int offset)
    {
        EnsureRange(data, offset, 2);

        return (ushort)(
            data[offset] |
            (data[offset + 1] << 8)
        );
    }

    private short ReadInt16(byte[] data, int offset)
    {
        return unchecked((short)ReadUInt16(data, offset));
    }

    private uint ReadUInt32(byte[] data, int offset)
    {
        EnsureRange(data, offset, 4);

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

    private float ReadSingle(byte[] data, int offset)
    {
        uint bits = ReadUInt32(data, offset);
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
            offset + count > data.Length)
        {
            throw new EndOfStreamException(
                "Read outside file bounds at 0x" +
                offset.ToString("X") +
                " (" + count + " bytes)."
            );
        }
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
                throw new EndOfStreamException(
                    "Bitstream read outside GANI at bit " +
                    BitPosition
                );
            }

            uint value = 0;

            for (int i = 0; i < count; i++)
            {
                int byteIndex = BitPosition >> 3;
                int bitIndex = BitPosition & 7;

                uint bit =
                    (uint)((data[byteIndex] >> bitIndex) & 1);

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
        public int DataSectionMinimum;
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

    private class DecodeResult
    {
        public int KeyCount;
        public int FinalFrame;
        public int EndByte;
        public SegmentValue FirstValue;
        public SegmentValue LastValue;
    }

    private class SegmentValue
    {
        public byte Type;
        public double[] Values;

        public string ToText()
        {
            if (Values == null)
            {
                return "<null>";
            }

            StringBuilder sb = new StringBuilder();
            sb.Append("(");

            for (int i = 0; i < Values.Length; i++)
            {
                if (i > 0)
                {
                    sb.Append(", ");
                }

                sb.Append(Values[i].ToString("G7"));
            }

            sb.Append(")");
            return sb.ToString();
        }
    }
}
