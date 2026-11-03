using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using LiveCaptionsTranslator.apis;
using LiveCaptionsTranslator.Utils;
using ColorEnum = LiveCaptionsTranslator.Utils.Color;

namespace LiveCaptionsTranslator;

/// <summary>Resizable, translucent game-chat overlay with a shared transcript and explicit click-through recovery.</summary>
public partial class OverlayWindow : Window
{
    private readonly Dictionary<ColorEnum, SolidColorBrush> colors = new()
    {
        [ColorEnum.White] = Brushes.White,
        [ColorEnum.Yellow] = Brushes.Yellow,
        [ColorEnum.LimeGreen] = Brushes.LimeGreen,
        [ColorEnum.Aqua] = Brushes.Aqua,
        [ColorEnum.Blue] = Brushes.Blue,
        [ColorEnum.DeepPink] = Brushes.DeepPink,
        [ColorEnum.Red] = Brushes.Red,
        [ColorEnum.Black] = Brushes.Black
    };
    private CaptionVisible onlyMode = CaptionVisible.Both;
    private bool locked;
    private models.OverlayWindowState Settings => Translator.Setting!.OverlayWindow;

    /// <summary>Chooses whether each chat row shows source, translation, or both without changing window geometry.</summary>
    public CaptionVisible OnlyMode
    {
        get => onlyMode;
        set { onlyMode = value; ApplyVisibility(); }
    }

    /// <summary>Initializes appearance from the existing overlay preferences.</summary>
    public OverlayWindow()
    {
        InitializeComponent();
        DataContext = Translator.Chat;
        ChatView.DataContext = Translator.Chat;
        ApplyFontSize();
        ApplyFontWeight();
        ChatView.TranslationBrush = colors[Settings.FontColor];
        ApplyBackgroundOpacity();
    }

    private void Border_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (!locked && e.LeftButton == MouseButtonState.Pressed) DragMove();
    }

    private void Window_MouseEnter(object sender, MouseEventArgs e) => ControlPanel.Opacity = 1;
    private void Window_MouseLeave(object sender, MouseEventArgs e) => ControlPanel.Opacity = 0.65;
    private void FontIncrease_Click(object sender, RoutedEventArgs e) => ChangeFontSize(StyleConsts.DELTA_FONT_SIZE);
    private void FontDecrease_Click(object sender, RoutedEventArgs e) => ChangeFontSize(-StyleConsts.DELTA_FONT_SIZE);
    private void ChangeFontSize(int delta)
    {
        Settings.FontSize = Math.Clamp(Settings.FontSize + delta, StyleConsts.MIN_FONT_SIZE, StyleConsts.MAX_FONT_SIZE);
        ApplyFontSize();
    }

    /// <summary>Applies the configured size to source and translation across all chat rows.</summary>
    public void ApplyFontSize()
    {
        ChatView.OriginalFontSize = Math.Max(10, Settings.FontSize - 2);
        ChatView.TranslationFontSize = Settings.FontSize * 1.25;
    }

    private void FontBold_Click(object sender, RoutedEventArgs e)
    {
        Settings.FontBold = Settings.FontBold == Utils.FontBold.None ? Utils.FontBold.TranslationOnly : Utils.FontBold.None;
        ApplyFontWeight();
    }
    private void ApplyFontWeight() => ChatView.TranslationWeight = Settings.FontBold is Utils.FontBold.TranslationOnly or Utils.FontBold.Both
        ? FontWeights.Bold : FontWeights.SemiBold;

    private void FontColorCycle_Click(object sender, RoutedEventArgs e)
    {
        Settings.FontColor = Settings.FontColor == ColorEnum.Black ? ColorEnum.White : Settings.FontColor + 1;
        ChatView.TranslationBrush = colors[Settings.FontColor];
    }
    private void BackgroundColorCycle_Click(object sender, RoutedEventArgs e)
    {
        Settings.BackgroundColor = Settings.BackgroundColor == ColorEnum.Black ? ColorEnum.White : Settings.BackgroundColor + 1;
        ApplyBackgroundOpacity();
    }
    private void BackgroundOpacityIncrease_Click(object sender, RoutedEventArgs e) => ChangeOpacity(StyleConsts.DELTA_OPACITY);
    private void BackgroundOpacityDecrease_Click(object sender, RoutedEventArgs e) => ChangeOpacity(-StyleConsts.DELTA_OPACITY);
    private void ChangeOpacity(int delta)
    {
        Settings.Opacity = Math.Clamp(Settings.Opacity + delta, StyleConsts.MIN_OPACITY, StyleConsts.MAX_OPACITY);
        ApplyBackgroundOpacity();
    }

    /// <summary>Changes only the background alpha, keeping caption text fully readable.</summary>
    public void ApplyBackgroundOpacity()
    {
        var color = colors[Settings.BackgroundColor].Color;
        BorderBackground.Background = new SolidColorBrush(System.Windows.Media.Color.FromArgb((byte)Settings.Opacity, color.R, color.G, color.B));
    }

    private void OnlyModeButton_Click(object sender, RoutedEventArgs e)
    {
        OnlyMode = OnlyMode switch
        {
            CaptionVisible.Both => CaptionVisible.TranslationOnly,
            CaptionVisible.TranslationOnly => CaptionVisible.SubtitleOnly,
            _ => CaptionVisible.Both
        };
    }
    private void ApplyVisibility()
    {
        ChatView.ShowOriginal = OnlyMode != CaptionVisible.TranslationOnly;
        ChatView.ShowTranslation = OnlyMode != CaptionVisible.SubtitleOnly;
        OnlyModeButton.Content = OnlyMode switch
        {
            CaptionVisible.TranslationOnly => "번역만",
            CaptionVisible.SubtitleOnly => "원문만",
            _ => "원문+번역"
        };
    }
    private void SwitchModeButton_Click(object sender, RoutedEventArgs e) => ChatView.TranslationFirst = !ChatView.TranslationFirst;
    private void MoreOptions_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button || button.ContextMenu == null) return;
        button.ContextMenu.PlacementTarget = button;
        button.ContextMenu.IsOpen = true;
    }
    private void ClickThrough_Click(object sender, RoutedEventArgs e) => SetClickThrough(true);

    /// <summary>Enables or disables mouse passthrough. The main window always provides an unlock button.</summary>
    /// <param name="enabled">True forwards mouse input to the game; false restores overlay interaction.</param>
    public void SetClickThrough(bool enabled)
    {
        var handle = new WindowInteropHelper(this).Handle;
        var style = WindowsAPI.GetWindowLong(handle, WindowsAPI.GWL_EXSTYLE);
        WindowsAPI.SetWindowLong(handle, WindowsAPI.GWL_EXSTYLE,
            enabled ? style | WindowsAPI.WS_EX_TRANSPARENT : style & ~WindowsAPI.WS_EX_TRANSPARENT);
        locked = enabled;
        ControlPanel.Visibility = enabled ? Visibility.Hidden : Visibility.Visible;
        ControlPanel.Opacity = 1;
    }

    private void TopThumb_OnDragDelta(object sender, DragDeltaEventArgs e)
    {
        var height = Math.Max(MinHeight, Height - e.VerticalChange);
        Top += Height - height;
        Height = height;
    }
    private void BottomThumb_OnDragDelta(object sender, DragDeltaEventArgs e) => Height = Math.Max(MinHeight, Height + e.VerticalChange);
    private void LeftThumb_OnDragDelta(object sender, DragDeltaEventArgs e)
    {
        var width = Math.Max(MinWidth, Width - e.HorizontalChange);
        Left += Width - width;
        Width = width;
    }
    private void RightThumb_OnDragDelta(object sender, DragDeltaEventArgs e) => Width = Math.Max(MinWidth, Width + e.HorizontalChange);
    private void TopLeftThumb_OnDragDelta(object sender, DragDeltaEventArgs e) { TopThumb_OnDragDelta(sender, e); LeftThumb_OnDragDelta(sender, e); }
    private void TopRightThumb_OnDragDelta(object sender, DragDeltaEventArgs e) { TopThumb_OnDragDelta(sender, e); RightThumb_OnDragDelta(sender, e); }
    private void BottomLeftThumb_OnDragDelta(object sender, DragDeltaEventArgs e) { BottomThumb_OnDragDelta(sender, e); LeftThumb_OnDragDelta(sender, e); }
    private void BottomRightThumb_OnDragDelta(object sender, DragDeltaEventArgs e) { BottomThumb_OnDragDelta(sender, e); RightThumb_OnDragDelta(sender, e); }
}
