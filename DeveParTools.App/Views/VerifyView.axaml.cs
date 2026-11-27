using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Platform.Storage;
using DeveParTools.App.ViewModels;

namespace DeveParTools.App.Views;

public sealed partial class VerifyView : UserControl
{
    public VerifyView()
    {
        InitializeComponent();
        DragDrop.SetAllowDrop(this, true);
        AddHandler(DragDrop.DropEvent, OnDrop);
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (DataContext is VerifyViewModel vm)
        {
            vm.DescribeBlock = block => Grid.Describe(block);
        }
    }

    private async void OnDrop(object? sender, DragEventArgs e)
    {
        if (DataContext is not VerifyViewModel vm)
        {
            return;
        }

        var file = e.DataTransfer.TryGetFiles()?.Select(f => f.TryGetLocalPath()).FirstOrDefault(p => p != null);
        if (file != null)
        {
            await vm.OpenAsync(file);
        }
    }
}
