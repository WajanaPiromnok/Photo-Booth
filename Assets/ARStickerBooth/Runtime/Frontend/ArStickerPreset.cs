using System;
using PhotoBooth.Booth.AR;
using UnityEngine;

namespace PhotoBooth.Booth.Frontend
{
    [Serializable]
    public sealed class ArStickerPreset
    {
        public string presetId = "default";
        public string displayName = "Default";
        public Sprite previewSprite;
        public ArStickerDefinition[] stickers = Array.Empty<ArStickerDefinition>();
    }
}
