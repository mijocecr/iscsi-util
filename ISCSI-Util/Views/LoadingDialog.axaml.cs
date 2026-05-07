using System;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;

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
        if (tb != null)
            tb.Text = msg;
    }
}