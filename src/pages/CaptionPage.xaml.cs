using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using LiveCaptionsTranslator.Utils;

namespace LiveCaptionsTranslator;

/// <summary>Main-window voice chat, sharing the same paired messages as the overlay.</summary>
public partial class CaptionPage : Page
{
    /// <summary>The cached caption page used by the main-window source visibility control.</summary>
    public static CaptionPage? Instance { get; private set; }
    private MainWindow? mainWindow;

    /// <summary>Initializes the shared transcript and attaches sizing only while the page is visible.</summary>
    public CaptionPage()
    {
        InitializeComponent();
        ChatView.DataContext = Translator.Chat;
        Instance = this;
        ApplyPreferences();
        Loaded += (_, _) =>
        {
            mainWindow = Window.GetWindow(this) as MainWindow;
            if (mainWindow == null) return;
            mainWindow.CaptionLogButton.Visibility = Visibility.Visible;
            mainWindow.SizeChanged += MainWindow_SizeChanged;
            AutoHeight();
        };
        Unloaded += (_, _) =>
        {
            if (mainWindow == null) return;
            mainWindow.CaptionLogButton.Visibility = Visibility.Collapsed;
            mainWindow.SizeChanged -= MainWindow_SizeChanged;
            mainWindow = null;
        };
    }

    private void MainWindow_SizeChanged(object sender, SizeChangedEventArgs e) => FitViewport();
    private void FitViewport()
    {
        if (mainWindow != null) Height = Math.Max(220, mainWindow.ActualHeight - 80);
    }
    private void ApplyPreferences()
    {
        var settings = Translator.Setting!.MainWindow;
        ChatView.TranslationFontSize = settings.TranslatedFontSize;
        ChatView.OriginalFontSize = Math.Max(10, settings.OriginalFontSize - 2);
        ChatView.ShowOriginal = settings.ShowOriginal;
    }
    private void ChatView_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (Keyboard.Modifiers != ModifierKeys.Control) return;
        var settings = Translator.Setting!.MainWindow;
        var delta = e.Delta > 0 ? StyleConsts.DELTA_FONT_SIZE : -StyleConsts.DELTA_FONT_SIZE;
        settings.TranslatedFontSize = Math.Clamp(settings.TranslatedFontSize + delta, StyleConsts.MIN_FONT_SIZE, StyleConsts.MAX_FONT_SIZE);
        settings.OriginalFontSize = Math.Clamp(settings.OriginalFontSize + delta, StyleConsts.MIN_FONT_SIZE, StyleConsts.MAX_FONT_SIZE);
        ApplyPreferences();
        e.Handled = true;
    }

    /// <summary>Shows or hides original captions without changing translation history or window height.</summary>
    /// <param name="show">Whether source captions appear below translations.</param>
    public void ShowOriginal(bool show) => ChatView.ShowOriginal = show;

    /// <summary>Keeps a bounded chat viewport; incoming messages never grow the window.</summary>
    public void AutoHeight()
    {
        mainWindow?.AutoHeightAdjust(minHeight: 360);
        FitViewport();
    }
}
