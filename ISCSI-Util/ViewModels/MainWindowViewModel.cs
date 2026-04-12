using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls.ApplicationLifetimes;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ISCSI_Util.Helpers;
using ISCSI_Util.Models;

namespace ISCSI_Util.ViewModels;

public partial class MainWindowViewModel : ObservableObject
{
    public ObservableCollection<IscsiDestino> Destinos { get; } = new();

    // CHAP unidireccional
    [ObservableProperty] private string usuario;
    [ObservableProperty] private string password;
    [ObservableProperty] private bool hayChapActivo;

    // ⭐ NUEVO: MUTUAL CHAP
    [ObservableProperty] private bool hayMutualChapActivo;
    [ObservableProperty] private string usuarioMutualChap;
    [ObservableProperty] private string passwordMutualChap;

    private string _ipServidor;
    public string IpServidor
    {
        get => _ipServidor;
        set => SetProperty(ref _ipServidor, value);
    }

    public MainWindowViewModel()
    {
        Destinos.CollectionChanged += (_, __) =>
        {
            foreach (var d in Destinos)
            {
                d.PropertyChanged -= Destino_PropertyChanged;
                d.PropertyChanged += Destino_PropertyChanged;
            }

            RecalcularChap();
        };
    }

    private void Destino_PropertyChanged(object sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(IscsiDestino.UsaChap) ||
            e.PropertyName == nameof(IscsiDestino.UsaMutualChap))
        {
            RecalcularChap();
        }
    }

    private void RecalcularChap()
    {
        HayChapActivo = Destinos.Any(d => d.UsaChap);
        HayMutualChapActivo = Destinos.Any(d => d.UsaMutualChap);
    }

    public async Task InicializarAsync()
    {
        CargarDestinosConectados();
    }

    private void CargarDestinosConectados()
    {
        var conectados = IscsiHelper.ObtenerDestinosConectados();

        foreach (var d in conectados)
        {
            IscsiHelper.CompletarInformacionDestino(d);

            if (!Destinos.Any(x => x.Iqn == d.Iqn && x.Ip == d.Ip))
                Destinos.Add(d);
        }

        Console.WriteLine($"[AUTO] Se cargaron {Destinos.Count} destinos conectados al iniciar.");
    }

    [RelayCommand]
    public async Task DescubrirDestinosAsync()
    {
        await EnsurePasswordAsync();

        if (string.IsNullOrWhiteSpace(IpServidor))
        {
            Console.WriteLine("No se indicó IP.");
            return;
        }

        var conectados = IscsiHelper.ObtenerDestinosConectados();
        Destinos.Clear();

        foreach (var d in conectados)
        {
            IscsiHelper.CompletarInformacionDestino(d);
            if (!Destinos.Any(x => x.Iqn == d.Iqn && x.Ip == d.Ip))
                Destinos.Add(d);
        }

        var encontrados = IscsiHelper.Descubrir(IpServidor);

        foreach (var destino in encontrados)
        {
            if (!Destinos.Any(x => x.Iqn == destino.Iqn && x.Ip == destino.Ip))
                Destinos.Add(destino);
        }

        Console.WriteLine($"Se descubrieron {Destinos.Count} destinos.");
    }

    [RelayCommand]
    private void ConectarSeleccionados()
    {
        foreach (var destino in Destinos.Where(d => d.Seleccionado))
        {
            // -------------------------
            // CHAP unidireccional
            // -------------------------
            if (destino.UsaChap)
            {
                if (!string.IsNullOrWhiteSpace(Usuario) &&
                    !string.IsNullOrWhiteSpace(Password))
                {
                    destino.UsuarioChap = Usuario;
                    destino.PasswordChap = Password;
                }
                else
                {
                    Console.WriteLine($"[WARN] CHAP habilitado pero sin Usuario/Password para {destino.Iqn}. Saltando.");
                    continue;
                }
            }
            else
            {
                destino.UsuarioChap = null;
                destino.PasswordChap = null;
            }

            // -------------------------
            // MUTUAL CHAP
            // -------------------------
            if (destino.UsaMutualChap)
            {
                if (!string.IsNullOrWhiteSpace(UsuarioMutualChap) &&
                    !string.IsNullOrWhiteSpace(PasswordMutualChap))
                {
                    destino.UsuarioMutualChap = UsuarioMutualChap;
                    destino.PasswordMutualChap = PasswordMutualChap;
                }
                else
                {
                    Console.WriteLine($"[WARN] Mutual CHAP habilitado pero sin Usuario/Password para {destino.Iqn}. Saltando.");
                    continue;
                }
            }
            else
            {
                destino.UsuarioMutualChap = null;
                destino.PasswordMutualChap = null;
            }

            // -------------------------
            // Conexión (doble llamada)
            // -------------------------
            IscsiHelper.Conectar(destino);
            IscsiHelper.Conectar(destino); // NO TOCAR

            IscsiHelper.CompletarInformacionDestino(destino);

            if (destino.Persistir)
            {
                IscsiHelper.ConfigurarPersistencia(destino, "ext4");
                IscsiHelper.CrearServicioPersistencia(destino);
            }
        }
    }

    [RelayCommand]
    private void DesconectarSeleccionados()
    {
        foreach (var destino in Destinos.Where(d => d.Seleccionado))
        {
            IscsiHelper.Desconectar(destino);

            if (destino.Persistir)
            {
                IscsiHelper.EliminarServicioPersistencia(destino);
            }
        }
    }

    [RelayCommand]
    private void InicializarDestino(IscsiDestino destino)
    {
        if (destino == null)
            return;

        IscsiHelper.InicializarDestino(destino);

        if (!destino.TieneFilesystem)
            return;

        IscsiHelper.Conectar(destino);
        IscsiHelper.Conectar(destino); // NO TOCAR

        IscsiHelper.CompletarInformacionDestino(destino);

        if (destino.Persistir)
        {
            IscsiHelper.ConfigurarPersistencia(destino, "ext4");
            IscsiHelper.CrearServicioPersistencia(destino);
        }
    }

    private async Task EnsurePasswordAsync()
    {
        if (App.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop &&
            desktop.MainWindow is ISCSI_Util.Views.MainWindow mw)
        {
            await mw.SolicitarPassword();
        }
    }
}
