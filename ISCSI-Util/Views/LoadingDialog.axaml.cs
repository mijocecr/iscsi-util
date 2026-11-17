using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Avalonia.Media;

namespace ISCSI_Util.Views;

public partial class LoadingDialog : Window
{
    public LoadingDialog(string message)
    {
        InitializeComponent();
        SetMessage(message);
        ApplyThemeColors();
    }

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);
    }

    private void ApplyThemeColors()
    {
        bool isLight = Application.Current?.ActualThemeVariant == Avalonia.Styling.ThemeVariant.Light;

        // Fondo de la tarjeta central según el tema
        if (CardBorder != null)
        {
            CardBorder.Background = new SolidColorBrush(Color.Parse(isLight ? "#FFFFFF" : "#1E1E1E"));
        }

        // Color del texto principal para que contraste bien en modo claro u oscuro
        if (MessageText != null)
        {
            MessageText.Foreground = new SolidColorBrush(Color.Parse(isLight ? "#1B2838" : "#FFFFFF"));
        }

        // Fondo de la barra de progreso adaptado al tema
        if (ProgressBarControl != null)
        {
            ProgressBarControl.Background = new SolidColorBrush(Color.Parse(isLight ? "#E0E0E0" : "#333333"));
        }
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