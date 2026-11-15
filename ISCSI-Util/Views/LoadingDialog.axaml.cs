using System;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Avalonia.Media;

namespace ISCSI_Util.Views;

public partial class LoadingDialog : Window
{
    public LoadingDialog(string message)
    {
        InitializeComponent();
    }

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public void SetMessage(string msg)
    {
        var tb = this.FindControl<TextBlock>("MessageText");
        if (tb == null)
            return;

        tb.TextWrapping = TextWrapping.Wrap;
        tb.Text = msg;
    }
}