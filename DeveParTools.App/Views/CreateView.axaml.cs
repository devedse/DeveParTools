using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Platform.Storage;
using DeveParTools.App.ViewModels;

namespace DeveParTools.App.Views;

public sealed partial class CreateView : UserControl
{
    public CreateView()
    {
        InitializeComponent();
        DragDrop.SetAllowDrop(this, true);
        AddHandler(DragDrop.DropEvent, (_, e) =>
        {
            if (DataContext is CreateViewModel vm)
            {
                foreach (var item in e.DataTransfer.TryGetFiles() ?? [])
                {
                    if (item.TryGetLocalPath() is { } path)
                    {
                        vm.Add(path);
                    }
                }
            }
        });
    }
}
