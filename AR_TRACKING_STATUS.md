# AR Tracking Status

Last updated: 2026-04-30

## Summary

ระบบ AR tracking ตอนนี้ใช้ `MediaPipe Face Landmarker` ผ่าน Homuler MediaPipe Unity Plugin เป็น provider หลัก และ fallback เป็น `OpenCVForUnity` YuNet + FaceMark ถ้า MediaPipe เริ่มไม่สำเร็จ

หลังจากนี้ ทุกครั้งที่มีการแก้ AR tracking / sticker overlay / face provider ให้เพิ่มบันทึกในไฟล์นี้เพื่อใช้ track สถานะงาน

เป้าหมายปัจจุบัน:

- ใช้ webcam flow เดิมของ Photo Booth
- detect หน้าแบบ realtime
- เอา landmark ตาซ้าย/ขวาจาก MediaPipe 478 จุดมาวางแว่น
- ใช้ MediaPipe face transform matrix เพื่อประมาณ yaw/pitch/roll สำหรับ live preview
- ถ้า `faces=0` ให้ overlay แว่นหาย
- ใช้ tracking frame เดียวกันทั้ง preview, capture PNG, motion frames, และ MP4

## What Has Been Done

### 1. AR tracking abstraction

เพิ่มโมเดลกลางสำหรับ AR tracking:

- `IArTrackingProvider`
- `ArTrackingFrame`
- `FaceTrack`
- `FaceLandmarks`
- capability flags สำหรับ `Face`, `Body`, `Hand`, `Face3D`

ไฟล์หลัก:

- `Assets/ARStickerBooth/Runtime/AR/ArTrackingModels.cs`

### 2. 2D sticker overlay

เพิ่มระบบ render sticker 2D:

- anchor ตาม `Eyes`, `Forehead`, `Nose`, `Mouth`, `FaceBounds`
- ใช้ eye landmarks เพื่อคำนวณ position, rotation, scale
- live preview แสดงเป็น child GameObject/RawImage ใต้ `ArPreviewOverlay` เพื่อให้ inspect และปรับตำแหน่งได้ง่ายขึ้น
- bake ลง captured texture และ motion frames

ไฟล์หลัก:

- `Assets/ARStickerBooth/Runtime/AR/ArStickerOverlay.cs`

### 3. Capture/composition integration

เชื่อม AR overlay เข้ากับ photo booth flow:

- preview loop track หน้าเรื่อย ๆ
- capture ใช้ frame tracking ล่าสุด
- motion frames bake sticker แล้ว
- final composed PNG รวม AR sticker ด้วย

ไฟล์หลัก:

- `Assets/ARStickerBooth/Runtime/Frontend/BoothFrontendController.cs`
- `Assets/ARStickerBooth/Runtime/Frontend/BoothCameraCaptureService.cs`

### 4. MP4 motion clip

เพิ่ม FFmpeg sidecar encoder:

- encode image sequence เป็น `motion.mp4`
- ถ้า encode fail จะ fallback เป็น PNG frame clip
- backend upload รองรับ `motion_video_file`

ไฟล์หลัก:

- `Assets/ARStickerBooth/Runtime/Frontend/BoothFfmpegMotionEncoder.cs`
- `backend/api/src/server.js`

### 5. OpenCV attempts before current version

มีการลอง provider หลายรอบ:

- heuristic / MediaPipe placeholder
- `OpenCV+Unity` Haar face/eye bridge
- local head-color fallback

ปัญหาที่เจอ:

- Haar detect หน้าไม่เสถียร
- fallback สีผิว detect มือ/คอ/เสื้อเป็น face ได้
- face count สลับ `0` / `1`

provider เก่ายังอยู่แต่ไม่ได้ใช้เป็นตัวหลักแล้ว:

- `Assets/ARStickerBooth/Runtime/AR/OpenCvPlusUnityFaceTrackingProvider.cs`

### 6. Fallback provider: OpenCVForUnity YuNet + FaceMark

ยังเก็บ fallback provider ไว้:

- `Assets/ARStickerBooth/Runtime/AR/OpenCvForUnityYuNetFaceTrackingProvider.cs`

ใช้ models:

- `Assets/StreamingAssets/OpenCVForUnityExamples/dnn/face_detection_yunet_2023mar.onnx`
- `Assets/StreamingAssets/OpenCVForUnityExamples/face/lbfmodel.yaml`

เชื่อม assembly reference แล้ว:

- `Assets/ARStickerBooth/Runtime/PhotoBooth.Booth.asmdef`
- เพิ่ม reference: `EnoxSoftware.OpenCVForUnity`

ถ้า MediaPipe unavailable แล้ว fallback ทำงาน ใน runtime ควรเห็น log:

```text
OpenCVForUnity YuNet + FaceMark LBF Face Tracker initialized for photo booth AR overlay.
```

ถ้า `lbfmodel.yaml` โหลดไม่ได้ ระบบ fallback จะกลับไปใช้ YuNet 5 จุด และ log warning ว่า FaceMark ใช้ไม่ได้ในเฟรมนั้น

### 7. Current provider: MediaPipe Face Landmarker

ตอนนี้ controller จะลองใช้ MediaPipe ก่อน:

- `Assets/ARStickerBooth/Runtime/AR/MediaPipeFaceLandmarkerTrackingProvider.cs`

ใช้ package/model:

- `Packages/com.github.homuler.mediapipe`
- `Assets/StreamingAssets/MediaPipe/face_landmarker.task`

ข้อมูลที่ส่งเข้า `FaceTrack`:

- `NormalizedLandmarks` จาก 478-point MediaPipe Face Landmarker
- `NormalizedLandmarks3D`
- `FaceTransform`
- `FaceEulerDegrees`
- `HasReliableEyeLandmarks = true` เมื่อมี eye landmarks หลัก

ถ้า provider นี้ initialize ไม่ได้ จะ fallback ไป `OpenCvForUnityYuNetFaceTrackingProvider`

หมายเหตุเรื่อง Scene ตัวอย่าง:

- มี Scene `Assets/OpenCVForUnity/Examples/ContribModules/face/FaceMarkExample/FaceMarkExample.unity`
- Scene นี้ใช้ `MultiSource2MatHelper` เปิดกล้องและ preview ของตัวเอง
- Photo Booth ใช้ `BoothCameraCaptureService` / `WebCamTexture` flow เดิมอยู่แล้ว จึงไม่โหลด Scene ตัวอย่างตรง ๆ
- implementation ปัจจุบันดึง logic หลักจาก `FaceMarkExample.cs` มาใช้ใน provider ได้แก่ `Face.createFacemarkLBF()`, `loadModel(lbfmodel.yaml)`, และ `facemark.fit(...)`
- face rect ยังมาจาก YuNet เพื่อให้ detect หน้าเร็วและเสถียรกว่า cascade ของตัวอย่าง แล้วค่อยส่ง rect เข้า FaceMark

## Why The Glasses Are Tiny

ตอนนี้แว่นเล็กเพราะ scale เดิมถูกตั้งไว้สำหรับ detector/fallback เก่าที่ eye distance ไม่แม่น และมักให้ระยะตากว้างกว่าความจริง

ค่าปัจจุบันอยู่ที่:

```csharp
[SerializeField] private Vector2 sunglassesScale = new(1.25f, 1.25f);
```

ไฟล์:

- `Assets/ARStickerBooth/Runtime/Frontend/BoothFrontendController.cs`

และ default sticker ก็เป็น:

```csharp
sizeScale = new Vector2(1.25f, 1.25f)
```

ไฟล์:

- `Assets/ARStickerBooth/Runtime/AR/ArStickerOverlay.cs`

สูตรที่ใช้คำนวณความกว้างแว่นตอนนี้:

```csharp
width = eyeDistance * 1.75f * sticker.sizeScale.x
```

ก่อนแก้ ถ้า `sizeScale.x = 0.42`:

```text
actual width = eyeDistance * 1.75 * 0.42
             = eyeDistance * 0.735
```

แปลว่าแว่นกว้างน้อยกว่าระยะตาจริงอีก

พอเปลี่ยนมา YuNet:

- eye landmark เป็นตำแหน่งตาจริงมากขึ้น
- `eyeDistance` เล็กและแม่นกว่า fallback เก่า
- scale เดิม `0.42` เลยทำให้แว่นจิ๋ว

แก้แล้วโดยเปลี่ยน default เป็น `1.25` และเพิ่ม migration ใน `BoothFrontendController` เพื่อดัน scene เก่าที่ serialize ค่า `0.42` ค้างไว้ให้เป็น `1.25` ตอน runtime

## Recommended Fix

ใช้ค่า `sunglassesScale` ใน `BoothFrontendController`

ค่าที่แนะนำให้ลอง:

```csharp
[SerializeField] private Vector2 sunglassesScale = new(1.25f, 1.25f);
```

range ที่น่าจะเหมาะ:

```text
1.10 - 1.45
```

ถ้าแว่นใหญ่พอดีแต่ต่ำ/สูงไป ให้ปรับ:

```csharp
[SerializeField] private Vector2 sunglassesOffset;
```

แนวทาง:

- `x`: ขยับซ้าย/ขวา
- `y`: ขยับขึ้น/ลง ตามระบบ normalized offset ของ sticker

## Current Important Files

- `Assets/ARStickerBooth/Runtime/AR/OpenCvForUnityYuNetFaceTrackingProvider.cs`
- `Assets/ARStickerBooth/Runtime/AR/ArStickerOverlay.cs`
- `Assets/ARStickerBooth/Runtime/Frontend/BoothFrontendController.cs`
- `Assets/ARStickerBooth/Runtime/Frontend/BoothCameraCaptureService.cs`
- `Assets/ARStickerBooth/Runtime/PhotoBooth.Booth.asmdef`
- `Assets/StreamingAssets/OpenCVForUnityExamples/dnn/face_detection_yunet_2023mar.onnx`

## Notes / Next Steps

1. ถ้าแว่นยังไม่พอดี ให้ fine-tune `sunglassesScale` จากค่าเริ่มต้น `1.25`
2. ถ้าตำแหน่งยังเพี้ยน ให้ตรวจว่า `mirrorArOverlayHorizontally` ต้องเปิดหรือปิดตามกล้องจริง
3. live preview ตอนนี้มี pseudo 3D yaw/pitch โดยหมุน child `RawImage` ใน `ArPreviewOverlay` และบีบแกน X ตาม yaw
4. capture PNG / motion frame ยัง bake แบบ 2D roll อยู่ ถ้าต้องการ perspective warp ในไฟล์ output จริง ต้องเพิ่ม quad/mesh warp ตอน compose
5. หลังจาก scale/offset ลงตัว ค่อย clean provider เก่าที่ไม่ได้ใช้

## Change Log

### 2026-04-29

- เพิ่มไฟล์ `AR_TRACKING_STATUS.md` เพื่อ track งาน AR tracking
- เปลี่ยน provider หลักเป็น `OpenCvForUnityYuNetFaceTrackingProvider`
- เพิ่ม reference `EnoxSoftware.OpenCVForUnity` ใน `PhotoBooth.Booth.asmdef`
- แก้ `Rect` namespace collision ใน `OpenCvForUnityYuNetFaceTrackingProvider`
- ปรับ default sunglasses scale จาก `0.42` เป็น `1.25` เพื่อให้เหมาะกับ YuNet landmarks
- เพิ่ม runtime migration ใน `BoothFrontendController.UpgradeLegacySunglassesScale()` เพื่อแก้ scene/component ที่ยัง serialize ค่า scale เก่าไว้
- แก้ mapping landmark ตา/ปากจาก YuNet ให้เรียงตามตำแหน่งบนภาพจริงก่อนส่งเข้า overlay เพราะชื่อ `LeftEye`/`RightEye` ของ model อาจเป็น anatomical side ไม่ใช่ซ้าย/ขวาบนจอ
- เปลี่ยน live AR preview จากการ render texture ก้อนเดียวเป็น child `RawImage` GameObject ใต้ `ArPreviewOverlay` เช่น `ArPreviewSticker_00`
- live preview ใช้ตำแหน่ง, scale, และ 2D rotation จาก eye landmarks โดยตรง ส่วน capture/final PNG ยัง bake sticker ลง texture เหมือนเดิม
- หมายเหตุ: การหมุนตอนนี้เป็น 2D roll จากเส้นตา ยังไม่ใช่ yaw/pitch 3D เต็มรูปแบบ ถ้าต้องการเอียงแบบ 3D เมื่อหันซ้าย/ขวา ควรต่อด้วย FaceMark หรือ mesh/quad warp จาก landmarks เพิ่มเติม
- เปิด FaceMark LBF ใน provider หลัก (`enableFaceMark: true`) โดยใช้ `lbfmodel.yaml`
- เพิ่ม landmark mapping จาก FaceMark 68 จุดเข้า index กลางที่ overlay ใช้ (`33`, `263`, `1`, `10`, `61`, `152`, `291`)
- เพิ่ม pose estimation ด้วย `solvePnP` เพื่อสร้าง `FaceEulerDegrees` / `FaceTransform` สำหรับ yaw/pitch/roll ของหน้า
- live `ArPreviewSticker_00` ใช้ pitch/yaw จาก FaceMark pose และยังใช้ roll จากเส้นตาเพื่อให้แว่นเอียงตามหัว
- เพิ่ม `showOnlySunglassesSticker = true` ใน controller เพื่อซ่อน default crown/mustache ไว้ก่อน ลดอาการเหมือนแว่น/สติ๊กเกอร์ไปติดคาง
- หมายเหตุ: output ที่ bake ลง PNG/MP4 ยังเป็น 2D roll ไม่ใช่ perspective warp เต็มรูปแบบ
- แก้ compile error `CS0165` โดย initialize `faceEuler` / `faceTransform` ก่อนเรียก `TryEstimateFacePose(...)`
- เพิ่ม `ArPreviewStickerObject` เป็น component บน runtime child GameObject `ArPreviewSticker_00` เพื่อให้ปรับ manual offset / scale / rotation / face pose จาก Inspector ได้
- แก้ fallback y-position ของ eye/forehead/mouth anchors ให้ตรงระบบ normalized bottom-up; fallback ตาเดิมต่ำเกินไปจนทำให้ sticker หล่นไปแถวปาก/คางเมื่อ FaceMark landmark ไม่เสถียร
- เพิ่ม validation ของ eye pair ใน `ArStickerTransformResolver` ถ้า landmark ตาออกนอก bounds, ต่ำเกินไป, หรือระยะตาผิดปกติ จะ fallback กลับไปตำแหน่งตาจาก face bounds แทน
- แยก FaceMark debug landmarks ออกจาก landmarks ที่ใช้วาง sticker โดยเพิ่ม `DebugNormalizedLandmarks` ใน `FaceTrack`
- เปลี่ยน glasses placement ให้ใช้ YuNet eye landmarks เป็นหลัก แม้ FaceMark จะเปิดอยู่ เพราะ FaceMark LBF บางเฟรม fit จุดตาไปแถวปาก/คาง
- เพิ่ม FaceMark debug line GameObjects ใต้ `ArPreviewOverlay` (`FaceMarkDebugLine_00...`) เพื่อแสดงเส้น 68-point landmark ที่ FaceMark detect ได้ใน preview
- เปลี่ยน glasses placement อีกครั้งให้ใช้ face-bounds eye guide สำหรับ sunglasses โดยตรง เพราะทั้ง FaceMark และ YuNet eye landmarks ยัง drift เข้าใกล้ nose/mouth ในบางเฟรม; FaceMark lines ตอนนี้ใช้เป็น diagnostic overlay เท่านั้น
- เปิด FaceMark/MediaPipe green debug lines เป็นค่า default (`showFaceMarkDebugLines = true`) เพื่อช่วยตรวจ alignment ระหว่าง tracking, glasses, และ FBX mask
- เพิ่ม `MediaPipeFaceLandmarkerTrackingProvider` เป็น provider ตัวใหม่สำหรับ MediaPipe Face Landmarker 3D โดยเตรียม mapping ให้รองรับ 478 landmarks, normalized 3D landmarks, face transform matrix, และ `FaceEulerDegrees`
- `BoothFrontendController` จะลอง start MediaPipe provider ก่อน ถ้าไม่มี Homuler MediaPipe Unity Plugin / ไม่มี `StreamingAssets/MediaPipe/face_landmarker.task` / binding ยังไม่พร้อม จะ log warning แล้ว fallback ไป `OpenCvForUnityYuNetFaceTrackingProvider`
- เพิ่ม `FaceTrack.HasReliableEyeLandmarks` เพื่อแยก landmark ที่เชื่อถือได้จาก MediaPipe ออกจาก landmark diagnostic/fallback ของ OpenCV
- ปรับ sunglasses placement ให้ใช้ eye landmarks เฉพาะตอน provider ระบุว่า reliable เท่านั้น; OpenCV/FaceMark fallback ยังใช้ face-bounds eye guide เพื่อลดอาการแว่นติดจมูก/คาง
- ปรับ fallback face-bounds eye guide จาก `0.62` เป็น `0.74` ของ face height เพื่อยกแว่นขึ้นไปบริเวณตา
- เพิ่มการ sort eye points ตาม screen x หลัง mirror handling เพื่อให้ roll rotation ของแว่นไม่กลับด้านเมื่อ mirror preview
- เพิ่ม EditMode tests สำหรับ MediaPipe-style reliable eye landmarks และ fallback raised face guide
- ติดตั้ง Homuler MediaPipe Unity Plugin `v0.16.3` เป็น embedded local package ที่ `Packages/com.github.homuler.mediapipe`
- เพิ่ม dependency `com.github.homuler.mediapipe` ใน `Packages/manifest.json` ด้วย local path `file:com.github.homuler.mediapipe`
- เพิ่ม reference `Mediapipe.Runtime` ใน `PhotoBooth.Booth.asmdef`
- ดาวน์โหลดและวาง MediaPipe Face Landmarker model ที่ `Assets/StreamingAssets/MediaPipe/face_landmarker.task`
- ต่อ `MediaPipeFaceLandmarkerTrackingProvider` เข้ากับ API จริงของ Homuler (`FaceLandmarker`, `FaceLandmarkerOptions`, `FaceLandmarkerResult`) โดยใช้ `RunningMode.VIDEO`, CPU delegate, `maxFaces`, และ `outputFaceTransformationMatrixes = true`
- MediaPipe provider ตอนนี้สร้าง `FaceTrack` จาก landmark 478 จุดจริง, เติม `NormalizedLandmarks3D`, `FaceTransform`, `FaceEulerDegrees`, และ mark `HasReliableEyeLandmarks = true` เมื่อมี landmark ตา index `33` / `263`
- แก้ MediaPipe landmark Y flip: provider ส่งค่า `NormalizedLandmarks` กลับเป็นระบบ normalized bottom-up เหมือน OpenCV provider เพราะ `ArStickerOverlay` แปลงเป็น UI top-left pixel เองอยู่แล้ว
- เปิด debug landmark lines ใน scene `PhotoBooth.unity` (`showFaceMarkDebugLines: 1`) เพื่อใช้ตรวจ MediaPipe/OpenCV face landmarks ระหว่างปรับตำแหน่งแว่นและ FBX mask
- เทียบกับ `starter.md`: ไม่เปลี่ยนไปใช้ Python + UDP เพราะ project ตอนนี้มี Unity-side MediaPipe แล้ว และ booth capture ใช้ `WebCamTexture` เดียวกันอยู่ การอยู่ใน Unity ลด process boundary, latency, packet loss, และปัญหา sync ระหว่าง preview/capture
- Borrow สิ่งที่ useful จาก `starter.md` เข้ามาใน Unity path แทน:
  - เพิ่ม `ArTrackingStabilizer` สำหรับ exponential smoothing ของ face bounds, landmarks, confidence, และ face Euler
  - เพิ่ม neutral calibration baseline ช่วงแรกของการเห็นหน้า เพื่อให้ yaw/pitch/roll เป็น relative จาก pose หน้าตรงตอนเริ่ม
  - เพิ่ม live debug telemetry (`ArDebugTelemetry`) ใต้ `ArPreviewOverlay` แสดง provider, face count, calibration, FPS, confidence, scale, eye distance, pitch/yaw/roll
  - เพิ่ม `ArPreviewFaceAnchor` GameObject ใต้ `ArPreviewOverlay` เพื่อเป็น anchor กลางสำหรับ attach/debug object ตามหน้าใน scene hierarchy
- `TrackCurrentFrameAsync()` ตอนนี้ส่ง raw provider frame ผ่าน stabilizer ก่อนนำไปใช้กับ preview overlay, capture PNG, motion frames, และ MP4
- ถ้า `faces=0` stabilizer จะ reset, sticker/FaceAnchor จะ hide, แต่ debug telemetry ยังแสดงสถานะ no-face เพื่อช่วยไล่ปัญหา
- เพิ่ม `ArPreviewFaceRig` สำหรับ modeler-facing face rig:
  - Runtime จะสร้าง GameObject `ArPreviewFaceRig` ใต้ `ArPreviewOverlay`
  - มี named anchors: `HeadCenter`, `LeftEye`, `RightEye`, `Nose`, `Mouth`, `Forehead`, `LeftCheek`, `RightCheek`, `Chin`
  - anchors ใช้ MediaPipe normalized landmarks + depth (`NormalizedLandmarks3D.z`) และหมุนด้วย `FaceEulerDegrees`
  - modeler สามารถ parent 3D/2D prefab ใต้ anchor เหล่านี้เพื่อทดลอง glasses, mask, nose/cheek effects ได้
  - มี wireframe face mesh guide เปิดด้วย `showModelerFaceMeshWireframe` เพื่อ debug ว่า landmark surface ตามหน้าจริงหรือไม่
- หมายเหตุ: `ArPreviewFaceRig` ตอนนี้เป็น preview/modeler rig บน UI overlay ยังไม่ใช่ full skinned 3D face mesh renderer พร้อม texture/occlusion; ขั้นถัดไปถ้าต้องการ TikTok-style เต็มกว่านี้คือเพิ่ม actual Unity `MeshFilter` face surface + depth-only occlusion material สำหรับ 3D model
- เพิ่ม FBX face mesh asset จาก user เข้า project:
  - source: `/Users/ezreal/Downloads/FaceAssets_v310/adjusted_3dFaceMask_v310/3d_face_quads_v310.fbx`
  - Unity asset: `Assets/Resources/ARStickerBooth/FaceAssets/3d_face_quads_v310.fbx`
- เพิ่ม `ArPreviewFaceModelRig` สำหรับ render 3D FBX model ที่ snap ตามหน้าแบบ filter:
  - Runtime สร้าง `ArModelOverlay` เป็น transparent `RawImage` ซ้อนเหนือ `CameraPreview`
  - Runtime สร้าง `ArFaceModelOverlayCamera` + `RenderTexture` เพื่อ render 3D model ทับ webcam UI ได้จริง
  - โหลด default FBX ด้วย `Resources.Load("ARStickerBooth/FaceAssets/3d_face_quads_v310")`
  - สร้าง world-space anchors: `HeadCenter`, `LeftEye`, `RightEye`, `Nose`, `Mouth`, `Forehead`, `LeftCheek`, `RightCheek`, `Chin`
  - วาง anchor จาก MediaPipe landmarks ผ่าน `Camera.ViewportToWorldPoint(...)`
  - หมุน model ด้วย `FaceEulerDegrees` และ scale ด้วยระยะ `LeftEye` ถึง `RightEye`
  - ถ้า `faces=0` จะ hide model overlay
- เพิ่ม toggle ใน controller: `enableTracked3dFaceModel` เพื่อเปิด/ปิด 3D FBX face model tracking แยกจาก debug wireframe
- หมายเหตุ: FBX ตอนนี้เป็น rigid tracked face model/mask ที่ snap, rotate, scale ตามหน้า ยังไม่ deform vertex-by-vertex ตาม 478 landmarks; ถ้าต้องการ skin mesh deform จริง ต้องเพิ่ม mesh retarget/deformation step ต่อจากนี้
- ปรับ default ให้ debug line กลับมาแสดงเพื่อแก้ alignment:
  - `showFaceMarkDebugLines = true` ใน scene `PhotoBooth.unity`
  - `showModelerFaceMeshWireframe = true` ใน `BoothFrontendController`
  - `enableTracked3dFaceModel = true` ยังเปิดอยู่ เพื่อให้ `3d_face_quads_v310.fbx` แสดงและ track ตามหน้า
  - ถ้าต้อง debug landmarks อีกครั้ง ให้เปิด `showFaceMarkDebugLines` หรือ `showModelerFaceMeshWireframe` จาก Inspector
- แก้ AR preview loop ที่ถูก cap ไว้ประมาณ 10 FPS:
  - เพิ่ม `arPreviewUpdateIntervalMs = 33` ใน `BoothFrontendController`
  - เปลี่ยน preview loop จาก delay `100ms` เป็น configurable delay `33ms` เพื่อ target ประมาณ 30 FPS
- เพิ่ม diagnostics ให้ `ArPreviewFaceModelRig`:
  - log เมื่อโหลด FBX สำเร็จ: `PhotoBooth AR face model loaded...`
  - log เมื่อ model แสดงครั้งแรก: `PhotoBooth AR face model visible...`
  - ลด default RenderTexture จาก `1024x1024` เป็น `768x768` เพื่อลด cost
  - เพิ่ม default `modelScaleMultiplier` เป็น `1.35` เพื่อให้ FBX เห็นง่ายขึ้นตอนทดสอบ
- แก้ alignment ของ FBX face model:
  - เปลี่ยน overlay render จาก perspective camera เป็น orthographic camera เพื่อให้ตำแหน่งบน preview map ตรงกว่า
  - เพิ่ม anchor `FaceMaskCenter` ระหว่าง eye center และ nose แทนการยึด model กับ `HeadCenter`
  - default model attach เปลี่ยนเป็น `FaceMaskCenter`
  - เพิ่ม `modelLocalPositionOffset = (0, 0.08, 0)` สำหรับดัน mask ขึ้นเล็กน้อยตาม pivot ของ FBX
- ปรับ `ArPreviewFaceModelRig` ให้เหมาะกับ workflow ใน Hierarchy:
  - Runtime host จะถูกสร้างใต้ `CaptureScreen > ArPreviewOverlay > ArPreviewFaceModelRig` ไม่ใช่แยกไว้ใต้ controller root
  - รองรับ OpenCV fallback ด้วย ไม่บังคับว่าต้องมี MediaPipe 468/478 landmarks ก่อนแสดง model
  - ถ้า landmark บางจุดไม่มี จะใช้ `NormalizedBounds` เป็น fallback สำหรับ eye/nose/mouth/cheek/chin anchors
  - ทำให้ model/anchor/camera GameObjects อยู่รวมกันใต้ overlay branch ที่ modeler inspect อยู่
- เพิ่ม prefab assignment workflow สำหรับ 3D face model:
  - เพิ่ม `faceModelPrefab` ใน `BoothFrontendController` เพื่อให้ลาก `3d_face_quads_v310` จาก Project ไปใส่ใน Inspector ได้โดยตรง
  - `BoothFrontendController` ส่ง prefab นี้เข้า `ArPreviewFaceModelRig.Initialize(...)`
  - `ArPreviewFaceModelRig` จะใช้ `faceModelPrefab` ก่อน ถ้าไม่ได้ assign ค่อย fallback ไป `Resources.Load("ARStickerBooth/FaceAssets/3d_face_quads_v310")`
  - ถ้าไม่มีทั้ง prefab และ resource จะ log: `PhotoBooth AR face model not assigned...`
- แก้ yaw direction ของ preview filter:
  - เดิม glasses / `ArPreviewFaceAnchor` / `ArPreviewFaceRig` / `ArPreviewFaceModelRig` ใช้ `-FaceEulerDegrees.y`
  - ทำให้ตอนหันหัวซ้าย filter หันขวา
  - เปลี่ยนเป็นใช้ `+FaceEulerDegrees.y` ให้ glasses และ FBX mask หมุนทิศเดียวกับหน้าใน preview
- แก้ green debug lines ไม่แสดงตอน fallback เป็น OpenCV FaceMark:
  - เดิม `UpdateFaceMarkDebugLines(...)` ต้องการ `DebugNormalizedLandmarks.Length >= 468`
  - OpenCV FaceMark LBF ให้ debug landmarks 68 จุด ทำให้โดน skip ทั้งหน้า
  - เพิ่ม topology สำหรับ 68-point FaceMark: jaw, eyebrows, nose, eyes, outer/inner mouth
  - เพิ่ม bounds check ใน `AddFaceMarkPolyline(...)` เพื่อไม่ให้ mixed provider index ทำให้ line renderer fail
- เปิด green debug lines กลับใน scene:
  - `Assets/Scenes/PhotoBooth.unity` เคย serialize ค่า `showFaceMarkDebugLines: 0` และ `showModelerFaceMeshWireframe: 0` ทับ default ใน code
  - ปรับ `showFaceMarkDebugLines: 1` เพื่อให้เห็น green FaceMark/face-bounds debug ตอน Play
  - `showModelerFaceMeshWireframe` ถูกปิดกลับในขั้นถอย runtime เป็น 2D-first เพราะเป็นส่วนของ 3D/modeler rig
- เพิ่ม green face-bounds fallback:
  - ก่อนหน้านี้ green face outline จะวาดเฉพาะเมื่อ `DebugNormalizedLandmarks` มี MediaPipe 468+ จุด หรือ OpenCV FaceMark 68 จุด
  - ถ้า YuNet detect หน้าได้ (`faces=1`) แต่ FaceMark fit ไม่สำเร็จ เช่น มือบังหน้า/มุมหน้าเพี้ยน จะไม่มีเส้นรอบหน้าแม้ยังมี face
  - `UpdateFaceMarkDebugLines(...)` ตอนนี้ fallback ไปวาด rectangle จาก `FaceTrack.NormalizedBounds` เพื่อให้ยังเห็นกรอบเขียวรอบ face detection เสมอ
- ถอย runtime กลับเป็น 2D-first preview ชั่วคราว:
  - ปิด `enableTracked3dFaceModel`, `enableModelerFaceRig`, และ `showModelerFaceMeshWireframe` ทั้ง default ใน `BoothFrontendController` และค่า serialize ใน `PhotoBooth.unity`
  - เหตุผล: หลังใส่ 3D FBX/model rig แล้ว alignment เพี้ยนจาก flow ช่วงกลางวันที่ใช้ 2D glasses + FaceMark debug เป็นหลัก
  - ยังไม่ลบไฟล์ 3D/FBX ออก เพื่อให้เปิดกลับมาทดสอบได้ทีหลัง แต่ runtime ตอนนี้จะไม่สร้าง `ArModelOverlay`, `ArPreviewFaceModelRig`, หรือ modeler wireframe โดย default
- เพิ่ม MediaPipe fallback diagnostics:
  - screenshot แสดงว่า runtime start `MediaPipe Face Landmarker (3D)` ก่อน แล้ว fallback ไป `OpenCVForUnity YuNet + FaceMark LBF`
  - เพิ่ม `mediaPipeFallbackReason` เพื่อเก็บ exception type/message เมื่อ MediaPipe start ไม่ผ่าน
  - log เพิ่มเป็น `PhotoBooth AR MediaPipe fallback detail:` พร้อม `exception.ToString()` แบบ `Debug.Log` ปกติ เพื่อให้เห็น stack/error แม้ warning row ไม่เด่นใน Console
  - AR debug telemetry จะแสดงบรรทัด `MediaPipe fallback: ...` เมื่อกำลังใช้ OpenCV เพราะ MediaPipe fail
- แก้ MediaPipe startup fail ที่ `Glog.Initialize(...)`:
  - Console พบ `Mediapipe.MediaPipeException: MediaPipe Aborted` จาก `Mediapipe.Glog.Initialize(...)`
  - เปลี่ยน `MediaPipeFaceLandmarkerTrackingProvider` ให้ init glog แบบ static one-time ต่อ process แทน field ต่อ provider instance
  - ถ้า glog init fail จะ log warning แล้วไปต่อ เพื่อให้ `FaceLandmarker.CreateFromOptions(...)` เป็นตัวตัดสินจริงว่า MediaPipe ใช้ได้ไหม
  - ไม่เรียก `Glog.Shutdown()` ใน provider dispose แล้ว เพื่อลดปัญหา init/shutdown ซ้ำใน Unity Editor play sessions
- เปลี่ยน debug guide จาก green line เป็น 3D face mask:
  - เปิด `enableTracked3dFaceModel` กลับมา และปิด `showFaceMarkDebugLines` ใน `PhotoBooth.unity`
  - ใช้ `3d_face_quads_v310` เป็น guide mesh ที่ snap ตามหน้าแทนเส้นเขียว เพื่อให้ modeler วาง/ปรับ content กับหน้าได้ง่ายขึ้น
  - เพิ่ม `showTracked3dFaceGuideModel` ใน `BoothFrontendController`
  - `ArPreviewFaceModelRig.ShowGuideModel = false` จะซ่อนตัว guide mesh แต่ยังให้ `ArFaceModelRigRoot` และ anchors ทำงานต่อ เพื่อโชว์เฉพาะ content ที่ parent/attach ไว้กับ anchor ต่าง ๆ
- ปรับ scale ของ 3D face guide:
  - เพิ่ม `tracked3dFaceGuideScale` ใน `BoothFrontendController` เพื่อปรับขนาด guide mesh จาก Inspector
  - ค่า default เพิ่มจาก scale multiplier `1.35` เป็น `2.35` เพราะ `3d_face_quads_v310` เล็กเกินเมื่อใช้เป็น face guide
  - `ArPreviewFaceModelRig.ModelScaleMultiplier` รับค่าจาก controller ทุก frame เพื่อให้ปรับ Play Mode แล้วเห็นผลทันที
- แก้ 3D face guide กลับด้าน / mirror:
  - เพิ่ม `mirrorTracked3dFaceGuideX` และ `invertTracked3dFaceGuideYaw` ใน `BoothFrontendController`
  - ค่า scene default เปิดทั้งคู่ (`1`) เพื่อแก้กรณี mesh/preview กลับด้านกับ MediaPipe face pose
  - เพิ่ม `tracked3dFaceGuideLocalOffset`, `tracked3dFaceGuideEulerOffset`, และ `tracked3dFaceGuideLocalScale` ให้ปรับตำแหน่ง/มุม/สัดส่วน guide mesh จาก Inspector ได้โดยไม่ต้องแก้ code
  - `ArPreviewFaceModelRig` เลิก clamp local scale เป็นค่าบวกอย่างเดียวแล้ว เพื่อให้ mirror scale ใช้งานได้จริง
- เพิ่ม Editor preview สำหรับ 3D face guide:
  - เพิ่ม `showEditorTracked3dFaceGuidePreview` ใน `BoothFrontendController`
  - ตอนไม่ได้ Play, `OnValidate()` จะสร้าง/อัปเดต `ArModelOverlay` และ `ArPreviewFaceModelRig` ใต้ `ArPreviewOverlay` ด้วย fake centered face pose
  - ทำให้ `3d_face_quads_v310` และ anchors โผล่ใน Hierarchy/Scene view ตอน Edit Mode เพื่อให้ modeler ปรับ Transform/content ได้ก่อนกด Play
  - `EnsureArPreviewFaceModelRig()` และ `EnsureArModelOverlay()` จะ reuse GameObject ที่มีอยู่แล้วใน scene แทนการสร้างซ้ำ
- แก้ state ค้างของ 3D face guide ใน Scene:
  - Scene เคยมี `ArPreviewFaceModelRig` / `FaceMaskModel_3d_face_quads_v310` ชื่อเก่าค้างจาก runtime/editor preview รอบก่อน ทำให้ object ที่เลือกใน Hierarchy ไม่ใช่ตัวเดียวกับตัวที่ tracking update ตอน Play
  - `EnsureArPreviewFaceModelRig(editorPreview: true)` จะ rename/reuse legacy `ArPreviewFaceModelRig` เป็น `ArPreviewFaceModelRig_EDITOR_PREVIEW`
  - ตอน Play จะใช้ rig แยกชื่อ `ArPreviewFaceModelRig_RUNTIME` และซ่อน editor preview ก่อน เพื่อไม่ให้ RenderTexture/camera ของ editor preview ทับ runtime
  - `ArPreviewFaceModelRig` จะ reuse `ArFaceModelRigRoot`, anchors, และ `FaceMaskModel_3d_face_quads_v310*` ที่มีอยู่แล้วก่อน instantiate ใหม่ เพื่อลด duplicate หลัง Unity domain reload
  - หมายเหตุ: `FaceMaskModel_3d_face_quads_v310_*` เป็น child ของ `FaceMaskCenter`; local transform ของตัว mesh จะดูนิ่งเพื่อให้ modeler ปรับ offset/scale ได้ ส่วนตำแหน่งจริงที่ขยับตามหน้าอยู่ที่ parent anchor `FaceMaskCenter`
- ปรับ RawImage overlay ให้ lock กับกล้อง:
  - เพิ่ม `AlignArOverlaysToCameraPreview()` เพื่อให้ `ArModelOverlay` และ `ArPreviewOverlay` copy `RectTransform` จาก `CameraPreview` ทุกครั้งที่ ensure/validate
  - sync ค่า anchor, pivot, anchored position, size, rotation, scale และ parent ให้ตรงกับ `CameraPreview`
  - จัด sibling order เป็น `CameraPreview` -> `ArModelOverlay` -> `ArPreviewOverlay` เพื่อให้ 3D RenderTexture และ 2D debug/sticker overlay ซ้อนบนภาพกล้อง แต่ไม่ดันไปทับ UI ที่อยู่ถัดไป
  - `EnsureArPreviewOverlay()` และ `EnsureArModelOverlay()` จะ reuse RawImage ที่มีอยู่ใน scene และ re-align ทันที แทนการเชื่อค่า RectTransform เก่าที่อาจเพี้ยนจากการปรับมือ
  - `UpdateArPreviewOverlay(...)` เรียก align ซ้ำระหว่าง preview loop เพื่อกันกรณี layout/scene object ถูกปรับระหว่าง Play Mode
  - ปรับ `Assets/Scenes/PhotoBooth.unity` ให้ sibling order ของ CaptureScreen เป็น `CameraPreview`, `ArModelOverlay`, `ArPreviewOverlay` แล้วจึงตามด้วย UI อื่น ๆ เพื่อให้ Edit Mode เห็น stack ถูกตั้งแต่เปิด Scene
- เพิ่มโหมด preserve transform สำหรับ 3D guide model:
  - เพิ่ม `preserveTracked3dFaceGuideSceneTransform` ใน `BoothFrontendController` และเปิดไว้ใน `PhotoBooth.unity`
  - ถ้า `FaceMaskModel_3d_face_quads_v310_*` มีอยู่ใน Scene อยู่แล้ว `ArPreviewFaceModelRig` จะไม่เขียนทับ `localPosition`, `localRotation`, หรือ `localScale` ของ model ตอน tracking
  - parent anchor `FaceMaskCenter` ยังขยับ/หมุน/scale ตามหน้าอยู่เหมือนเดิม ดังนั้น modeler สามารถปรับ local transform ของ guide/content ใน Scene แล้วค่าไม่เด้งกลับตอนเริ่ม Play
  - ถ้า model ถูก instantiate ใหม่จาก prefab runtime จะยังใช้ค่า `tracked3dFaceGuideLocalOffset`, `tracked3dFaceGuideEulerOffset`, และ `tracked3dFaceGuideLocalScale` เป็น fallback
- เพิ่มการ copy calibration จาก editor preview ไป runtime:
  - ก่อนหน้านี้ `ArPreviewFaceModelRig_RUNTIME` ถูกสร้างแยกจาก `ArPreviewFaceModelRig_EDITOR_PREVIEW` ทำให้ local transform ที่ modeler จัดไว้ใน Edit Mode ไม่ถูกนำไปใช้กับ runtime object
  - `BoothFrontendController` ตอนสร้าง/reuse runtime rig จะอ่าน local position/rotation/scale จาก guide model ใต้ `ArPreviewFaceModelRig_EDITOR_PREVIEW`
  - จากนั้นส่งค่าไปที่ `ArPreviewFaceModelRig_RUNTIME` ผ่าน `ApplyGuideModelLocalTransform(...)` และตั้งให้ runtime preserve transform นั้นระหว่าง tracking
  - ผลลัพธ์คือให้ปรับ `FaceMaskModel_3d_face_quads_v310_*` ใต้ editor preview ใน Edit Mode แล้วกด Play ได้ โดย runtime จะเริ่มจาก offset เดียวกัน
