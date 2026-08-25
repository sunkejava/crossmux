using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using SkiaSharp;

sealed class SmartStandbyRenderer : IDisposable {
  readonly ILogger<SmartStandbyRenderer> logger;
  readonly SKTypeface typeface;
  static readonly SKColor Paper = new(245, 244, 238);
  static readonly SKColor Ink = new(22, 24, 27);
  static readonly SKColor Muted = new(105, 108, 112);
  static readonly SKColor Line = new(195, 195, 188);

  public SmartStandbyRenderer(IOptions<SmartStandbyOptions> configured, ILogger<SmartStandbyRenderer> logger) {
    this.logger = logger;
    typeface = ResolveTypeface(configured.Value, logger);
  }

  public void Dispose() => typeface.Dispose();

  public async Task<Dictionary<string, string>> ResolveValuesAsync(
    SmartStandbyTemplate template, HttpClient http, CancellationToken ct) {
    var values = new Dictionary<string, string>(template.Values, StringComparer.OrdinalIgnoreCase) {
      ["now"] = DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture),
      ["date"] = DateTimeOffset.Now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
      ["time"] = DateTimeOffset.Now.ToString("HH:mm", CultureInfo.InvariantCulture)
    };
    foreach (var source in template.DataSources.Take(8)) {
      try {
        using var request = new HttpRequestMessage(HttpMethod.Get, source.Url);
        if (source.Headers is not null)
          foreach (var header in source.Headers.Take(12)) request.Headers.TryAddWithoutValidation(header.Key, header.Value);
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var json = await JsonDocument.ParseAsync(stream, new JsonDocumentOptions { MaxDepth = 32 }, ct);
        foreach (var binding in source.Bindings.Take(32)) {
          var value = ReadPath(json.RootElement, binding.Value);
          if (value is not null) values[binding.Key] = value;
        }
      } catch (Exception ex) when (ex is not OperationCanceledException) {
        logger.LogWarning(ex, "Smart standby data source {SourceId} failed", source.Id);
      }
    }
    return values;
  }

  public async Task<Dictionary<int, byte[]>> ResolveImagesAsync(SmartStandbyTemplate template,
    IReadOnlyDictionary<string, string> values, HttpClient http, CancellationToken ct) {
    var images = new Dictionary<int, byte[]>();
    for (var index = 0; index < template.Widgets.Count && index < 64; ++index) {
      var widget = template.Widgets[index];
      if (!widget.Type.Equals("image", StringComparison.OrdinalIgnoreCase)) continue;
      var url = Expand(widget.Value ?? "", values);
      if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https")) continue;
      try {
        using var response = await http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength is > 4_194_304) throw new InvalidDataException("Image exceeds 4 MiB");
        await using var input = await response.Content.ReadAsStreamAsync(ct);
        using var output = new MemoryStream();
        var buffer = new byte[32 * 1024];
        int read;
        while ((read = await input.ReadAsync(buffer, ct)) > 0) {
          if (output.Length + read > 4_194_304) throw new InvalidDataException("Image exceeds 4 MiB");
          await output.WriteAsync(buffer.AsMemory(0, read), ct);
        }
        images[index] = output.ToArray();
      } catch (Exception ex) when (ex is not OperationCanceledException) {
        logger.LogWarning(ex, "Smart standby image widget {WidgetIndex} failed", index);
      }
    }
    return images;
  }

  public byte[] Render(SmartStandbyTemplate template, IReadOnlyDictionary<string, string> values,
    IReadOnlyDictionary<int, byte[]> images) {
    var width = Math.Clamp(template.Width, 320, 1200);
    var height = Math.Clamp(template.Height, 240, 1200);
    using var bitmap = new SKBitmap(width, height, SKColorType.Bgra8888, SKAlphaType.Opaque);
    using var canvas = new SKCanvas(bitmap);
    canvas.Clear(ParseColor(template.Background, Paper));
    for (var index = 0; index < template.Widgets.Count && index < 64; ++index)
      DrawWidget(canvas, template.Widgets[index], values, images.GetValueOrDefault(index), width, height);
    return EncodeBmp(bitmap);
  }

  void DrawWidget(SKCanvas canvas, SmartWidget widget, IReadOnlyDictionary<string, string> values,
    byte[]? imageBytes,
    int screenWidth, int screenHeight) {
    var rect = new SKRect(
      Math.Clamp(widget.X, 0, screenWidth - 1), Math.Clamp(widget.Y, 0, screenHeight - 1),
      Math.Clamp(widget.X + widget.Width, 1, screenWidth), Math.Clamp(widget.Y + widget.Height, 1, screenHeight));
    var foreground = widget.Inverted ? Paper : Ink;
    if (widget.Inverted) {
      using var fill = new SKPaint { Color = Ink, IsAntialias = true };
      canvas.DrawRoundRect(rect, 14, 14, fill);
    }
    if (widget.Border) {
      using var border = new SKPaint { Color = widget.Inverted ? Muted : Line, Style = SKPaintStyle.Stroke, StrokeWidth = 2 };
      canvas.DrawRoundRect(rect, 14, 14, border);
    }
    var title = Expand(widget.Title ?? "", values);
    var value = Expand(widget.Value ?? "", values);
    switch (widget.Type.ToLowerInvariant()) {
      case "image":
        DrawImage(canvas, rect, imageBytes, widget.Border);
        return;
      case "divider":
        using (var line = new SKPaint { Color = widget.Inverted ? Paper : Line, StrokeWidth = Math.Max(1, widget.Height) })
          canvas.DrawLine(rect.Left, rect.MidY, rect.Right, rect.MidY, line);
        return;
      case "progress":
        DrawProgress(canvas, rect, title, value, foreground);
        return;
      default:
        DrawTextBlock(canvas, rect, title, value, widget.FontSize, widget.Align, foreground);
        return;
    }
  }

  static void DrawImage(SKCanvas canvas, SKRect rect, byte[]? bytes, bool border) {
    if (bytes is null) return;
    using var bitmap = SKBitmap.Decode(bytes);
    if (bitmap is null || bitmap.Width <= 0 || bitmap.Height <= 0) return;
    var scale = Math.Min(rect.Width / bitmap.Width, rect.Height / bitmap.Height);
    var width = bitmap.Width * scale;
    var height = bitmap.Height * scale;
    var target = new SKRect(rect.MidX - width / 2, rect.MidY - height / 2, rect.MidX + width / 2, rect.MidY + height / 2);
    using var paint = new SKPaint { IsAntialias = true, FilterQuality = SKFilterQuality.Medium };
    canvas.DrawBitmap(bitmap, target, paint);
    if (border) {
      using var outline = new SKPaint { Color = Line, Style = SKPaintStyle.Stroke, StrokeWidth = 2 };
      canvas.DrawRoundRect(rect, 14, 14, outline);
    }
  }

  void DrawTextBlock(SKCanvas canvas, SKRect rect, string title, string value, float size, string align, SKColor color) {
    using var titlePaint = new SKPaint { Color = color == Ink ? Muted : Paper, IsAntialias = true, TextSize = Math.Max(12, size * .42f), Typeface = typeface };
    using var valuePaint = new SKPaint { Color = color, IsAntialias = true, TextSize = Math.Clamp(size, 12, 120), Typeface = typeface };
    var x = align.Equals("center", StringComparison.OrdinalIgnoreCase) ? rect.MidX : rect.Left + 14;
    titlePaint.TextAlign = valuePaint.TextAlign = align.Equals("center", StringComparison.OrdinalIgnoreCase) ? SKTextAlign.Center : SKTextAlign.Left;
    var y = rect.Top + 20 + titlePaint.TextSize;
    if (!string.IsNullOrWhiteSpace(title)) canvas.DrawText(title, x, y, titlePaint);
    y += string.IsNullOrWhiteSpace(title) ? valuePaint.TextSize : valuePaint.TextSize + 10;
    foreach (var line in Wrap(value, valuePaint, Math.Max(20, rect.Width - 28)).Take(5)) {
      if (y > rect.Bottom - 4) break;
      canvas.DrawText(line, x, y, valuePaint);
      y += valuePaint.TextSize * 1.18f;
    }
  }

  void DrawProgress(SKCanvas canvas, SKRect rect, string title, string value, SKColor color) {
    var parsed = double.TryParse(value.TrimEnd('%'), NumberStyles.Float, CultureInfo.InvariantCulture, out var n) ? Math.Clamp(n, 0, 100) : 0;
    using var text = new SKPaint { Color = color, IsAntialias = true, TextSize = 18, Typeface = typeface };
    canvas.DrawText($"{title}  {parsed:0}%", rect.Left + 10, rect.Top + 24, text);
    var bar = new SKRect(rect.Left + 10, rect.Bottom - 20, rect.Right - 10, rect.Bottom - 8);
    using var track = new SKPaint { Color = Line };
    using var fill = new SKPaint { Color = color };
    canvas.DrawRoundRect(bar, 6, 6, track);
    canvas.DrawRoundRect(new SKRect(bar.Left, bar.Top, bar.Left + bar.Width * (float)(parsed / 100), bar.Bottom), 6, 6, fill);
  }

  static IEnumerable<string> Wrap(string text, SKPaint paint, float maxWidth) {
    if (string.IsNullOrEmpty(text)) yield break;
    var current = new StringBuilder();
    foreach (var rune in text.EnumerateRunes()) {
      current.Append(rune.ToString());
      if (paint.MeasureText(current.ToString()) <= maxWidth) continue;
      var overflow = rune.ToString();
      current.Length -= overflow.Length;
      if (current.Length > 0) yield return current.ToString();
      current.Clear();
      current.Append(overflow);
    }
    if (current.Length > 0) yield return current.ToString();
  }

  static string Expand(string input, IReadOnlyDictionary<string, string> values) {
    foreach (var item in values) input = input.Replace("{{" + item.Key + "}}", item.Value, StringComparison.OrdinalIgnoreCase);
    return input;
  }

  static string? ReadPath(JsonElement element, string path) {
    foreach (var segment in path.Trim().TrimStart('$', '.').Split('.', StringSplitOptions.RemoveEmptyEntries)) {
      if (element.ValueKind == JsonValueKind.Object && element.TryGetProperty(segment, out var child)) element = child;
      else if (element.ValueKind == JsonValueKind.Array && int.TryParse(segment, out var index) && index >= 0 && index < element.GetArrayLength()) element = element[index];
      else return null;
    }
    return element.ValueKind == JsonValueKind.String ? element.GetString() : element.ToString();
  }

  static SKColor ParseColor(string value, SKColor fallback) => SKColor.TryParse(value, out var color) ? color : fallback;

  static SKTypeface ResolveTypeface(SmartStandbyOptions options, ILogger logger) {
    if (!string.IsNullOrWhiteSpace(options.FontPath)) {
      var path = Path.GetFullPath(options.FontPath);
      if (File.Exists(path)) {
        var fromFile = SKTypeface.FromFile(path);
        if (fromFile is not null) {
          logger.LogInformation("Smart standby font loaded from {FontPath}", path);
          return fromFile;
        }
      }
      logger.LogWarning("Smart standby FontPath is unavailable: {FontPath}", path);
    }

    var families = new List<string>();
    if (!string.IsNullOrWhiteSpace(options.FontFamily)) families.Add(options.FontFamily);
    if (OperatingSystem.IsWindows())
      families.AddRange(["Microsoft YaHei UI", "Microsoft YaHei", "SimHei", "SimSun"]);
    else if (OperatingSystem.IsLinux())
      families.AddRange(["Noto Sans CJK SC", "Noto Sans SC", "WenQuanYi Micro Hei", "Source Han Sans SC"]);
    else
      families.AddRange(["PingFang SC", "Hiragino Sans GB"]);

    foreach (var family in families) {
      var candidate = SKTypeface.FromFamilyName(family);
      if (candidate is null) continue;
      if (candidate.FamilyName.Contains(family, StringComparison.OrdinalIgnoreCase) ||
          family.Contains(candidate.FamilyName, StringComparison.OrdinalIgnoreCase)) {
        logger.LogInformation("Smart standby font selected: {FontFamily}", candidate.FamilyName);
        return candidate;
      }
      candidate.Dispose();
    }
    logger.LogWarning("No CJK font found; set SmartStandby__FontPath to a Chinese TTF/TTC/OTF file");
    return SKTypeface.Default;
  }

  static byte[] EncodeBmp(SKBitmap bitmap) {
    // The reader consumes four native gray levels. A 2-bpp paletted BMP is
    // ~96 KiB at 800x480, versus ~1.15 MiB for the previous 24-bpp output.
    var rowBytes = (bitmap.Width * 2 + 31) / 32 * 4;
    var imageBytes = rowBytes * bitmap.Height;
    const int paletteBytes = 4 * 4;
    const int pixelOffset = 54 + paletteBytes;
    using var output = new MemoryStream(pixelOffset + imageBytes);
    using var writer = new BinaryWriter(output, Encoding.ASCII, true);
    writer.Write((byte)'B'); writer.Write((byte)'M'); writer.Write(pixelOffset + imageBytes); writer.Write(0); writer.Write(pixelOffset);
    writer.Write(40); writer.Write(bitmap.Width); writer.Write(bitmap.Height); writer.Write((short)1); writer.Write((short)2);
    writer.Write(0); writer.Write(imageBytes); writer.Write(2835); writer.Write(2835); writer.Write(4); writer.Write(4);
    foreach (var gray in new byte[] { 0, 85, 170, 255 }) {
      writer.Write(gray); writer.Write(gray); writer.Write(gray); writer.Write((byte)0);
    }
    for (var y = bitmap.Height - 1; y >= 0; --y) {
      var packed = 0;
      var bits = 6;
      var written = 0;
      for (var x = 0; x < bitmap.Width; ++x) {
        var color = bitmap.GetPixel(x, y);
        var gray = (byte)Math.Clamp((color.Red * 299 + color.Green * 587 + color.Blue * 114) / 1000, 0, 255);
        var level = gray < 64 ? 0 : gray < 128 ? 1 : gray < 192 ? 2 : 3;
        packed |= level << bits;
        if (bits == 0) {
          writer.Write((byte)packed);
          packed = 0;
          bits = 6;
          ++written;
        } else {
          bits -= 2;
        }
      }
      if (bits != 6) {
        writer.Write((byte)packed);
        ++written;
      }
      for (; written < rowBytes; ++written) writer.Write((byte)0);
    }
    return output.ToArray();
  }
}
