using UnityEngine;

namespace Quest3TriggerUI
{
    internal static class ColdRasterBump
    {
        internal static void Convert(NativeCacheBuffer.RasterLease raw, int width, int height, float strength)
        {
            var rows = new float[checked(width * 3)];
            Fill(raw, rows, width, 0);
            Vector3 normal = new Vector3();
            for (int y = 0; y < height; y++)
            {
                if (y + 1 < height) Fill(raw, rows, width, y + 1);
                int current = (y % 3) * width;
                int previous = y > 0 ? ((y - 1) % 3) * width : -1;
                int next = y + 1 < height ? ((y + 1) % 3) * width : -1;
                for (int x = 0; x < width; x++)
                {
                    float bl = Sample(rows, next, x - 1, width), l = Sample(rows, current, x - 1, width);
                    float tl = Sample(rows, previous, x - 1, width), down = Sample(rows, next, x, width);
                    float up = Sample(rows, previous, x, width), br = Sample(rows, next, x + 1, width);
                    float r = Sample(rows, current, x + 1, width), tr = Sample(rows, previous, x + 1, width);
                    normal.x = (br + 2f * r + tr - bl - 2f * l - tl) * strength;
                    normal.y = (tl + 2f * up + tr - bl - 2f * down - br) * strength;
                    normal.z = 1f; normal.Normalize();
                    normal.x = normal.x * 0.5f + 0.5f; normal.y = normal.y * 0.5f + 0.5f; normal.z = normal.z * 0.5f + 0.5f;
                    int index = (y * width + x) * 4;
                    raw.Write(index, (byte)(int)(normal.x * 255f)); raw.Write(index + 1, (byte)(int)(normal.y * 255f));
                    raw.Write(index + 2, (byte)(int)(normal.z * 255f)); raw.Write(index + 3, 255);
                }
            }
        }
        static void Fill(NativeCacheBuffer.RasterLease raw, float[] rows, int width, int y)
        {
            int row = (y % 3) * width, source = y * width * 4;
            for (int x = 0; x < width; x++)
            { int i = source + x * 4; rows[row + x] = (raw.Read(i) + raw.Read(i + 1) + raw.Read(i + 2)) / 768f; }
        }
        static float Sample(float[] rows, int row, int x, int width) { return row < 0 || x < 0 || x >= width ? 0.5f : rows[row + x]; }
    }
}
