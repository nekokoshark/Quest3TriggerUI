using System;
using System.Collections.Generic;
namespace Quest3TriggerUI
{
    internal static class Bc7MipReadbackPlan
    {
        // Explicit snapshot only: <=1MiB RGBA per request, at most four textures.
        internal static int[] Levels(int width, int height, int mips)
        {
            var levels = new List<int>();
            if (width < 1 || height < 1 || mips < 1 || mips > 32) return levels.ToArray();
            for (int mip = 0; mip < mips; mip++)
            {
                if ((long)width * height <= 262144) levels.Add(mip);
                if (width == 1 && height == 1) break;
                width = Math.Max(1, width / 2); height = Math.Max(1, height / 2);
            }
            return levels.ToArray();
        }
    }
}
