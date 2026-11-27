using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace SeasonsInstaller.Controls;

public class SimsButton : Button
{
    public static readonly StyledProperty<IImage?> NormalImageProperty =
        AvaloniaProperty.Register<SimsButton, IImage?>(nameof(NormalImage));

    public static readonly StyledProperty<IImage?> HoverImageProperty =
        AvaloniaProperty.Register<SimsButton, IImage?>(nameof(HoverImage));

    public static readonly StyledProperty<IImage?> PressedImageProperty =
        AvaloniaProperty.Register<SimsButton, IImage?>(nameof(PressedImage));

    public static readonly StyledProperty<Point> DiscCentreProperty =
        AvaloniaProperty.Register<SimsButton, Point>(nameof(DiscCentre));

    public static readonly StyledProperty<double> DiscRadiusProperty =
        AvaloniaProperty.Register<SimsButton, double>(nameof(DiscRadius));

    public static readonly StyledProperty<Thickness> LabelMarginProperty =
        AvaloniaProperty.Register<SimsButton, Thickness>(nameof(LabelMargin));

    public static readonly StyledProperty<string?> LabelProperty =
        AvaloniaProperty.Register<SimsButton, string?>(nameof(Label));

    public IImage? NormalImage { get => GetValue(NormalImageProperty); set => SetValue(NormalImageProperty, value); }
    public IImage? HoverImage { get => GetValue(HoverImageProperty); set => SetValue(HoverImageProperty, value); }
    public IImage? PressedImage { get => GetValue(PressedImageProperty); set => SetValue(PressedImageProperty, value); }
    public Point DiscCentre { get => GetValue(DiscCentreProperty); set => SetValue(DiscCentreProperty, value); }
    public double DiscRadius { get => GetValue(DiscRadiusProperty); set => SetValue(DiscRadiusProperty, value); }
    public Thickness LabelMargin { get => GetValue(LabelMarginProperty); set => SetValue(LabelMarginProperty, value); }
    public string? Label { get => GetValue(LabelProperty); set => SetValue(LabelProperty, value); }

    public const double LabelColumnWidth = 170 + 2;

    public Size ArtSize => NormalImage?.Size ?? default;

    private const double LabelGap = 6;

    private const double LabelBottomInset = 4;

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == DiscCentreProperty || change.Property == DiscRadiusProperty || change.Property == NormalImageProperty)
        {
            var h = ArtSize.Height;

            var discBottom = DiscCentre.Y + DiscRadius;
            var top = 0.0;
            var bottom = Math.Max(0, h - discBottom + LabelBottomInset);

            var right = LabelGap - (DiscCentre.X - DiscRadius) - ColumnSpacing;
            LabelMargin = new Thickness(0, top, right, bottom);
        }
    }

    private const double ColumnSpacing = 2;

    protected override Type StyleKeyOverride => typeof(SimsButton);
}
