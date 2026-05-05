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
    }
}
