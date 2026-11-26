using Dezolati.Services;

namespace Dezolati.Controls;

/// <summary>
/// Vlastní obrázky výzdoby akce: base64 z <see cref="AkceService"/> → IImage pro kreslení (<see cref="SceneDrawable.Images"/>),
/// a výběr obrázku z telefonu zmenšený na 320 px (JPEG) k nahrání.
/// </summary>
public static class AkceImages
{
    private static bool _hooked;

    /// <summary>Napojit jednou (volá každá stránka akce, nic se nestane dvakrát).</summary>
    public static void Init()
    {
        if (_hooked) return;
        _hooked = true;
        AkceService.MediaLoaded += Sync;
    }

    public static event Action<string>? Changed;

    private static void Sync(string key)
    {
        SceneDrawable.Images.Remove(key);
        if (AkceService.MediaOf(key) is { } m)
            try
            {
                if (Decode(Convert.FromBase64String(m.Data)) is { } img) SceneDrawable.Images[key] = img;
            }
            catch { /* poškozený obrázek → kreslí se motiv */ }
        MainThread.BeginInvokeOnMainThread(() => Changed?.Invoke(key));
    }

    private static Microsoft.Maui.Graphics.IImage? Decode(byte[] bytes)
    {
#if ANDROID || IOS || MACCATALYST
        using var ms = new MemoryStream(bytes);
        return Microsoft.Maui.Graphics.Platform.PlatformImage.FromStream(ms);
#else
        return null;
#endif
    }

    /// <summary>Vybrat obrázek a zmenšit; null = nic nevybral. Vrací base64 JPEG.</summary>
    public static async Task<string?> PickAsync()
    {
        var file = await FilePicker.Default.PickAsync(new PickOptions { PickerTitle = "Vyber obrázek", FileTypes = FilePickerFileType.Images });
        if (file == null) return null;
#if ANDROID || IOS || MACCATALYST
        using var src = await file.OpenReadAsync();
        var img = Microsoft.Maui.Graphics.Platform.PlatformImage.FromStream(src);
#if ANDROID
        // fotky z mobilu mívají otočení jen v EXIFu – Bitmap ho neaplikuje
        if (OperatingSystem.IsAndroidVersionAtLeast(24))
            try
            {
                using var exifStream = await file.OpenReadAsync();
                var exif = new Android.Media.ExifInterface(exifStream);
                var deg = exif.GetAttributeInt(Android.Media.ExifInterface.TagOrientation, 1) switch { 6 => 90, 3 => 180, 8 => 270, _ => 0 };
                if (deg != 0 && img is Microsoft.Maui.Graphics.Platform.PlatformImage pi && pi.PlatformRepresentation is Android.Graphics.Bitmap bmp)
                {
                    using var matrix = new Android.Graphics.Matrix();
                    matrix.PostRotate(deg);
                    var rotated = Android.Graphics.Bitmap.CreateBitmap(bmp, 0, 0, bmp.Width, bmp.Height, matrix, true)!;
                    img = new Microsoft.Maui.Graphics.Platform.PlatformImage(rotated);
                }
            }
            catch { /* bez otočení */ }
#endif
        var small = img.Width > 320 || img.Height > 320 ? img.Downsize(320, true) : img;
        using var ms = new MemoryStream();
        await small.SaveAsync(ms, ImageFormat.Jpeg, 0.85f);
        return Convert.ToBase64String(ms.ToArray());
#else
        using var src = await file.OpenReadAsync();
        using var ms = new MemoryStream();
        await src.CopyToAsync(ms);
        return Convert.ToBase64String(ms.ToArray());
#endif
    }
}
