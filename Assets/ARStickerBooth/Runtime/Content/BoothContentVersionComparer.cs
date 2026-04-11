using System;

namespace PhotoBooth.Booth.Content
{
    public static class BoothContentVersionComparer
    {
        public static int Compare(string leftVersion, string rightVersion)
        {
            var left = Normalize(leftVersion);
            var right = Normalize(rightVersion);
            var max = Math.Max(left.Length, right.Length);

            for (var index = 0; index < max; index++)
            {
                var leftValue = index < left.Length ? left[index] : 0;
                var rightValue = index < right.Length ? right[index] : 0;

                if (leftValue != rightValue)
                {
                    return leftValue.CompareTo(rightValue);
                }
            }

            return 0;
        }

        private static int[] Normalize(string version)
        {
            if (string.IsNullOrWhiteSpace(version))
            {
                return Array.Empty<int>();
            }

            var clean = version.Trim();
            if (clean.StartsWith("v", StringComparison.OrdinalIgnoreCase))
            {
                clean = clean[1..];
            }

            var parts = clean.Split('.');
            var values = new int[parts.Length];

            for (var index = 0; index < parts.Length; index++)
            {
                var rawPart = parts[index];
                var digits = string.Empty;

                foreach (var character in rawPart)
                {
                    if (char.IsDigit(character))
                    {
                        digits += character;
                    }
                    else
                    {
                        break;
                    }
                }

                values[index] = int.TryParse(digits, out var number) ? number : 0;
            }

            return values;
        }
    }
}
