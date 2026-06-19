using PhotoBooth.Booth.Frontend;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace PhotoBooth.Booth.Editor.Frontend
{
    public static class MrkremeEditableUiInstaller
    {
        [MenuItem("Photo Booth/UI/Install Editable MRKREME UI In Scene", priority = 200)]
        public static void InstallEditableUiInScene()
        {
            var controller = UnityEngine.Object.FindFirstObjectByType<BoothFrontendController>();
            if (controller == null)
            {
                var host = new GameObject("BoothFrontendController");
                Undo.RegisterCreatedObjectUndo(host, "Create Booth Frontend Controller");
                controller = host.AddComponent<BoothFrontendController>();
            }
            else
            {
                Undo.RegisterFullObjectHierarchyUndo(controller.gameObject, "Rebuild Editable MRKREME UI");
            }

            controller.BuildEditableUiInScene();
            EditorUtility.SetDirty(controller);
            EditorSceneManager.MarkSceneDirty(controller.gameObject.scene);
            Selection.activeGameObject = controller.gameObject;
            Debug.Log("Installed editable MRKREME UI in the current scene.");
        }

        [MenuItem("Photo Booth/UI/Install Editable Voucher Entry Screen In Scene", priority = 201)]
        public static void InstallEditableVoucherEntryScreenInScene()
        {
            var controller = UnityEngine.Object.FindFirstObjectByType<BoothFrontendController>();
            if (controller == null)
            {
                EditorUtility.DisplayDialog("Photo Booth", "BoothFrontendController was not found in the current scene.", "OK");
                return;
            }

            Undo.RegisterFullObjectHierarchyUndo(controller.gameObject, "Install Editable Voucher Entry Screen");
            controller.EnsureEditableVoucherEntryScreenInScene();
            EditorUtility.SetDirty(controller);
            EditorSceneManager.MarkSceneDirty(controller.gameObject.scene);

            var voucherScreen = controller.transform.Find("BoothFrontendCanvas/EditableMrkremeFrontendRoot/VoucherEntryScreen");
            Selection.activeGameObject = voucherScreen != null ? voucherScreen.gameObject : controller.gameObject;
            Debug.Log("Installed editable VoucherEntryScreen in the current scene.");
        }
    }
}
