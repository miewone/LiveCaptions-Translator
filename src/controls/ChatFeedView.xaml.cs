using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using LiveCaptionsTranslator.models;

namespace LiveCaptionsTranslator;

/// <summary>Reusable game-style caption feed with paired source text, bounded history and opt-in follow scrolling.</summary>
public partial class ChatFeedView : UserControl
{
    /// <summary>Translation text size in device-independent pixels.</summary>
    public static readonly DependencyProperty TranslationFontSizeProperty = DependencyProperty.Register(
        nameof(TranslationFontSize), typeof(double), typeof(ChatFeedView), new PropertyMetadata(18.0));
    /// <summary>Source text size in device-independent pixels.</summary>
    public static readonly DependencyProperty OriginalFontSizeProperty = DependencyProperty.Register(
        nameof(OriginalFontSize), typeof(double), typeof(ChatFeedView), new PropertyMetadata(12.0));
    /// <summary>Controls source-text visibility without discarding transcript data.</summary>
    public static readonly DependencyProperty ShowOriginalProperty = DependencyProperty.Register(
        nameof(ShowOriginal), typeof(bool), typeof(ChatFeedView), new PropertyMetadata(true));
    /// <summary>Controls translation visibility without discarding transcript data.</summary>
    public static readonly DependencyProperty ShowTranslationProperty = DependencyProperty.Register(
        nameof(ShowTranslation), typeof(bool), typeof(ChatFeedView), new PropertyMetadata(true));
    /// <summary>Places translation above its source when true.</summary>
    public static readonly DependencyProperty TranslationFirstProperty = DependencyProperty.Register(
        nameof(TranslationFirst), typeof(bool), typeof(ChatFeedView), new PropertyMetadata(true));
    /// <summary>Translation foreground brush, shared by live and completed captions.</summary>
    public static readonly DependencyProperty TranslationBrushProperty = DependencyProperty.Register(
        nameof(TranslationBrush), typeof(Brush), typeof(ChatFeedView), new PropertyMetadata(Brushes.White));
    /// <summary>Translation font weight.</summary>
    public static readonly DependencyProperty TranslationWeightProperty = DependencyProperty.Register(
        nameof(TranslationWeight), typeof(FontWeight), typeof(ChatFeedView), new PropertyMetadata(FontWeights.SemiBold));

    /// <summary>Gets or sets translation text size.</summary>
    public double TranslationFontSize { get => (double)GetValue(TranslationFontSizeProperty); set => SetValue(TranslationFontSizeProperty, value); }
    /// <summary>Gets or sets source text size.</summary>
    public double OriginalFontSize { get => (double)GetValue(OriginalFontSizeProperty); set => SetValue(OriginalFontSizeProperty, value); }
    /// <summary>Gets or sets whether source captions are visible.</summary>
    public bool ShowOriginal { get => (bool)GetValue(ShowOriginalProperty); set => SetValue(ShowOriginalProperty, value); }
    /// <summary>Gets or sets whether translated captions are visible.</summary>
    public bool ShowTranslation { get => (bool)GetValue(ShowTranslationProperty); set => SetValue(ShowTranslationProperty, value); }
    /// <summary>Gets or sets the order of translation and source text.</summary>
    public bool TranslationFirst { get => (bool)GetValue(TranslationFirstProperty); set => SetValue(TranslationFirstProperty, value); }
    /// <summary>Gets or sets the translation foreground.</summary>
    public Brush TranslationBrush { get => (Brush)GetValue(TranslationBrushProperty); set => SetValue(TranslationBrushProperty, value); }
    /// <summary>Gets or sets the translation font weight.</summary>
    public FontWeight TranslationWeight { get => (FontWeight)GetValue(TranslationWeightProperty); set => SetValue(TranslationWeightProperty, value); }

    private bool followLatest = true;

    /// <summary>Initializes the chat view; the DataContext must be a ChatTranscript.</summary>
    public ChatFeedView() => InitializeComponent();

    private void ConversationScroll_Changed(object sender, ScrollChangedEventArgs e)
    {
        if (e.ExtentHeightChange == 0 && e.ViewportHeightChange == 0)
            followLatest = ConversationScroll.ScrollableHeight - ConversationScroll.VerticalOffset < 8;
        if (followLatest && (e.ExtentHeightChange != 0 || e.ViewportHeightChange != 0))
            ConversationScroll.ScrollToEnd();
        LatestButton.Visibility = followLatest ? Visibility.Collapsed : Visibility.Visible;
    }

    private void LatestButton_Click(object sender, RoutedEventArgs e)
    {
        followLatest = true;
        ConversationScroll.ScrollToEnd();
        LatestButton.Visibility = Visibility.Collapsed;
    }

    private void CopyMessage_Click(object sender, MouseButtonEventArgs e)
    {
        if (sender is not TextBlock text || string.IsNullOrWhiteSpace(text.Text)) return;
        try { Clipboard.SetText(text.Text); }
        catch (System.Runtime.InteropServices.COMException) { return; }
        e.Handled = true;
    }

    private void CopyReply_Click(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: ReplySuggestion reply }) return;
        try { Clipboard.SetText(reply.Text); }
        catch (System.Runtime.InteropServices.COMException) { return; }
        e.Handled = true;
    }
}
