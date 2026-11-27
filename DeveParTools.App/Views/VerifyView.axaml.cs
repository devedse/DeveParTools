using Avalonia.Controls;
using DeveParTools.App.ViewModels;

namespace DeveParTools.App.Views;

public sealed partial class VerifyView : UserControl
{
    public VerifyView()
    {
        InitializeComponent();
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (DataContext is VerifyViewModel vm)
        {
            vm.DescribeBlock = block => Grid.Describe(block);
        }
    }
}
