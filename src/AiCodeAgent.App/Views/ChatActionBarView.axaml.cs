using Avalonia.Controls;

namespace AiCodeAgent.App.Views;

/// <summary>Pipeline, character and effort pickers plus Rewind, under the chat input. Wraps instead of overlapping when the chat column is narrow.</summary>
public partial class ChatActionBarView : UserControl
{
    public ChatActionBarView() => InitializeComponent();
}
