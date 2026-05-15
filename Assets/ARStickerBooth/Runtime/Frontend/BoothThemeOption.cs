using System;
using UnityEngine;

namespace PhotoBooth.Booth.Frontend
{
    [Serializable]
    public sealed class BoothThemeOption
    {
        public string themeId = "classic";
        public string displayName = "Classic";
        public long priceMinorUnits = 12000;
        public string currencyCode = "THB";
        public Sprite previewSprite;
        public Sprite frameTemplateSprite;
        public Texture2D frameTemplateTexture;
        public string frameTemplateResourcePath = "MrkremeUi/piece_03";
        public string backendFrameId;

        public string BackendFrameId => string.IsNullOrWhiteSpace(backendFrameId) ? themeId : backendFrameId;
        public string FrameTemplateResourcePath => string.IsNullOrWhiteSpace(frameTemplateResourcePath) ? "MrkremeUi/piece_03" : frameTemplateResourcePath;
    }
}
