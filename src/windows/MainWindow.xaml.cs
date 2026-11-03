using System.Diagnostics;
using System.Reflection;
using System.Windows;
using Wpf.Ui.Appearance;
using Wpf.Ui.Controls;

using LiveCaptionsTranslator.utils;
using LiveCaptionsTranslator.Utils;
using Button = Wpf.Ui.Controls.Button;

namespace LiveCaptionsTranslator
{
    public partial class MainWindow : FluentWindow
    {
        public OverlayWindow? OverlayWindow { get; set; } = null;
        public bool IsAutoHeight { get; set; } = true;

        public MainWindow()
        {
            InitializeComponent();
            ApplicationThemeManager.Apply(ApplicationTheme.Dark);

            Loaded += (s, e) =>
            {
                ApplicationThemeManager.Apply(ApplicationTheme.Dark);
                RootNavigation.Navigate(typeof(CaptionPage));
                IsAutoHeight = true;
                CheckForFirstUse();
                CheckForUpdates();
            };

            double screenWidth = SystemParameters.PrimaryScreenWidth;
            double screenHeight = SystemParameters.PrimaryScreenHeight;

            var windowState = WindowHandler.LoadState(this, Translator.Setting);
            if (windowState.Left <= 0 || windowState.Left >= screenWidth ||
                windowState.Top <= 0 || windowState.Top >= screenHeight)
            {
                WindowHandler.RestoreState(this, new Rect(
                    (screenWidth - 650) / 2, Math.Max(0, (screenHeight - 440) / 2), 650, 440));
            }
            else
                WindowHandler.RestoreState(this, windowState);

            Top = Math.Clamp(Top, SystemParameters.WorkArea.Top,
                Math.Max(SystemParameters.WorkArea.Top, SystemParameters.WorkArea.Bottom - Height));
            ToggleTopmost(Translator.Setting.MainWindow.Topmost);
            ShowLogCard(Translator.Setting.MainWindow.ShowOriginal);
        }

        private void TopmostButton_Click(object sender, RoutedEventArgs e)
        {
            ToggleTopmost(!this.Topmost);
        }

        private void OverlayModeButton_Click(object sender, RoutedEventArgs e)
        {
            if (OverlayWindow == null)
            {
                OverlayWindow = new OverlayWindow();
                double screenWidth = SystemParameters.PrimaryScreenWidth;
                double screenHeight = SystemParameters.PrimaryScreenHeight;
                var bounds = WindowHandler.LoadState(OverlayWindow, Translator.Setting);
                if (bounds.IsEmpty || bounds.Left < 0 || bounds.Left >= screenWidth || bounds.Top < 0 || bounds.Top >= screenHeight)
                    bounds = new Rect(32, Math.Max(0, screenHeight - 380), 520, 320);
                WindowHandler.RestoreState(OverlayWindow, bounds);
                OverlayWindow.Top = Math.Clamp(OverlayWindow.Top, SystemParameters.WorkArea.Top,
                    Math.Max(SystemParameters.WorkArea.Top, SystemParameters.WorkArea.Bottom - OverlayWindow.Height));
                OverlayWindow.SizeChanged += (s, args) => WindowHandler.SaveState(OverlayWindow, Translator.Setting);
                OverlayWindow.LocationChanged += (s, args) => WindowHandler.SaveState(OverlayWindow, Translator.Setting);
                OverlayWindow.Closed += (_, _) =>
                {
                    OverlayWindow = null;
                    OverlayModeButton.Content = "오버레이";
                    UnlockOverlayButton.Visibility = Visibility.Collapsed;
                };
                OverlayWindow.Show();
                OverlayModeButton.Content = "오버레이 닫기";
                UnlockOverlayButton.Visibility = Visibility.Visible;
            }
            else OverlayWindow.Close();
        }

        private void UnlockOverlay_Click(object sender, RoutedEventArgs e) => OverlayWindow?.SetClickThrough(false);

        private void LogOnlyButton_Click(object sender, RoutedEventArgs e)
        {
            Translator.LogOnlyFlag = !Translator.LogOnlyFlag;
            LogOnlyButton.Content = Translator.LogOnlyFlag ? "번역 재개" : "일시정지";
            Translator.ClearContexts();
        }

        private void CaptionLogButton_Click(object sender, RoutedEventArgs e)
        {
            var settings = Translator.Setting!.MainWindow;
            settings.ShowOriginal = !settings.ShowOriginal;
            ShowLogCard(settings.ShowOriginal);
        }

        private void MainWindow_LocationChanged(object sender, EventArgs e)
        {
            var window = sender as Window;
            WindowHandler.SaveState(window, Translator.Setting);
        }

        private void MainWindow_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            MainWindow_LocationChanged(sender, e);
            IsAutoHeight = false;
        }

        public void ToggleTopmost(bool enabled)
        {
            var button = TopmostButton as Button;
            var symbolIcon = button?.Icon as SymbolIcon;
            symbolIcon.Filled = enabled;
            this.Topmost = enabled;
            Translator.Setting.MainWindow.Topmost = enabled;
        }

        private void CheckForFirstUse()
        {
            if (!Translator.FirstUseFlag)
                return;

            RootNavigation.Navigate(typeof(SettingPage));
            LiveCaptionsHandler.RestoreLiveCaptions(Translator.Window);

            Dispatcher.InvokeAsync(() =>
            {
                var welcomeWindow = new WelcomeWindow
                {
                    Owner = this
                };
                welcomeWindow.Show();
            }, System.Windows.Threading.DispatcherPriority.Background);
        }

        private async Task CheckForUpdates()
        {
            if (Translator.FirstUseFlag)
                return;

            string latestVersion = string.Empty;
            try
            {
                latestVersion = await UpdateUtil.GetLatestVersion();
            }
            catch (Exception ex)
            {
                SnackbarHost.Show("[ERROR] Update Check Failed.", ex.Message, SnackbarType.Error,
                    timeout: 2, closeButton: true);

                return;
            }

            var currentVersion = Assembly.GetExecutingAssembly().GetName().Version?.ToString();
            var ignoredVersion = Translator.Setting.IgnoredUpdateVersion;
            if (!string.IsNullOrEmpty(ignoredVersion) && ignoredVersion == latestVersion)
                return;
            if (!string.IsNullOrEmpty(latestVersion) && latestVersion != currentVersion)
            {
                var dialog = new Wpf.Ui.Controls.MessageBox
                {
                    Title = "New Version Available",
                    Content = $"A new version has been detected: {latestVersion}\n" +
                              $"Current version: {currentVersion}\n" +
                              $"Please visit GitHub to download the latest release.",
                    PrimaryButtonText = "Update",
                    CloseButtonText = "Ignore this version"
                };
                var result = await dialog.ShowDialogAsync();

                if (result == Wpf.Ui.Controls.MessageBoxResult.Primary)
                {
                    var url = UpdateUtil.GitHubReleasesUrl;
                    try
                    {
                        Process.Start(new ProcessStartInfo
                        {
                            FileName = url,
                            UseShellExecute = true
                        });
                    }
                    catch (Exception ex)
                    {
                        SnackbarHost.Show("[ERROR] Open Browser Failed.", ex.Message, SnackbarType.Error,
                            timeout: 2, closeButton: true);
                    }
                }
                else
                    Translator.Setting.IgnoredUpdateVersion = latestVersion;
            }
        }

        /// <summary>Updates the main chat's source-text toggle.</summary>
        /// <param name="enabled">True shows original captions below translations.</param>
        public void ShowLogCard(bool enabled)
        {
            CaptionLogButton.Content = enabled ? "원문 켜짐" : "원문 꺼짐";
            CaptionPage.Instance?.ShowOriginal(enabled);
        }

        public void AutoHeightAdjust(int minHeight = -1, int maxHeight = -1)
        {
            if (minHeight > 0 && Height < minHeight)
            {
                Height = minHeight;
                IsAutoHeight = true;
            }

            if (IsAutoHeight && maxHeight > 0 && Height > maxHeight)
                Height = maxHeight;
        }
    }
}