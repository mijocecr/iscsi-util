
# iSCSI-Util

Herramienta para Linux (Manjaro / Arch) escrita en **C# con Avalonia** para descubrir, conectar y desconectar destinos iSCSI de forma gráfica y sencilla.  
Incluye soporte para notificaciones en Linux mediante `notify-send`. 
El ejecutable corre bien en Ubuntu y probablemente en otras distros tambien (https://github.com/mijocecr/Iniciador-iSCSI-GUI-Manjaro/releases/download/iscsi-util/ISCSI-Util).

---

## ✨ Características

- Descubrimiento de destinos iSCSI (`iscsiadm -m discovery`).
- Conexión y desconexión de múltiples destinos seleccionados.
- Montaje automático en `/mnt/iscsi/<IQN>` con detección de filesystem.
- Ajuste dinámico de permisos y grupo de usuario.
- Notificaciones de escritorio en Linux:
  - Al descubrir: número de destinos encontrados.
  - Al conectar: IQN y punto de montaje.
  - Al desconectar: IQN y punto de montaje liberado.

---

<img width="630" height="738" alt="Captura de pantalla_20260221_141752" src="https://github.com/user-attachments/assets/51a69f96-de09-46f3-9814-4d1f3148dbb9" />



## 📦 Requisitos

- **.NET 9.0** o superior.
- **Avalonia UI**.
- **CommunityToolkit.Mvvm**.
- Herramientas del sistema:
  - `iscsiadm`
  - `lsblk`, `blkid`
  - `notify-send` (para notificaciones en Linux).
- Permisos de administrador (`sudo`) para ejecutar comandos iSCSI.

---

## ⚙️ Instalación

Manjaro/Arch: yay -S iscsi-util

Clona el repositorio y compila con `dotnet`:

```bash
git clone https://github.com/mijocecr/ISCSI-Util.git
cd ISCSI-Util
dotnet build
