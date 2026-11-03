using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using LiveCaptionsTranslator.auth;
using LiveCaptionsTranslator.models;

namespace LiveCaptionsTranslator;

/// <summary>ChatGPT account actions for API Settings; asynchronous operations never expose credentials in the UI.</summary>
public partial class SettingWindow
{
    private CancellationTokenSource? chatGptOperation;
    private bool loadingChatGptAccounts;
    private bool loadingChatGptModels;
    private static ChatGptOAuthConfig ChatGptConfig => Translator.Setting?["ChatGptOAuth"] as ChatGptOAuthConfig
        ?? throw new OAuthException("Translation settings have not been initialized.");

    private async void InitializeChatGpt(object? sender, RoutedEventArgs e)
    {
        await RunChatGptOperation(RefreshChatGptAccounts);
    }

    private async Task RefreshChatGptAccounts(CancellationToken token)
    {
        var vault = await ChatGptRuntime.Session.ReadAsync(token);
        loadingChatGptAccounts = true;
        try
        {
            ChatGptAccounts.ItemsSource = vault.Accounts;
            ChatGptAccounts.SelectedValue = vault.ActiveClientId;
        }
        finally { loadingChatGptAccounts = false; }
        var active = vault.Accounts.Find(x => x.ClientId == vault.ActiveClientId);
        ChatGptAccountStatus.Text = active == null || string.IsNullOrEmpty(active.AccessToken) ? "Signed out"
            : active.CanInvoke ? "Signed in · ChatGPT plan usage enabled" : "Signed in · ChatGPT plan usage disabled";
        var signedIn = active != null && !string.IsNullOrEmpty(active.AccessToken);
        ChatGptConfig.SelectCatalogAccount(signedIn ? active!.ClientId : "");
        RestoreChatGptModelControls();
        if (signedIn && active!.CanInvoke)
            await FetchChatGptModels(token, refresh: false);
        else
            ChatGptStatus.Text = signedIn
                ? "저장된 모델 목록과 설정을 복원했습니다. 목록을 갱신하려면 새로고침을 누르세요."
                : "Choose an account or continue with ChatGPT.";
    }

    private void RestoreChatGptModelControls()
    {
        loadingChatGptModels = true;
        try
        {
            var config = ChatGptConfig;
            ChatGptModels.ItemsSource = config.CachedModels;
            ChatGptModels.SelectedValue = config.ModelName;
            ChatGptReasoningBox.ItemsSource = config.ReasoningOptions;
            ChatGptReasoningBox.SelectedValue = config.ReasoningEffort;
            ChatGptReasoningBox.IsEnabled = !string.IsNullOrEmpty(config.ModelName);
        }
        finally { loadingChatGptModels = false; }
    }

    private async Task FetchChatGptModels(CancellationToken token, bool refresh = true)
    {
        var config = ChatGptConfig;
        var account = config.CatalogAccountId;
        if (string.IsNullOrEmpty(account)) throw new OAuthException("먼저 ChatGPT 계정에 로그인해 주세요.");
        var cached = config.HasCachedCatalog && !refresh;
        var models = await ChatGptRuntime.Responses.GetModelsAsync(config, account, refresh, token);
        token.ThrowIfCancellationRequested();
        if (config.CatalogAccountId != account) return;
        RestoreChatGptModelControls();
        ChatGptStatus.Text = models.Count == 0 ? "No models are available for this account."
            : cached ? "저장된 모델 목록과 설정을 복원했습니다. 목록을 갱신하려면 새로고침을 누르세요."
            : "모델 목록을 저장했습니다. 모델과 추론 강도를 선택하면 다음 실행에도 유지됩니다.";
    }

    private async Task RunChatGptOperation(Func<CancellationToken, Task> action)
    {
        if (chatGptOperation != null) return;
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        chatGptOperation = cancellation;
        ChatGptActions.IsEnabled = false;
        ChatGptCancel.IsEnabled = true;
        try { await action(cancellation.Token); }
        catch (OAuthException ex) { ChatGptStatus.Text = ex.Message; }
        catch (OperationCanceledException) { ChatGptStatus.Text = "The operation was canceled or timed out. You can try again."; }
        catch (Exception) { ChatGptStatus.Text = "Unable to complete the ChatGPT operation. Check the network and local credential storage."; }
        finally
        {
            ChatGptActions.IsEnabled = true;
            ChatGptCancel.IsEnabled = false;
            chatGptOperation = null;
        }
    }

    private async Task LoginChatGpt(bool addAccount, bool requestConsent)
    {
        var selected = ChatGptAccounts.SelectedValue as string;
        await RunChatGptOperation(async token =>
        {
            ChatGptStatus.Text = "Complete sign-in in your browser. Waiting up to five minutes…";
            await ChatGptRuntime.Session.SignInAsync(addAccount ? null : selected, requestConsent, token);
            await RefreshChatGptAccounts(token);
        });
    }

    private async void ChatGptSignIn_Click(object sender, RoutedEventArgs e) => await LoginChatGpt(false, false);
    private async void ChatGptAddAccount_Click(object sender, RoutedEventArgs e) => await LoginChatGpt(true, false);
    private async void ChatGptConsent_Click(object sender, RoutedEventArgs e) => await LoginChatGpt(false, true);
    private void ChatGptCancel_Click(object sender, RoutedEventArgs e) => chatGptOperation?.Cancel();

    private async void ChatGptAccounts_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (loadingChatGptAccounts || ChatGptAccounts.SelectedValue is not string clientId) return;
        await RunChatGptOperation(async token =>
        {
            await ChatGptRuntime.Session.SelectAsync(clientId, token);
            await RefreshChatGptAccounts(token);
        });
    }

    private async void ChatGptLoadModels_Click(object sender, RoutedEventArgs e)
    {
        await RunChatGptOperation(token => FetchChatGptModels(token));
    }

    private void ChatGptModels_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (loadingChatGptModels || ChatGptModels.SelectedItem is not ChatGptModel model) return;
        ChatGptConfig.ModelName = model.Id;
        RestoreChatGptModelControls();
    }

    private void ChatGptReasoning_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (loadingChatGptModels || ChatGptReasoningBox.SelectedValue is not string effort) return;
        ChatGptConfig.ReasoningEffort = effort;
    }

    private async void ChatGptSignOut_Click(object sender, RoutedEventArgs e)
    {
        if (ChatGptAccounts.SelectedValue is not string clientId) return;
        await RunChatGptOperation(async token =>
        {
            bool revoked = await ChatGptRuntime.Session.SignOutAsync(clientId, token);
            await RefreshChatGptAccounts(token);
            ChatGptStatus.Text = revoked ? "Signed out. The renewable session was revoked."
                : "Signed out locally. Remote revocation could not be confirmed; disconnect the app in ChatGPT Settings.";
        });
    }

    private void ChatGptResume_Click(object sender, RoutedEventArgs e)
    {
        if (ChatGptAccounts.SelectedValue is not string clientId) return;
        ChatGptRuntime.Session.Resume(clientId);
        ChatGptStatus.Text = "Requests resumed. ChatGPT account and app limits still apply.";
    }

    private void ChatGptUsage_Click(object sender, RoutedEventArgs e)
    {
        try { Process.Start(new ProcessStartInfo("https://chatgpt.com/#settings/Usage") { UseShellExecute = true }); }
        catch (Exception) { ChatGptStatus.Text = "Open ChatGPT Settings → Usage in your browser."; }
    }
}
