using System.Windows;
using System.Windows.Controls;

namespace DS5BtHaptics.App.Ui.Views;

/// <summary>One line of status in the flyout: controller, virtual device, haptics.</summary>
public partial class StatusCard : UserControl
{
    public static readonly DependencyProperty GlyphProperty = DependencyProperty.Register(nameof(Glyph), typeof(string), typeof(StatusCard));
    public static readonly DependencyProperty TitleProperty = DependencyProperty.Register(nameof(Title), typeof(string), typeof(StatusCard));
    public static readonly DependencyProperty DetailProperty = DependencyProperty.Register(nameof(Detail), typeof(string), typeof(StatusCard));
    public static readonly DependencyProperty AccessoryProperty = DependencyProperty.Register(nameof(Accessory), typeof(object), typeof(StatusCard));
    public static readonly DependencyProperty IsOkProperty = DependencyProperty.Register(
        nameof(IsOk), typeof(bool), typeof(StatusCard), new PropertyMetadata(false, (d, _) => ((StatusCard)d).UpdateTile()));

    public StatusCard()
    {
        InitializeComponent();
        UpdateTile();
    }

    public string? Glyph { get => (string?)GetValue(GlyphProperty); set => SetValue(GlyphProperty, value); }
    public string? Title { get => (string?)GetValue(TitleProperty); set => SetValue(TitleProperty, value); }
    public string? Detail { get => (string?)GetValue(DetailProperty); set => SetValue(DetailProperty, value); }
    public object? Accessory { get => GetValue(AccessoryProperty); set => SetValue(AccessoryProperty, value); }
    public bool IsOk { get => (bool)GetValue(IsOkProperty); set => SetValue(IsOkProperty, value); }

    private void UpdateTile()
    {
        // SetResourceReference keeps following light/dark switches.
        GlyphTile.SetResourceReference(Border.BackgroundProperty, IsOk ? "AccentSoftBrush" : "ControlBrush");
        GlyphText.SetResourceReference(TextBlock.ForegroundProperty, IsOk ? "AccentBrush" : "TextDisabledBrush");
    }
}
