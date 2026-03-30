using System;
using System.Collections.ObjectModel;
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

    private string _ipServidor;
    public string IpServidor
    {
        get => _ipServidor;
        set => SetProperty(ref _ipServidor, value);
    }

    // ⭐ IMPORTANTE:
    // Ya NO llamamos InicializarAsync() aquí.
    // El constructor queda limpio.
    public MainWindowViewModel()
    {
    }

    // ⭐ Ahora es PÚBLICO para que MainWindow lo llame en OnOpened()
    public async Task InicializarAsync()
    {
        // 1) Pedir contraseña primero
        //await EnsurePasswordAsync();

        // 2) Ahora sí podemos usar sudo
        CargarDestinosConectados();
    }

    private void CargarDestinosConectados()
    {
        var conectados = IscsiHelper.ObtenerDestinosConectados();

        foreach (var d in conectados)
        {
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
