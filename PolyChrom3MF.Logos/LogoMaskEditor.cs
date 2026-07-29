namespace PolyChrom3MF.Logos;

public sealed class LogoMaskEditor
{
    readonly LogoRaster _original;
    readonly byte[] _current;

    public LogoMaskEditor(LogoRaster source)
    {
        LogoImageImporter.ValidateRaster(source);
        _original = source with { Rgba = (byte[])source.Rgba.Clone() };
        _current = (byte[])source.Rgba.Clone();
    }

    public LogoRaster Snapshot() => _original with { Rgba = (byte[])_current.Clone() };

    public void Reset() => Buffer.BlockCopy(_original.Rgba, 0, _current, 0, _current.Length);

    public int RemoveBorderBackground(byte tolerance, CancellationToken cancellationToken = default)
    {
        var width = _original.Width;
        var height = _original.Height;
        var visited = new bool[checked(width * height)];
        var queue = new Queue<int>();
        void Add(int x, int y)
        {
            var index = y * width + x;
            if (visited[index]) return;
            visited[index] = true;
            queue.Enqueue(index);
        }
        for (var x = 0; x < width; x++) { Add(x, 0); Add(x, height - 1); }
        for (var y = 1; y < height - 1; y++) { Add(0, y); Add(width - 1, y); }

        var reference = BorderMedianColor();
        var removed = 0;
        var threshold = tolerance * tolerance * 3;
        while (queue.Count > 0)
        {
            if ((removed & 16383) == 0) cancellationToken.ThrowIfCancellationRequested();
            var pixel = queue.Dequeue();
            var offset = pixel * 4;
            var dr = _original.Rgba[offset] - reference.R;
            var dg = _original.Rgba[offset + 1] - reference.G;
            var db = _original.Rgba[offset + 2] - reference.B;
            if (dr * dr + dg * dg + db * db > threshold) continue;
            if (_current[offset + 3] != 0) { _current[offset + 3] = 0; removed++; }
            var x = pixel % width;
            var y = pixel / width;
            if (x > 0) Add(x - 1, y);
            if (x + 1 < width) Add(x + 1, y);
            if (y > 0) Add(x, y - 1);
            if (y + 1 < height) Add(x, y + 1);
        }
        return removed;
    }

    public int RemoveDominantColor(byte tolerance, CancellationToken cancellationToken = default)
    {
        var histogram = new Dictionary<int, int>();
        for (var offset = 0; offset < _original.Rgba.Length; offset += 4)
        {
            if (_original.Rgba[offset + 3] == 0) continue;
            var key = ((_original.Rgba[offset] >> 3) << 10) |
                      ((_original.Rgba[offset + 1] >> 3) << 5) |
                      (_original.Rgba[offset + 2] >> 3);
            histogram[key] = histogram.GetValueOrDefault(key) + 1;
        }
        if (histogram.Count == 0) return 0;
        var dominant = histogram.MaxBy(pair => pair.Value).Key;
        var r = (byte)(((dominant >> 10) & 31) * 255 / 31);
        var g = (byte)(((dominant >> 5) & 31) * 255 / 31);
        var b = (byte)((dominant & 31) * 255 / 31);
        var threshold = tolerance * tolerance * 3;
        var removed = 0;
        for (var offset = 0; offset < _original.Rgba.Length; offset += 4)
        {
            if ((offset & 0x3ffff) == 0) cancellationToken.ThrowIfCancellationRequested();
            var dr = _original.Rgba[offset] - r;
            var dg = _original.Rgba[offset + 1] - g;
            var db = _original.Rgba[offset + 2] - b;
            if (dr * dr + dg * dg + db * db <= threshold && _current[offset + 3] != 0)
            {
                _current[offset + 3] = 0;
                removed++;
            }
        }
        return removed;
    }

    public int ApplyBrush(float centerX, float centerY, float radius, LogoBrushMode mode)
    {
        if (!float.IsFinite(centerX) || !float.IsFinite(centerY) || !float.IsFinite(radius) || radius <= 0)
            throw new ArgumentOutOfRangeException(nameof(radius));
        var minX = Math.Max(0, (int)Math.Floor(centerX - radius));
        var maxX = Math.Min(_original.Width - 1, (int)Math.Ceiling(centerX + radius));
        var minY = Math.Max(0, (int)Math.Floor(centerY - radius));
        var maxY = Math.Min(_original.Height - 1, (int)Math.Ceiling(centerY + radius));
        var radiusSquared = radius * radius;
        var changed = 0;
        for (var y = minY; y <= maxY; y++)
        for (var x = minX; x <= maxX; x++)
        {
            var dx = x - centerX;
            var dy = y - centerY;
            if (dx * dx + dy * dy > radiusSquared) continue;
            var alpha = (y * _original.Width + x) * 4 + 3;
            var value = mode == LogoBrushMode.Erase ? (byte)0 : _original.Rgba[alpha];
            if (_current[alpha] == value) continue;
            _current[alpha] = value;
            changed++;
        }
        return changed;
    }

    (byte R, byte G, byte B) BorderMedianColor()
    {
        var samples = new List<(byte R, byte G, byte B)>();
        var stepX = Math.Max(1, _original.Width / 128);
        var stepY = Math.Max(1, _original.Height / 128);
        for (var x = 0; x < _original.Width; x += stepX)
        {
            samples.Add(ColorAt(x, 0));
            samples.Add(ColorAt(x, _original.Height - 1));
        }
        for (var y = 0; y < _original.Height; y += stepY)
        {
            samples.Add(ColorAt(0, y));
            samples.Add(ColorAt(_original.Width - 1, y));
        }
        byte Median(Func<(byte R, byte G, byte B), byte> selector)
        {
            var values = samples.Select(selector).Order().ToArray();
            return values[values.Length / 2];
        }
        return (Median(value => value.R), Median(value => value.G), Median(value => value.B));
    }

    (byte R, byte G, byte B) ColorAt(int x, int y)
    {
        var offset = (y * _original.Width + x) * 4;
        return (_original.Rgba[offset], _original.Rgba[offset + 1], _original.Rgba[offset + 2]);
    }
}
