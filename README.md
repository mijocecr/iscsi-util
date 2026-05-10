# iSCSI‑Util

iSCSI‑Util is a graphical application designed to simplify the management of iSCSI connections on Linux systems.  
It provides a clear and accessible interface for discovering storage targets, establishing sessions, configuring authentication, and reviewing system activity, without requiring direct interaction with command‑line tools.

The goal of the project is to offer a reliable, easy‑to‑use utility that helps administrators and advanced users work with iSCSI in a more intuitive and organized way.

---

## Purpose

iSCSI‑Util was created to address a common need: managing iSCSI targets and sessions in a way that is both straightforward and visually structured.  
While the underlying iSCSI tools are powerful, they can be difficult to use efficiently in day‑to‑day workflows.  
This application presents those capabilities through a modern interface that focuses on clarity, safety, and ease of use.

---

## Key Features

- **Target Discovery**  
  Detect available iSCSI targets on a remote server using a guided workflow.

- **Session Management**  
  Connect to and disconnect from targets with clear status information and confirmation steps.

- **Authentication Configuration**  
  Set up CHAP and Mutual CHAP credentials on a per‑target basis, with a dedicated overview panel for reviewing all stored configurations.

- **System Information and Logs**  
  View relevant log entries and service information to assist with troubleshooting and monitoring.

- **Disk Initialization**  
  Prepare newly attached iSCSI disks through a controlled and clearly documented process.

- **Persistent Configuration**  
  Store per‑target settings in a structured JSON format, allowing the application to remember and reuse configurations.

- **Modern Cross‑Platform Interface**  
  Built with Avalonia, providing a consistent experience across Linux environments.

---

## Installation

Pre‑built packages and release archives are available on the project’s GitHub page:

**https://github.com/mijocecr/iscsi-util/releases**

Download the latest release, extract the contents, and run the application directly.

---

## Requirements

To function correctly, iSCSI‑Util requires:

- A Linux system with `open-iscsi` installed  
- `systemd` for managing the iSCSI service  
- Administrative privileges for certain operations (such as connecting to targets or initializing disks)

---

## Usage Overview

iSCSI‑Util is organized into several sections, each focused on a specific aspect of iSCSI management:

- **Targets** — Discover and review available iSCSI targets.  
- **Sessions** — Monitor active connections and their status.  
- **Configuration** — Manage authentication and advanced options.  
- **Logs** — Inspect relevant system messages.  
- **Disk Tools** — Initialize and prepare newly attached storage devices.

Each section is designed to guide the user through the required steps with clear labels, structured layouts, and informative messages.

---

## Project Structure

The application follows a modular architecture that separates interface, logic, and data handling:

- **Models** — Represent targets, sessions, authentication settings, and configuration data.  
- **Helpers** — Execute system commands and interact with the underlying iSCSI tools.  
- **Utils** — Handle file operations, JSON persistence, and path management.  
- **Views** — Provide the graphical interface for each functional area.  
- **ViewModels** — Connect the interface with the application logic.

This structure ensures maintainability and allows the project to grow in a controlled and predictable way.

---

## Roadmap

Future improvements may include:


- Enhanced monitoring and real‑time status updates  
- Additional tools for inspecting LUNs and storage properties  
- Integration with other system administration utilities

---

## Contributing

Contributions, suggestions, and issue reports are welcome.  
Please use the GitHub issue tracker or submit a pull request to participate in the project’s development.

---

## License

iSCSI‑Util is distributed under the **MIT License**, allowing free use, modification, and distribution.

