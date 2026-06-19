using System.Collections;
using UnityEngine;
using UnityEngine.Networking;

public class testPhoto : MonoBehaviour
{
    [Header("Image URL")]
    [SerializeField] private string imageUrl = "https://api.wajanapir.com/v1/assets/composed/featured";

    [Header("Sticker Template")]
    [SerializeField] private Texture2D baseTemplate;
    [SerializeField] private RectInt targetRectPixels = new RectInt(315, 449, 816, 1170);
    [SerializeField] private bool preserveAspectRatio = true;
    [SerializeField] private bool flipSourceY;

    [Header("Target Material")]
    [SerializeField] private Material targetMaterial;

    [Header("Refresh Settings")]
    [SerializeField] private float refreshSeconds = 600f; // 10 นาที

    private Texture2D appliedTexture;

    private void Start()
    {
        StartCoroutine(RefreshImageLoop());
    }

    private IEnumerator RefreshImageLoop()
    {
        while (true)
        {
            yield return LoadImageToMaterial();

            yield return new WaitForSeconds(refreshSeconds);
        }
    }

    private IEnumerator LoadImageToMaterial()
    {
        // ใส่ timestamp กัน cache
        string urlWithCacheBust = BuildCacheBustUrl(imageUrl);

        using (UnityWebRequest request = UnityWebRequestTexture.GetTexture(urlWithCacheBust, false))
        {
            yield return request.SendWebRequest();

            if (request.result != UnityWebRequest.Result.Success)
            {
                Debug.LogError("Load image failed: " + request.error);
                yield break;
            }

            Texture2D sourceTexture = DownloadHandlerTexture.GetContent(request);
            Texture2D texture = ComposeStickerTexture(sourceTexture);

            if (targetMaterial != null)
            {
                // Built-in Render Pipeline
                targetMaterial.SetTexture("_MainTex", texture);

                // URP ใช้อันนี้แทน หรือเปิดไว้คู่กันก็ได้
                targetMaterial.SetTexture("_BaseMap", texture);
            }

            Debug.Log("Image refreshed at: " + System.DateTime.Now);
        }
    }

    private string BuildCacheBustUrl(string url)
    {
        string separator = url.Contains("?") ? "&" : "?";
        return url + separator + "t=" + System.DateTimeOffset.UtcNow.ToUnixTimeSeconds();
    }

    private Texture2D ComposeStickerTexture(Texture2D sourceTexture)
    {
        if (baseTemplate == null)
        {
            ReplaceAppliedTexture(sourceTexture);
            return sourceTexture;
        }

        Texture2D composedTexture = new Texture2D(baseTemplate.width, baseTemplate.height, TextureFormat.RGBA32, false);
        composedTexture.name = "Runtime Composed Sticker";
        composedTexture.wrapMode = TextureWrapMode.Clamp;
        composedTexture.filterMode = FilterMode.Bilinear;

        Color32[] pixels;
        try
        {
            pixels = baseTemplate.GetPixels32();
        }
        catch (UnityException error)
        {
            Debug.LogError("Base template must have Read/Write enabled: " + error.Message);
            Destroy(composedTexture);
            ReplaceAppliedTexture(sourceTexture);
            return sourceTexture;
        }

        RectInt targetRect = ClampRect(targetRectPixels, baseTemplate.width, baseTemplate.height);
        Rect sourceRect = preserveAspectRatio
            ? CalculateCoverRect(sourceTexture.width, sourceTexture.height, targetRect.width, targetRect.height)
            : new Rect(0f, 0f, sourceTexture.width, sourceTexture.height);

        for (int y = 0; y < targetRect.height; y++)
        {
            float targetV = targetRect.height <= 1 ? 0f : (y + 0.5f) / targetRect.height;
            float sourceY = sourceRect.y + targetV * sourceRect.height;
            if (flipSourceY)
            {
                sourceY = sourceRect.yMax - targetV * sourceRect.height;
            }

            for (int x = 0; x < targetRect.width; x++)
            {
                float targetU = targetRect.width <= 1 ? 0f : (x + 0.5f) / targetRect.width;
                float sourceX = sourceRect.x + targetU * sourceRect.width;
                Color sourceColor = sourceTexture.GetPixelBilinear(
                    Mathf.Clamp01(sourceX / sourceTexture.width),
                    Mathf.Clamp01(sourceY / sourceTexture.height)
                );

                int destinationX = targetRect.x + x;
                int destinationY = targetRect.y + y;
                pixels[destinationY * baseTemplate.width + destinationX] = sourceColor;
            }
        }

        composedTexture.SetPixels32(pixels);
        composedTexture.Apply(false, false);

        ReplaceAppliedTexture(composedTexture);
        if (sourceTexture != composedTexture)
        {
            Destroy(sourceTexture);
        }

        return composedTexture;
    }

    private RectInt ClampRect(RectInt rect, int maxWidth, int maxHeight)
    {
        int x = Mathf.Clamp(rect.x, 0, maxWidth - 1);
        int y = Mathf.Clamp(rect.y, 0, maxHeight - 1);
        int width = Mathf.Clamp(rect.width, 1, maxWidth - x);
        int height = Mathf.Clamp(rect.height, 1, maxHeight - y);
        return new RectInt(x, y, width, height);
    }

    private Rect CalculateCoverRect(int sourceWidth, int sourceHeight, int targetWidth, int targetHeight)
    {
        float sourceAspect = sourceWidth / (float)sourceHeight;
        float targetAspect = targetWidth / (float)targetHeight;

        if (sourceAspect > targetAspect)
        {
            float cropWidth = sourceHeight * targetAspect;
            return new Rect((sourceWidth - cropWidth) * 0.5f, 0f, cropWidth, sourceHeight);
        }

        float cropHeight = sourceWidth / targetAspect;
        return new Rect(0f, (sourceHeight - cropHeight) * 0.5f, sourceWidth, cropHeight);
    }

    private void ReplaceAppliedTexture(Texture2D texture)
    {
        if (appliedTexture != null && appliedTexture != texture)
        {
            Destroy(appliedTexture);
        }

        appliedTexture = texture;
    }

    private void OnDestroy()
    {
        if (appliedTexture != null)
        {
            Destroy(appliedTexture);
        }
    }
}
