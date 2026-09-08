using System.Windows.Input;
using OmniBIM.Desktop.Services;

namespace OmniBIM.Desktop.ViewModels;

/// <summary>
/// Backs the Settings page: the two things OmniBimSettings actually holds. Real, not a
/// placeholder - the chat panel's "add one to Settings" message would otherwise be pointing
/// at a page that couldn't do what it says.
/// </summary>
public sealed class SettingsViewModel : ObservableObject
{
    private string _sharedTimeTrackingFolder = string.Empty;
    public string SharedTimeTrackingFolder { get => _sharedTimeTrackingFolder; set => SetField(ref _sharedTimeTrackingFolder, value); }

    private string _geminiModel = "gemini-3.5-flash-lite";
    public string GeminiModel { get => _geminiModel; set => SetField(ref _geminiModel, value); }

    /// <summary>
    /// Not bound from the PasswordBox directly - WPF has no safe two-way binding for one.
    /// SettingsView's code-behind calls SetApiKey from PasswordChanged instead.
    /// </summary>
    private string _pendingApiKey = string.Empty;

    private bool _hasApiKeyConfigured;
    public bool HasApiKeyConfigured { get => _hasApiKeyConfigured; private set => SetField(ref _hasApiKeyConfigured, value); }

    private string _statusMessage = string.Empty;
    public string StatusMessage { get => _statusMessage; private set => SetField(ref _statusMessage, value); }

    public ICommand SaveCommand { get; }

    public SettingsViewModel()
    {
        var settings = OmniBimSettings.Load();
        SharedTimeTrackingFolder = settings.SharedTimeTrackingFolder;
        GeminiModel = string.IsNullOrWhiteSpace(settings.GeminiModel) ? "gemini-3.5-flash-lite" : settings.GeminiModel;
        HasApiKeyConfigured = GeminiChatService.ResolveApiKey() is not null;

        SaveCommand = new RelayCommand(_ => Save());
    }

    public void SetApiKey(string value) => _pendingApiKey = value;

    private void Save()
    {
        var settings = OmniBimSettings.Load();
        settings.SharedTimeTrackingFolder = SharedTimeTrackingFolder.Trim();
        settings.GeminiModel = string.IsNullOrWhiteSpace(GeminiModel) ? "gemini-3.5-flash-lite" : GeminiModel.Trim();

        // Only overwrite a stored key if the box actually had something typed into it this
        // save - an untouched, empty PasswordBox must not blank out a key that came from the
        // GEMINI_API_KEY environment variable, or one saved in an earlier session.
        if (!string.IsNullOrEmpty(_pendingApiKey)) settings.GeminiApiKey = _pendingApiKey;

        settings.Save();

        HasApiKeyConfigured = GeminiChatService.ResolveApiKey() is not null;
        StatusMessage = $"Saved at {DateTime.Now:HH:mm:ss}. The shared folder change needs a restart to take effect; " +
                         "the API key and model apply to the next message you send.";
    }
}
