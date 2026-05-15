# 3D AR Migration Plan: Unity to Three.js

## Goal

ย้ายงาน 3D AR face model ออกจาก Unity ไปทำบน Three.js เพื่อควบคุม camera, projection, render loop, asset loading, และ calibration ได้ตรงกว่า โดยให้ Unity คงทำงาน Photo Booth และ 2D overlay ตามเดิม

## Direction

- Unity: ใช้เป็น Photo Booth runtime หลักและ 2D AR overlay
- Three.js: ใช้สำหรับ 3D AR experiment/runtime แยก
- ไม่ผูก 3D Three.js เข้ากับ Unity จนกว่าระบบ 3D บนเว็บจะนิ่ง

## Recommended Architecture

### Option A: Standalone Three.js Web AR Prototype

เหมาะสำหรับเริ่มต้น

- ใช้ browser webcam
- ใช้ MediaPipe Face Landmarker บนเว็บ
- ใช้ Three.js render 3D model overlay
- calibrate model ใน browser
- export ค่า calibration เป็น JSON

ข้อดี:

- iterate เร็ว
- debug camera/projection ง่าย
- ไม่กระทบ Unity booth

ข้อเสีย:

- ยังไม่รวมเข้ากับ production flow

### Option B: Three.js Sidecar Renderer

เหมาะหลัง prototype stable แล้ว

- Unity ส่งภาพหรือ frame data ออกไป
- Three.js render 3D overlay เป็น image/video
- Unity หรือ backend รวมผลภายหลัง

ข้อดี:

- ใช้ Three.js สำหรับ 3D โดยเฉพาะ
- Unity ไม่ต้องแบก 3D AR complexity

ข้อเสีย:

- ต้องออกแบบ data exchange และ latency

### Option C: Full Web Photo Booth

เหมาะถ้าจะย้ายทั้ง product ไป web ในอนาคต

- camera, 2D overlay, 3D overlay, capture, preview อยู่บนเว็บทั้งหมด
- Unity ไม่เกี่ยวกับ AR แล้ว

ข้อดี:

- stack เดียวสำหรับ AR
- deploy/test ง่ายขึ้นใน browser

ข้อเสีย:

- scope ใหญ่ที่สุด

## Suggested Starting Point

เริ่มจาก Option A ก่อน

เป้าหมาย prototype แรก:

- เปิด webcam ใน browser ได้
- detect face ด้วย MediaPipe Face Landmarker
- วาด 2D debug landmarks ได้
- load 3D model ด้วย Three.js
- attach model กับ face anchor เช่น Eyes, Nose, Mouth หรือ FaceCenter
- support yaw, pitch, roll
- มี control panel สำหรับ offset/rotation/scale
- export calibration JSON

## Three.js Data Model

ควรเก็บ calibration เป็น JSON ประมาณนี้:

```json
{
  "modelId": "face-mask-v1",
  "anchor": "FaceMaskCenter",
  "positionOffset": { "x": 0, "y": 0, "z": 0 },
  "rotationOffset": { "x": 0, "y": 180, "z": 0 },
  "scale": { "x": 1, "y": 1, "z": 1 },
  "mirrorX": true,
  "yawMultiplier": 1,
  "pitchMultiplier": 1,
  "rollMultiplier": 1
}
```

## Face Anchors To Support

Minimum anchors:

- `FaceCenter`
- `FaceMaskCenter`
- `Eyes`
- `LeftEye`
- `RightEye`
- `Nose`
- `Mouth`
- `Forehead`
- `Chin`

Recommended model mapping:

- glasses: `Eyes`
- left eye item: `LeftEye`
- right eye item: `RightEye`
- nose item: `Nose`
- mouth/mustache item: `Mouth`
- hat/crown item: `Forehead`
- full face mask: `FaceMaskCenter`

## Prototype Milestones

### Milestone 1: Webcam + Landmarks

- Browser opens webcam
- MediaPipe Face Landmarker detects one face
- Draw landmark points/face outline on canvas
- Confirm head yaw, pitch, roll values are available

Pass criteria:

- landmarks follow face in real time
- no 3D model yet

### Milestone 2: Basic Three.js Overlay

- Add Three.js scene over webcam
- Load one GLB/GLTF model
- Attach model to `FaceMaskCenter`
- Match render canvas size to webcam display size

Pass criteria:

- model appears on top of camera feed
- model follows face center

### Milestone 3: Rotation Calibration

- Apply yaw/pitch/roll from face transform
- Add multipliers for yaw, pitch, roll
- Add UI controls for rotation offset

Pass criteria:

- yaw left/right follows correctly
- head tilt/roll follows correctly
- pitch is acceptable for slight up/down movement

### Milestone 4: Position/Scale Calibration

- Add UI controls for position offset and scale
- Save/load calibration JSON
- Test different face distances from camera

Pass criteria:

- model stays attached for front face, yaw, roll, and small pitch changes
- calibration can be restored after refresh

### Milestone 5: Capture Output

- Capture combined webcam + 3D overlay as PNG
- Optional: capture short motion clip
- Export final image/video for backend testing

Pass criteria:

- captured output matches live preview

## Asset Guidelines

- Prefer GLB/GLTF
- Keep model pivot at the intended anchor point
- For glasses, pivot should be center between lenses
- For nose, pivot should be nose bridge or tip depending on design
- For mouth/mustache, pivot should be mouth center
- For full face mask, pivot should be face center or nose bridge area
- Normalize model scale before runtime calibration

## Risks

- Webcam mirroring can invert X if not handled consistently
- MediaPipe coordinate space and Three.js coordinate space differ
- Full face masks are harder than separate parts
- Roll and pitch often need separate multipliers
- Browser camera resolution can change between devices

## Recommendation

Use Unity 2D AR for the current booth product. Build Three.js 3D AR as a separate prototype first. Only integrate it into production after the Three.js prototype passes live preview and capture-output tests consistently.
