# Unity AR Plan: Remove 3D AR, Keep 2D Overlay

## Goal

ถอดเส้นทาง 3D AR face model ออกจาก Unity runtime เพื่อให้ระบบกลับไปโฟกัสกับ 2D AR overlay ที่เสถียรกว่า โดยคง AR แว่น/สติกเกอร์ 2D ที่ snap กับ face landmarks ไว้เป็นฟีเจอร์หลัก

## Current Decision

- ไม่ใช้ 3D face model ใน Unity แล้ว
- ใช้ 2D AR overlay เป็น production path
- เก็บ code/document ของ 3D ไว้เป็น reference ได้ แต่ต้องไม่ให้ทำงานใน runtime
- งาน 3D ในอนาคตจะย้ายไปทำบน Three.js แทน

## Implementation Status

Implemented in the current worktree:

- Added a controller-level 3D feature gate: `UseUnity3dAr = false`
- Disabled serialized 3D defaults in `BoothFrontendController`
- Stopped baking 3D face model into captured textures
- Stopped updating live 3D face model rig during preview
- Scene flags for 3D face guide/model/parts were set to false
- Auto-built UI no longer creates `ArModelOverlay`
- EditMode tests were trimmed back to the 2D overlay/sticker path

Kept for rollback/reference:

- `ArPreviewFaceModelRig.cs`
- 3D calibration notes
- 3D-to-Three.js migration plan

## Unity Scope

ให้ Unity เหลือเฉพาะ:

- face tracking provider
- 2D face landmarks/debug lines
- 2D sticker overlay เช่น แว่น
- bake 2D sticker ลง capture PNG
- bake 2D sticker ลง motion frames
- composed preview ที่มี 2D overlay

ให้ปิดหรือถอดออกจาก runtime:

- 3D face guide model
- 3D face part model bindings
- 3D model RenderTexture overlay
- calibration UI/log สำหรับ 3D model
- scene objects ที่เป็น `ArPreviewFaceModelRig` หรือ model overlay runtime ถ้าไม่ได้ใช้กับ 2D

## Recommended Implementation Steps

1. Disable 3D AR from Inspector first

   ใน `BoothFrontendController` ให้ตั้งค่า runtime ของ 3D เป็นปิดทั้งหมดก่อน เพื่อ verify ว่า 2D overlay ยังทำงานครบ:

   - `Enable Tracked 3d Face Model = false`
   - `Show Tracked 3d Face Guide Model = false`
   - `Enable Tracked 3d Face Parts = false`
   - clear `Tracked 3d Face Parts` array ถ้าไม่ใช้แล้ว

2. Verify 2D overlay path

   เทสต์บน Capture screen:

   - เปิดกล้องแล้วเห็น face debug/landmarks
   - แว่น 2D เกาะตา
   - หันซ้าย/ขวาแล้วแว่นตาม rotation
   - เอียงคอแล้วแว่น rotate ตาม roll
   - ถ้าไม่มีหน้า แว่นต้องหาย

3. Verify capture output

   Capture จริง 1 รอบ แล้วตรวจ:

   - raw/capture มี 2D overlay
   - motion frames มี 2D overlay
   - composed preview มี 2D overlay
   - ไม่มี 3D model โผล่ในภาพใด ๆ

4. Remove runtime references after verification

   หลัง confirm ว่า 2D path stable แล้ว ค่อยให้ GLM ถอดโค้ด 3D runtime แบบ conservative:

   - ลบหรือ bypass call ไปยัง `ApplyTracked3dFaceModelToTexture`
   - ลบหรือ bypass call ไปยัง `UpdateArPreviewFaceModelRig`
   - ไม่สร้าง `ArPreviewFaceModelRig_RUNTIME`
   - ไม่สร้าง `ArModelOverlay` ถ้าไม่ได้ใช้กับ 2D overlay แล้ว
   - คง `ArPreviewOverlay` ไว้ เพราะ 2D sticker/debug ใช้อยู่

5. Keep tests focused on 2D

   Test ที่ควรเหลือ:

   - 2D sticker center follows eye landmarks
   - 2D sticker mirrors correctly if mirror mode is enabled
   - 2D sticker hides when no face
   - sticker bakes into composed image
   - motion frame bake still works

   Test ที่ควรลบหรือ disable:

   - 3D projection geometry tests
   - 3D face model rig tests
   - 3D face part anchor tests ถ้าไม่มี runtime ใช้แล้ว

## Files/Areas To Review

- `Assets/ARStickerBooth/Runtime/Frontend/BoothFrontendController.cs`
  - keep 2D AR preview and bake flow
  - remove/bypass 3D model flow

- `Assets/ARStickerBooth/Runtime/AR/ArStickerOverlay.cs`
  - keep as primary AR overlay renderer

- `Assets/ARStickerBooth/Runtime/Frontend/ArPreviewStickerObject.cs`
  - keep for live 2D preview sticker objects

- `Assets/ARStickerBooth/Runtime/Frontend/ArPreviewFaceAnchor.cs`
  - keep if still used by 2D face anchor/debug behavior

- `Assets/ARStickerBooth/Runtime/Frontend/ArPreviewFaceModelRig.cs`
  - candidate for removal or archive after 2D path is confirmed

## Acceptance Criteria

- Unity Console has no 3D AR compile/runtime errors
- Capture screen still shows 2D AR glasses tracking the face
- 2D overlay remains stable for front face, yaw left/right, and head tilt
- Capture PNG, motion frames, and composed preview include 2D overlay
- No 3D model, 3D RenderTexture, or 3D calibration log appears during normal booth flow

## Rollback Strategy

Before deleting 3D files, keep one commit or stash with the current 3D implementation. If future work needs Unity 3D again, restore from that snapshot. For now, the production Unity path should be 2D-only.
