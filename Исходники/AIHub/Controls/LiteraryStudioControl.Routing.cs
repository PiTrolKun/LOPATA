using System.Windows.Input;
using AIHub.Services;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;

namespace AIHub.Controls;

public sealed partial class LiteraryStudioControl
{
    private bool CanSendToWriter => State.Role == LiteraryChatProfile.Advisor
        || (State.WriterComment || LiteraryStudioPrompts.Get(State.Action).RequiresInput)
            && !string.IsNullOrWhiteSpace(State.Input) && State.Task.Length > 0
            && (State.Action == "Continue" || State.Result.Length > 0);

    private async Task SendToAdvisorAsync()
    {
        if (IsWorking || _blocked()) return;
        if (State.Role == LiteraryChatProfile.Writer)
        {
            State.ReturnToAdvisor(); Render();
        }
        if (string.IsNullOrWhiteSpace(State.Input)) { StartHint(); Save(); return; }
        await SendAsync();
    }

    private async Task SendToWriterAsync()
    {
        if (IsWorking || _blocked() || !CanSendToWriter) return;
        await SendAsync(transfer: State.Role == LiteraryChatProfile.Advisor);
    }

    private async void InputKeyDown(object sender, KeyEventArgs e)
    {
        var modifiers = Keyboard.Modifiers;
        if (e.Key != Key.Enter || modifiers is not (ModifierKeys.None or ModifierKeys.Control)) return;
        var action = modifiers == ModifierKeys.Control ? State.ControlEnterAction : State.EnterAction;
        // Native Enter retains TextBox caret, selection and undo handling for a newline.
        if (action == StudioSendKeyAction.NewLine && modifiers == ModifierKeys.None) return;
        e.Handled = true;
        if (!e.IsRepeat) await HandleSendKeyAsync(action);
    }

    private async Task HandleSendKeyAsync(StudioSendKeyAction action)
    {
        if (IsWorking || _blocked()) return;
        if (action == StudioSendKeyAction.NewLine)
        {
            _input.SelectedText = Environment.NewLine;
            _input.CaretIndex = _input.SelectionStart + _input.SelectionLength;
            _input.SelectionLength = 0;
            return;
        }
        var writer = action == StudioSendKeyAction.Writer
            || action == StudioSendKeyAction.CurrentRole && State.Role == LiteraryChatProfile.Writer;
        if (writer) await SendToWriterAsync(); else await SendToAdvisorAsync();
    }
}
