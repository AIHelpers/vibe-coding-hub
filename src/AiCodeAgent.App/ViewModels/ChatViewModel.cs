using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System;
using System.Collections.ObjectModel;
using System.Threading.Tasks;
using AiCodeAgent.App.Services;

namespace AiCodeAgent.App.ViewModels;

public partial class ChatViewModel : ObservableObject
{
    private readonly AgentService _agentService;

    [ObservableProperty]
    private string _inputText = string.Empty;

    [ObservableProperty]
    private bool _isProcessing;

    public ObservableCollection<ChatMessage> Messages { get; } = new();

    public ChatViewModel(AgentService agentService)
    {
        _agentService = agentService;

        // Add welcome message
        Messages.Add(new ChatMessage
        {
            Role = "Assistant",
            Content = "Hello! I'm your AI Code Assistant. How can I help you today? You can ask me to:\n" +
                      "- Read, write, or edit files\n" +
                      "- Search for code using grep\n" +
                      "- Run shell commands\n" +
                      "- Work with git repositories\n" +
                      "- And much more!"
        });
    }

    [RelayCommand]
    private async Task SendAsync()
    {
        if (string.IsNullOrWhiteSpace(InputText) || IsProcessing)
            return;

        var userMessage = InputText.Trim();
        InputText = string.Empty;

        // Add user message
        Messages.Add(new ChatMessage
        {
            Role = "User",
            Content = userMessage
        });

        IsProcessing = true;

        try
        {
            // Add assistant message placeholder
            var assistantMessage = new ChatMessage
            {
                Role = "Assistant",
                Content = "Thinking..."
            };
            Messages.Add(assistantMessage);

            // Get response from agent
            var response = await _agentService.SendMessageAsync(userMessage);

            // Update assistant message
            assistantMessage.Content = response;
        }
        catch (Exception ex)
        {
            Messages.Add(new ChatMessage
            {
                Role = "Assistant",
                Content = $"Error: {ex.Message}"
            });
        }
        finally
        {
            IsProcessing = false;
        }
    }
}

public class ChatMessage
{
    public string Role { get; set; } = string.Empty;
    public string Content { get; set; } = string.Empty;
}