using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Platform.Storage;

namespace DeveParTools.App.Controls;

/// <summary>
/// Drag and drop for MVVM: dropping files or folders on a control runs a command with their local paths
/// (an <see cref="IReadOnlyList{T}"/> of strings), so the view needs no code-behind.
/// </summary>
/// <example><code>&lt;UserControl c:FileDrop.Command="{Binding AddPathsCommand}"&gt;</code></example>
public static class FileDrop
{
    public static readonly AttachedProperty<ICommand?> CommandProperty = AvaloniaProperty.RegisterAttached<Control, ICommand?>("Command", typeof(FileDrop));

    static FileDrop()
    {
        CommandProperty.Changed.AddClassHandler<Control>((control, e) =>
        {
            control.RemoveHandler(DragDrop.DropEvent, OnDrop);
            DragDrop.SetAllowDrop(control, e.NewValue != null);
            if (e.NewValue != null)
            {
                control.AddHandler(DragDrop.DropEvent, OnDrop);
            }
        });
    }

    public static ICommand? GetCommand(Control control) => control.GetValue(CommandProperty);

    public static void SetCommand(Control control, ICommand? value) => control.SetValue(CommandProperty, value);

    private static void OnDrop(object? sender, DragEventArgs e)
    {
        if (sender is not Control control || GetCommand(control) is not { } command)
        {
            return;
        }

        IReadOnlyList<string> paths = e.DataTransfer.TryGetFiles()?.Select(f => f.TryGetLocalPath()).OfType<string>().ToList() ?? [];
        if (paths.Count > 0 && command.CanExecute(paths))
        {
            command.Execute(paths);
        }
    }
}
