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

    [ObservableProperty] private string usuario;
    [ObservableProperty] private string password;

    // ⭐ NUEVO: propiedad que controla si se muestran los campos CHAP
    [ObservableProperty] private bool hayChapActivo;

    private string _ipServidor;
    public string IpServidor
    {
        get => _ipServidor;
        set => SetProperty(ref _ipServidor, value);
    }

    // Constructor limpio
    public MainWindowViewModel()
    {
        // ⭐ Suscribirse a cambios en la colección
        Destinos.CollectionChanged += (_, __) =>
        {
            // Suscribir a cambios internos de cada destino
            foreach (var d in Destinos)
            {
                d.PropertyChanged -= Destino_PropertyChanged;
                d.PropertyChanged += Destino_PropertyChanged;
            }

            RecalcularChap();
        };
    }

    // ⭐ Detectar cambios en UsaChap dentro de cada destino
    private void Destino_PropertyChanged(object sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(IscsiDestino.UsaChap))
            RecalcularChap();
    }

    // ⭐ Recalcular si hay CHAP activo en algún destino
    private void RecalcularChap()
    {
        HayChapActivo = Destinos.Any(d => d.UsaChap);
    }

    // Llamado desde MainWindow.OnOpened()
    public async Task InicializarAsync()
    {
        // Aquí NO pedimos password, eso ya lo hace MainWindow
        CargarDestinosConectados();
    }

    private void CargarDestinosConectados()
    {
        var conectados = IscsiHelper.ObtenerDestinosConectados();

        foreach (var d in conectados)
        {
            // ⭐ Completar información igual que un destino descubierto
            IscsiHelper.CompletarInformacionDestino(d);

            if (!Destinos.Any(x => x.Iqn == d.Iqn && x.Ip == d.Ip))
                Destinos.Add(d);
        }

        Console.WriteLine($"[AUTO] Se cargaron {Destinos.Count} destinos conectados al iniciar.");
    }

    
    // Comando asíncrono para descubrir
    [RelayCommand]
    public async Task DescubrirDestinosAsync()
    {
        await EnsurePasswordAsync();

        Destinos.Clear();

        if (string.IsNullOrWhiteSpace(IpServidor))
        {
            Console.WriteLine("No se indicó IP.");
            return;
        }

        var encontrados = IscsiHelper.Descubrir(IpServidor);

        foreach (var destino in encontrados)
            Destinos.Add(destino);

        Console.WriteLine($"Se descubrieron {Destinos.Count} destinos.");
    }

    // Conectar seleccionados con persistencia opcional por destino
    [RelayCommand]
    private void ConectarSeleccionados()
    {
        foreach (var destino in Destinos.Where(d => d.Seleccionado))
        {
            if (destino.UsaChap)
            {
                if (!string.IsNullOrWhiteSpace(Usuario) && !string.IsNullOrWhiteSpace(Password))
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

            IscsiHelper.Conectar(destino);
            IscsiHelper.Conectar(destino); //Esta llamada doble es intencional - no tocar

            if (destino.Persistir)
            {
                IscsiHelper.ConfigurarPersistencia(destino, "ext4");
                IscsiHelper.CrearServicioPersistencia(destino);
            }
        }
    }

    // Desconectar seleccionados y eliminar persistencia si aplica
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

    // Método auxiliar para abrir el PasswordDialog
    private async Task EnsurePasswordAsync()
    {
        if (App.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop &&
            desktop.MainWindow is ISCSI_Util.Views.MainWindow mw)
        {
            await mw.SolicitarPassword();
        }
    }
}
