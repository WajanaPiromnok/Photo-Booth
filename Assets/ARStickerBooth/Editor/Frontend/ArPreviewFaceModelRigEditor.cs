using PhotoBooth.Booth.Frontend;
using UnityEditor;
using UnityEngine;

namespace PhotoBooth.Booth.Editor.Frontend
{
    [CustomEditor(typeof(ArPreviewFaceModelRig))]
    public sealed class ArPreviewFaceModelRigEditor : UnityEditor.Editor
    {
        private SerializedProperty modelScaleMultiplierProp;
        private SerializedProperty modelLocalScaleOffsetProp;
        private SerializedProperty canonicalMatrixScaleWeightProp;
        private SerializedProperty modelLocalPositionOffsetProp;
        private SerializedProperty faceMaskScreenOffsetByEyeDistanceProp;
        private SerializedProperty faceDepthProp;
        private SerializedProperty faceDepthScaleProp;
        private SerializedProperty modelLocalEulerOffsetProp;
        private SerializedProperty mirrorModelXProp;
        private SerializedProperty invertFaceYawProp;
        private SerializedProperty neutralFaceMaskNoseBlendProp;
        private SerializedProperty sideFaceMaskNoseBlendProp;
        private SerializedProperty fullSideYawDegreesProp;
        private SerializedProperty showGuideModelProp;
        private SerializedProperty alignmentModeProp;
        private SerializedProperty showRuntimeDebugInfoProp;

        private bool calibrationLocked = false;
        private string calibrationPersonId = "Person1";
        private string calibrationNotes = "";

        private void OnEnable()
        {
            modelScaleMultiplierProp = serializedObject.FindProperty("modelScaleMultiplier");
            modelLocalScaleOffsetProp = serializedObject.FindProperty("modelLocalScaleOffset");
            canonicalMatrixScaleWeightProp = serializedObject.FindProperty("canonicalMatrixScaleWeight");
            modelLocalPositionOffsetProp = serializedObject.FindProperty("modelLocalPositionOffset");
            faceMaskScreenOffsetByEyeDistanceProp = serializedObject.FindProperty("faceMaskScreenOffsetByEyeDistance");
            faceDepthProp = serializedObject.FindProperty("faceDepth");
            faceDepthScaleProp = serializedObject.FindProperty("faceDepthScale");
            modelLocalEulerOffsetProp = serializedObject.FindProperty("modelLocalEulerOffset");
            mirrorModelXProp = serializedObject.FindProperty("mirrorModelX");
            invertFaceYawProp = serializedObject.FindProperty("invertFaceYaw");
            neutralFaceMaskNoseBlendProp = serializedObject.FindProperty("neutralFaceMaskNoseBlend");
            sideFaceMaskNoseBlendProp = serializedObject.FindProperty("sideFaceMaskNoseBlend");
            fullSideYawDegreesProp = serializedObject.FindProperty("fullSideYawDegrees");
            showGuideModelProp = serializedObject.FindProperty("showGuideModel");
            alignmentModeProp = serializedObject.FindProperty("alignmentMode");
            showRuntimeDebugInfoProp = serializedObject.FindProperty("showRuntimeDebugInfo");
        }

        public override void OnInspectorGUI()
        {
            serializedObject.Update();

            var rig = (ArPreviewFaceModelRig)target;

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("AR Face Model Calibration", EditorStyles.boldLabel);
            EditorGUILayout.HelpBox(
                "Adjust these values while in Play Mode to align the 3D face model with real faces.\n" +
                "Test with multiple people to find universal values.",
                MessageType.Info);

            EditorGUILayout.Space();

            // Lock Calibration
            using (new EditorGUILayout.HorizontalScope())
            {
                calibrationLocked = EditorGUILayout.Toggle(new GUIContent("Lock Calibration", "Prevents accidental changes to calibration values"), calibrationLocked);
                if (calibrationLocked)
                {
                    EditorGUILayout.HelpBox("Calibration is locked. Unlock to make changes.", MessageType.Warning);
                }
            }

            EditorGUI.BeginDisabledGroup(calibrationLocked);

            // Scale Calibration Section
            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Scale Calibration", EditorStyles.boldLabel);
            EditorGUILayout.PropertyField(modelScaleMultiplierProp, new GUIContent("Model Scale Multiplier", "Multiplies eye distance to determine base model scale"));
            SliderWithValidation(modelScaleMultiplierProp, 0.1f, 10f, 0.5f, 5f);

            EditorGUILayout.PropertyField(modelLocalScaleOffsetProp, new GUIContent("Model Local Scale Offset", "Local scale applied to the model instance"));
            Vector3SliderWithValidation(modelLocalScaleOffsetProp, 0.1f, 50f, 0.5f, 20f);

            EditorGUILayout.PropertyField(canonicalMatrixScaleWeightProp, new GUIContent("Canonical Matrix Scale Weight", "Blends between canonical face transform scale (1.0) and uniform scale (0.0)"));
            SliderWithValidation(canonicalMatrixScaleWeightProp, 0f, 1f, 0f, 1f);

            // Position Calibration Section
            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Position Calibration", EditorStyles.boldLabel);
            EditorGUILayout.PropertyField(modelLocalPositionOffsetProp, new GUIContent("Model Local Position Offset", "Local position offset applied to the model instance"));

            EditorGUILayout.PropertyField(faceMaskScreenOffsetByEyeDistanceProp, new GUIContent("Face Mask Screen Offset", "Screen offset scaled by eye distance (X=horizontal, Y=vertical)"));
            Vector2SliderWithValidation(faceMaskScreenOffsetByEyeDistanceProp, -2f, 2f, -1f, 1f);

            EditorGUILayout.PropertyField(faceDepthProp, new GUIContent("Face Depth", "Base Z-depth offset for all face anchors"));
            SliderWithValidation(faceDepthProp, -5f, 5f, -2f, 2f);

            EditorGUILayout.PropertyField(faceDepthScaleProp, new GUIContent("Face Depth Scale", "Multiplier for normalized depth from landmarks"));
            SliderWithValidation(faceDepthScaleProp, 0.1f, 5f, 0.5f, 3f);

            // Rotation & Orientation Section
            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Rotation & Orientation", EditorStyles.boldLabel);
            EditorGUILayout.PropertyField(modelLocalEulerOffsetProp, new GUIContent("Model Local Euler Offset", "Local rotation (Euler angles) applied to the model instance"));
            EditorGUILayout.PropertyField(mirrorModelXProp, new GUIContent("Mirror Model X", "Mirrors the model on the X-axis"));
            EditorGUILayout.PropertyField(invertFaceYawProp, new GUIContent("Invert Face Yaw", "Inverts the yaw rotation from face tracking"));

            // Face Blend Section
            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Face Blend Settings", EditorStyles.boldLabel);
            EditorGUILayout.PropertyField(neutralFaceMaskNoseBlendProp, new GUIContent("Neutral Face Nose Blend", "How much to blend face center toward nose when facing forward (0=eye center, 1=nose)"));
            SliderWithValidation(neutralFaceMaskNoseBlendProp, 0f, 1f, 0f, 1f);

            EditorGUILayout.PropertyField(sideFaceMaskNoseBlendProp, new GUIContent("Side Face Nose Blend", "How much to blend face center toward nose when facing sideways"));
            SliderWithValidation(sideFaceMaskNoseBlendProp, 0f, 1f, 0f, 1f);

            EditorGUILayout.PropertyField(fullSideYawDegreesProp, new GUIContent("Full Side Yaw Degrees", "Yaw angle (degrees) considered 'full side' profile for blending"));
            SliderWithValidation(fullSideYawDegreesProp, 0f, 90f, 15f, 60f);

            // Debug Options Section
            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Debug Options", EditorStyles.boldLabel);
            EditorGUILayout.PropertyField(showGuideModelProp, new GUIContent("Show Guide Model", "Show/hide the 3D face model"));
            EditorGUILayout.PropertyField(alignmentModeProp, new GUIContent("Alignment Mode", "CanonicalMatrix uses face transform, LandmarkAnchored uses only landmarks"));
            EditorGUILayout.PropertyField(showRuntimeDebugInfoProp, new GUIContent("Show Runtime Debug Info", "Display real-time debug info on screen during Play Mode"));

            EditorGUI.EndDisabledGroup();

            // Runtime Debug Info Section
            EditorGUILayout.Space();
            DrawDebugInfoBox(rig);

            // Quick Actions Section
            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Quick Actions", EditorStyles.boldLabel);
            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("Reset to Defaults", EditorStyles.miniButtonLeft))
                {
                    ResetToDefaults();
                }
                if (GUILayout.Button("Print Current Values", EditorStyles.miniButtonMid))
                {
                    PrintCurrentValues(rig);
                }
                if (GUILayout.Button("Save Calibration Session", EditorStyles.miniButtonRight))
                {
                    SaveCalibrationSession(rig);
                }
            }

            // Calibration Session Input
            EditorGUILayout.Space();
            calibrationPersonId = EditorGUILayout.TextField(new GUIContent("Person ID", "Identifier for calibration logging (e.g., Person1, TestSubject_A)"), calibrationPersonId);
            calibrationNotes = EditorGUILayout.TextField(new GUIContent("Notes", "Additional notes for this calibration session"), calibrationNotes);

            EditorGUILayout.Space();
            serializedObject.ApplyModifiedProperties();

            // Force continuous update during Play Mode
            if (Application.isPlaying && !calibrationLocked)
            {
                serializedObject.ApplyModifiedProperties();
            }
        }

        private void DrawDebugInfoBox(ArPreviewFaceModelRig rig)
        {
            EditorGUILayout.LabelField("Runtime Debug Info", EditorStyles.boldLabel);

            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
            {
                if (Application.isPlaying && rig.FaceMaskCenter != null)
                {
                    var faceMaskCenter = rig.FaceMaskCenter;
                    EditorGUILayout.LabelField($"Face Mask Center Position: {faceMaskCenter.position:F3}");
                    EditorGUILayout.LabelField($"Face Mask Center Scale: {faceMaskCenter.localScale.x:F4}");

                    var leftEye = rig.LeftEye;
                    var rightEye = rig.RightEye;
                    if (leftEye != null && rightEye != null)
                    {
                        var eyeDistance = Vector3.Distance(leftEye.position, rightEye.position);
                        EditorGUILayout.LabelField($"Eye Distance (World): {eyeDistance:F4}");
                    }

                    var headCenter = rig.HeadCenter;
                    if (headCenter != null)
                    {
                        var euler = headCenter.rotation.eulerAngles;
                        EditorGUILayout.LabelField($"Head Rotation: Y={NormalizeAngle(euler.y):F1}°, P={NormalizeAngle(euler.x):F1}°, R={NormalizeAngle(euler.z):F1}°");
                    }
                }
                else
                {
                    EditorGUILayout.LabelField("Enter Play Mode to see real-time debug info");
                }

                EditorGUILayout.LabelField($"Alignment Mode: {rig.AlignmentMode}");
                EditorGUILayout.LabelField($"Show Guide Model: {rig.ShowGuideModel}");
            }
        }

        private void SliderWithValidation(SerializedProperty property, float min, float max, float warnMin, float warnMax)
        {
            var value = property.floatValue;
            var newValue = EditorGUILayout.Slider(new GUIContent(" ", "Drag to adjust"), value, min, max);

            if (Mathf.Abs(newValue - value) > 0.0001f)
            {
                property.floatValue = newValue;
            }

            // Validation warning
            if (newValue < warnMin || newValue > warnMax)
            {
                var warningStyle = new GUIStyle(EditorStyles.helpBox);
                warningStyle.normal.textColor = Color.yellow;
                EditorGUILayout.HelpBox($"Value {newValue:F2} is outside typical range [{warnMin:F1}, {warnMax:F1}]", MessageType.Warning);
            }
        }

        private void Vector2SliderWithValidation(SerializedProperty property, float min, float max, float warnMin, float warnMax)
        {
            var value = property.vector2Value;

            using (new EditorGUILayout.HorizontalScope())
            {
                EditorGUILayout.PrefixLabel(new GUIContent(" ", "Drag to adjust"));

                var newX = EditorGUILayout.Slider(value.x, min, max, GUILayout.Width(150));
                var newY = EditorGUILayout.Slider(value.y, min, max, GUILayout.Width(150));

                if (Mathf.Abs(newX - value.x) > 0.0001f || Mathf.Abs(newY - value.y) > 0.0001f)
                {
                    property.vector2Value = new Vector2(newX, newY);
                }
            }

            // Validation warning
            if (value.x < warnMin || value.x > warnMax || value.y < warnMin || value.y > warnMax)
            {
                EditorGUILayout.HelpBox($"Value ({value.x:F2}, {value.y:F2}) is outside typical range [{warnMin:F1}, {warnMax:F1}]", MessageType.Warning);
            }
        }

        private void Vector3SliderWithValidation(SerializedProperty property, float min, float max, float warnMin, float warnMax)
        {
            var value = property.vector3Value;

            using (new EditorGUILayout.HorizontalScope())
            {
                EditorGUILayout.PrefixLabel(new GUIContent(" ", "Drag to adjust"));

                var newX = EditorGUILayout.Slider(value.x, min, max, GUILayout.Width(100));
                var newY = EditorGUILayout.Slider(value.y, min, max, GUILayout.Width(100));
                var newZ = EditorGUILayout.Slider(value.z, min, max, GUILayout.Width(100));

                if (Mathf.Abs(newX - value.x) > 0.0001f || Mathf.Abs(newY - value.y) > 0.0001f || Mathf.Abs(newZ - value.z) > 0.0001f)
                {
                    property.vector3Value = new Vector3(newX, newY, newZ);
                }
            }

            // Validation warning
            if (value.x < warnMin || value.x > warnMax || value.y < warnMin || value.y > warnMax || value.z < warnMin || value.z > warnMax)
            {
                EditorGUILayout.HelpBox($"Value ({value.x:F2}, {value.y:F2}, {value.z:F2}) is outside typical range [{warnMin:F1}, {warnMax:F1}]", MessageType.Warning);
            }
        }

        private void ResetToDefaults()
        {
            modelScaleMultiplierProp.floatValue = 1.9f;
            modelLocalScaleOffsetProp.vector3Value = new Vector3(9f, 9f, 9f);
            canonicalMatrixScaleWeightProp.floatValue = 0.35f;
            modelLocalPositionOffsetProp.vector3Value = Vector3.zero;
            faceMaskScreenOffsetByEyeDistanceProp.vector2Value = Vector2.zero;
            faceDepthProp.floatValue = 0f;
            faceDepthScaleProp.floatValue = 1.4f;
            modelLocalEulerOffsetProp.vector3Value = new Vector3(0f, 180f, 0f);
            mirrorModelXProp.boolValue = true;
            invertFaceYawProp.boolValue = true;
            neutralFaceMaskNoseBlendProp.floatValue = 0.38f;
            sideFaceMaskNoseBlendProp.floatValue = 0.85f;
            fullSideYawDegreesProp.floatValue = 35f;

            serializedObject.ApplyModifiedProperties();
            Debug.Log("PhotoBooth: Calibration reset to defaults");
        }

        private void PrintCurrentValues(ArPreviewFaceModelRig rig)
        {
            var output = $"=== AR Face Model Calibration Values ===\n" +
                         $"modelScaleMultiplier: {modelScaleMultiplierProp.floatValue:F4}\n" +
                         $"modelLocalScaleOffset: ({modelLocalScaleOffsetProp.vector3Value.x:F2}, {modelLocalScaleOffsetProp.vector3Value.y:F2}, {modelLocalScaleOffsetProp.vector3Value.z:F2})\n" +
                         $"canonicalMatrixScaleWeight: {canonicalMatrixScaleWeightProp.floatValue:F3}\n" +
                         $"modelLocalPositionOffset: ({modelLocalPositionOffsetProp.vector3Value.x:F3}, {modelLocalPositionOffsetProp.vector3Value.y:F3}, {modelLocalPositionOffsetProp.vector3Value.z:F3})\n" +
                         $"faceMaskScreenOffsetByEyeDistance: ({faceMaskScreenOffsetByEyeDistanceProp.vector2Value.x:F3}, {faceMaskScreenOffsetByEyeDistanceProp.vector2Value.y:F3})\n" +
                         $"faceDepth: {faceDepthProp.floatValue:F3}\n" +
                         $"faceDepthScale: {faceDepthScaleProp.floatValue:F3}\n" +
                         $"modelLocalEulerOffset: ({modelLocalEulerOffsetProp.vector3Value.x:F1}, {modelLocalEulerOffsetProp.vector3Value.y:F1}, {modelLocalEulerOffsetProp.vector3Value.z:F1})\n" +
                         $"mirrorModelX: {mirrorModelXProp.boolValue}\n" +
                         $"invertFaceYaw: {invertFaceYawProp.boolValue}\n" +
                         $"neutralFaceMaskNoseBlend: {neutralFaceMaskNoseBlendProp.floatValue:F3}\n" +
                         $"sideFaceMaskNoseBlend: {sideFaceMaskNoseBlendProp.floatValue:F3}\n" +
                         $"fullSideYawDegrees: {fullSideYawDegreesProp.floatValue:F1}\n" +
                         $"alignmentMode: {alignmentModeProp.enumDisplayNames[alignmentModeProp.enumValueIndex]}";

            Debug.Log(output);
        }

        private void SaveCalibrationSession(ArPreviewFaceModelRig rig)
        {
            var csv = $"[CALIBRATION] PersonID={calibrationPersonId}," +
                      $"Scale={modelScaleMultiplierProp.floatValue:F4}," +
                      $"OffsetX={modelLocalPositionOffsetProp.vector3Value.x:F3}," +
                      $"OffsetY={modelLocalPositionOffsetProp.vector3Value.y:F3}," +
                      $"OffsetZ={modelLocalPositionOffsetProp.vector3Value.z:F3}," +
                      $"ScreenOffsetX={faceMaskScreenOffsetByEyeDistanceProp.vector2Value.x:F3}," +
                      $"ScreenOffsetY={faceMaskScreenOffsetByEyeDistanceProp.vector2Value.y:F3}," +
                      $"NoseBlendNeutral={neutralFaceMaskNoseBlendProp.floatValue:F3}," +
                      $"NoseBlendSide={sideFaceMaskNoseBlendProp.floatValue:F3}," +
                      $"LocalScaleX={modelLocalScaleOffsetProp.vector3Value.x:F2}," +
                      $"LocalScaleY={modelLocalScaleOffsetProp.vector3Value.y:F2}," +
                      $"LocalScaleZ={modelLocalScaleOffsetProp.vector3Value.z:F2}," +
                      $"AlignmentMode={alignmentModeProp.enumDisplayNames[alignmentModeProp.enumValueIndex]}," +
                      $"Notes={calibrationNotes}";

            Debug.Log(csv);
            Debug.Log("Calibration session logged. Copy the [CALIBRATION] line above to your spreadsheet.");
        }

        private static float NormalizeAngle(float angle)
        {
            while (angle > 180f) angle -= 360f;
            while (angle < -180f) angle += 360f;
            return angle;
        }
    }
}
