
# iSCSI-Util

Herramienta para Linux escrita en **C# con Avalonia** para descubrir, conectar y desconectar destinos iSCSI de forma gráfica y sencilla.  
esta inspirado en el iniciador iSCSI de Microsoft Windows.
esta herramienta no necesita configuración ni comandos una vez esta instalada. 
Inicialmente se desarrollo para Manjaro/Arch pero existen binarios portables, que pueden usarse sin problema en otras distribuciones.
(https://github.com/mijocecr/Iniciador-iSCSI-GUI-Manjaro/releases/tag/iscsi-util)

---

## ✨ Características

- Descubrimiento de destinos iSCSI (`iscsiadm -m discovery`).
- Conexión y desconexión de múltiples destinos seleccionados.
- Montaje automático y persistencia en `/mnt/iscsi/<IQN>` con detección de filesystem.
- Ajuste dinámico de permisos y grupo de usuario.
- Notificaciones de escritorio en Linux.

---

<img width="630" height="738" alt="Captura de pantalla_20260331_222332" src="https://github.com/user-attachments/assets/f771769c-d942-4c12-8d58-be56b025feef" />




## 📦 Requisitos

- **.NET 9.0**.
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
