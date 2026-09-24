# GANI Type2 / Mini-GANI research tools

Experimental Unity Editor tools developed alongside FMDL Studio v2.1 for reverse-engineering
MGSV/Fox Engine Type2 (Mini-GANI) animation data.

## Included

- `GANIType2Probe.cs` — read-only GANI + TRK layout/decode probe.
- `GANIClipPreviewDebugger.cs` — samples generated AnimationClips directly on a selected hierarchy.
- `GANIType2BodyAlpha.cs` — early conservative body/finger mapping experiment.
- `GANIType2BodyBeta.cs` — isolated limb mapping experiment.
- `GANIType2FullBodyRotation.cs` — combined rotation-only body experiment.
- `GANIType2FullBodyRotationAxisFix.cs` — coordinate-basis conversion experiment.
- `GANIType2MotionPointViewer.cs` — visualizes decoded Vector3 motion-point tracks in Scene view.

## Unity / FMDL Studio

These scripts are placed inside the existing `FMDL-Studio-v2` Unity project so they live together
with the FMDL Studio Unity package. They use `FMDL Studio/GANI Tools/...` editor menu entries.

The existing FMDL Studio source/package is intentionally left unchanged.

## Important status

This is experimental research code, not a finished animation importer/exporter. Rotation decoding,
TRK/GANI segment matching, key-frame stepping, and Unity clip sampling were validated during testing,
but full Motion Point / IK mapping is still under investigation.

## Game data

No extracted MGSV `.gani`, `.trk`, `.fmdl`, or other game assets are committed here.
Users must supply their own legally obtained files for testing.
