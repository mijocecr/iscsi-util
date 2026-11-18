using Avalonia.Controls;
using Avalonia.Interactivity;
using ISCSI_Util.Helpers;

namespace ISCSI_Util.Views;

public partial class IscsiMissingWindow : Window
{
    public IscsiMissingWindow()
        : this(IscsiEnvironment.Inspect())
    {
    }

    public IscsiMissingWindow(IscsiEnvironment.Probe probe)
    {
        InitializeComponent();

        // El aviso de sudo aparece cuando no hay binario sudo en el sistema: sin
        // él iSCSI-Util no puede ejecutar iscsiadm ni como usuario privilegiado.
        SudoNotice.IsVisible = !IscsiEnvironment.IsSudoAvailable(probe);
    }

    private void OnClose(object? sender, RoutedEventArgs e)
    {
        Close();
    }
}
