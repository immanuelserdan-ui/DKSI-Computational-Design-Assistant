using System.Collections.ObjectModel;
using System.Windows.Input;
using OmniBIM.Desktop.Models;
using OmniBIM.Desktop.Services;

namespace OmniBIM.Desktop.ViewModels;

public sealed class ChatMessageViewModel(ChatMessage message)
{
    public ChatRole Role { get; } = message.Role;
    public string Text { get; } = message.Text;
    public bool IsUser => Role == ChatRole.User;
    public bool IsSystem => Role == ChatRole.System;
}

/// <summary>
/// Backs the AI chat panel. Every reply is grounded by ChatContextBuilder in exactly what the
/// dashboard currently shows - see its remarks for why that matters. Fire-and-forget SendAsync
/// (RelayCommand has no async Execute), but every failure mode is caught and shown as a System
/// message rather than left to crash the app or vanish silently.
/// </summary>
public sealed class ChatViewModel : ObservableObject
{
    private readonly ClaudeChatService _service = new();
    private readonly MainViewModel _dashboard;
    private readonly List<ChatMessage> _history = [];

    public ObservableCollection<ChatMessageViewModel> Messages { get; } = [];

    private string _draft = string.Empty;
    public string Draft { get => _draft; set => SetField(ref _draft, value); }

    private bool _isSending;
    public bool IsSending { get => _isSending; private set => SetField(ref _isSending, value); }

    public ICommand SendCommand { get; }

    public ChatViewModel(MainViewModel dashboard)
    {
        _dashboard = dashboard;
        SendCommand = new RelayCommand(_ => _ = SendAsync());

        var noKey = ClaudeChatService.ResolveApiKey() is null;
        if (noKey)
        {
            Messages.Add(new ChatMessageViewModel(new ChatMessage
            {
                Role = ChatRole.System,
                Text = "No Claude API key configured yet. Set the ANTHROPIC_API_KEY environment " +
                       "variable and restart OmniBIM to use this chat.",
            }));
        }
    }

    private async Task SendAsync()
    {
        var text = Draft.Trim();
        if (text.Length == 0 || IsSending) return;

        Draft = string.Empty;

        var userMessage = new ChatMessage { Role = ChatRole.User, Text = text };
        _history.Add(userMessage);
        Messages.Add(new ChatMessageViewModel(userMessage));

        IsSending = true;
        try
        {
            var systemPrompt = ChatContextBuilder.BuildSystemPrompt(_dashboard);
            var reply = await _service.SendAsync(systemPrompt, _history);

            var assistantMessage = new ChatMessage { Role = ChatRole.Assistant, Text = reply };
            _history.Add(assistantMessage);
            Messages.Add(new ChatMessageViewModel(assistantMessage));
        }
        catch (ChatServiceException ex)
        {
            Messages.Add(new ChatMessageViewModel(new ChatMessage { Role = ChatRole.System, Text = ex.Message }));
        }
        catch (Exception ex)
        {
            Messages.Add(new ChatMessageViewModel(new ChatMessage
            {
                Role = ChatRole.System,
                Text = $"Something went wrong sending that: {ex.Message}",
            }));
        }
        finally
        {
            IsSending = false;
        }
    }
}
