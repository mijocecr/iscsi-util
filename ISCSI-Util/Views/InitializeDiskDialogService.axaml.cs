using Avalonia.Controls;
using ISCSI_Util.Helpers;
using ISCSI_Util.Models;
using System.Threading.Tasks;

namespace ISCSI_Util.Views;

public partial class InitializeDiskDialog : Window
{
    private readonly IscsiDestino _destino;

    public InitializeDiskDialog(IscsiDestino destino)
    {
        InitializeComponent();
        _destino = destino;

        DeviceInfo.Text = $"Device: {destino.PartitionPath}";

        LoadFilesystems();

        CancelBtn.Click += (_, _) => Close();
        OkBtn.Click += async (_, _) => await OnInitialize();
    }

    private void LoadFilesystems()
    {
        var fsCandidates = new[] { "ext4", "xfs", "btrfs", "f2fs", "ntfs", "exfat" };

        foreach (var fs in fsCandidates)
        {
            if (IscsiHelper.SoportaFs(fs))
                FsCombo.Items.Add(fs);
        }

        if (FsCombo.Items.Count == 0)
            FsCombo.Items.Add("ext4");

        FsCombo.SelectedIndex = 0;
    }

    private async Task OnInitialize()
    {
        string label = LabelBox.Text?.Trim() ?? "NewDisk";
        string fs = FsCombo.SelectedItem?.ToString() ?? "ext4";

        await IscsiHelper.InicializarDestino(_destino, label, fs);

        Close();
    }
}