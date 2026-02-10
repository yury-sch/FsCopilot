## 🧰 Developer Setup

This directory contains the **MSFS project** for the FS Copilot module (UI bridge).
To build and load it inside Microsoft Flight Simulator:

1. **Enable Developer Mode** in the simulator settings
   (`Options → General → Developers → Developer Mode`).
2. Install the Microsoft Flight Simulator SDK 
   (`Help -> SDK Installer` in the MSFS main menu).
3. Open and build the `FsCopilot.Bridge.Wasm` project.
2. In the simulator menu bar, open **File → Open Project**.
3. Select and open the file **`fs-copilot.xml`** located in this directory.
4. In the **Project Editor** window:
   - Right-click on the project entry **`fscopilot-bridge`**.
   - Choose **“Build and Mount”** from the context menu.

After the build process completes:
- The compiled package will appear inside the **`Packages`** folder.
- It will be **automatically mounted and loaded** into the simulator for immediate testing.

> 💡 **Tip:** Changes to source files may require rebuilding the package for updates to take effect.