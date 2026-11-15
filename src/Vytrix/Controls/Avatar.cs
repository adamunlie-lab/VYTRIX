using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;

namespace Vytrix.Controls;

/// <summary>
/// A round account picture: the sign-in provider's photo when there is one, otherwise (or while
/// it loads, or if it can't be fetched, e.g. offline) the person's initials on the accent colour.
/// </summary>
public sealed class Avatar : Grid
{
    private readonly Border _initials;
    private readonly Ellipse _photo;

    public Avatar(string displayName, string pictureUrl, double size = 36)
    {
        Width = Height = size;
        IsHitTestVisible = false;
        _initials = new Border
        {
            CornerRadius = new CornerRadius(size / 2),
            Background = Ui.Res("AccentBrush"),
            Child = new TextBlock
            {
                Text = Initials(displayName), Foreground = Brushes.White, FontWeight = FontWeights.SemiBold,
                FontSize = Math.Round(size * 0.4), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
            },
        };
        Children.Add(_initials);
        _photo = new Ellipse { Visibility = Visibility.Collapsed };
        Children.Add(_photo);
        if (pictureUrl.Length > 0) LoadPhoto(pictureUrl, size);
    }

    private void LoadPhoto(string url, double size)
    {
        try
        {
            var img = new BitmapImage();
            img.BeginInit();
            img.UriSource = new Uri(url);
            img.DecodePixelWidth = (int)Math.Ceiling(size * 2);   // sharp on high-DPI screens, small in memory
            img.CacheOption = BitmapCacheOption.OnLoad;
            img.EndInit();
            void Show() { _photo.Fill = new ImageBrush(img) { Stretch = Stretch.UniformToFill }; _photo.Visibility = Visibility.Visible; _initials.Visibility = Visibility.Collapsed; }
            if (img.IsDownloading) img.DownloadCompleted += (_, _) => Show();
            else Show();
            img.DownloadFailed += (_, _) => { _photo.Visibility = Visibility.Collapsed; _initials.Visibility = Visibility.Visible; };
        }
        catch { /* keep the initials */ }
    }

    /// <summary>"Adam Smith" → "AS", "gamer99" → "G".</summary>
    public static string Initials(string name)
    {
        var parts = name.Split([' ', '.', '_', '-'], StringSplitOptions.RemoveEmptyEntries).Where(p => char.IsLetterOrDigit(p[0])).ToArray();
        if (parts.Length == 0) return "?";
        var first = char.ToUpperInvariant(parts[0][0]).ToString();
        return parts.Length > 1 && name.Contains(' ') ? first + char.ToUpperInvariant(parts[^1][0]) : first;
    }
}
