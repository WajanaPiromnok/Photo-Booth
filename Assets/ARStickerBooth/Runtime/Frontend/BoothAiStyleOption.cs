using System;
using UnityEngine;

namespace PhotoBooth.Booth.Frontend
{
    [Serializable]
    public sealed class BoothAiStyleOption
    {
        public string styleId = "natural";
        public string displayName = "Natural";
        public string aiPrompt = "Natural photo booth portrait";
        public Color primaryColor = new(0.95f, 0.95f, 0.95f, 1f);
        public Color secondaryColor = new(0.15f, 0.18f, 0.22f, 1f);
        public bool applyLocalStylizedPreview;
    }
}
