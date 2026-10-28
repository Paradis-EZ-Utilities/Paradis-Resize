namespace ParadisResize.Core.Planning;

/// <summary>Works out the new pixel size for one image from the Resize settings.</summary>
public static class SizePlanner
{
    public readonly record struct Plan(int Width, int Height)
    {
        public bool IsSameAs(int width, int height) => Width == width && Height == height;
    }

    /// <param name="width">The picture's shown width (after EXIF orientation).</param>
    /// <param name="height">The picture's shown height.</param>
    public static Plan For(int width, int height, ResizeSettings s)
    {
        if (width < 1 || height < 1) throw new ArgumentOutOfRangeException(nameof(width));

        if (s.By == ResizeBy.Percentage)
        {
            double scale = s.Percent / 100.0;
            if (s.DontEnlarge && scale > 1) scale = 1;
            return Scaled(width, height, scale, scale);
        }

        int? w = s.Width, h = s.Height;
        if (w == null && h == null) return new Plan(width, height);

        if (s.KeepProportions)
        {
            // One side given: the other follows. Both given: fit inside that box.
            double scale = (w, h) switch
            {
                (int ww, null) => ww / (double)width,
                (null, int hh) => hh / (double)height,
                (int ww, int hh) => Math.Min(ww / (double)width, hh / (double)height),
                _ => 1,
            };
            if (s.DontEnlarge && scale > 1) scale = 1;
            var p = Scaled(width, height, scale, scale);
            // Make the side the user typed exact (rounding the scale can be off by one).
            if (scale < 1 || !s.DontEnlarge)
            {
                if (w != null && h == null) p = p with { Width = w.Value };
                else if (h != null && w == null) p = p with { Height = h.Value };
            }
            return p;
        }

        // Proportions off: each side is set independently; an Auto side stays as it is.
        int nw = w ?? width, nh = h ?? height;
        if (s.DontEnlarge)
        {
            nw = Math.Min(nw, width);
            nh = Math.Min(nh, height);
        }
        return new Plan(Math.Max(1, nw), Math.Max(1, nh));
    }

    private static Plan Scaled(int width, int height, double sx, double sy) =>
        new(Math.Max(1, (int)Math.Round(width * sx, MidpointRounding.AwayFromZero)),
            Math.Max(1, (int)Math.Round(height * sy, MidpointRounding.AwayFromZero)));
}
