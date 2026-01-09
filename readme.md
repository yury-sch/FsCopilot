> 🌐 Visit the official website — [FS Copilot — Shared Cockpit for MSFS 2024](https://fscopilot.com)

[![Discord](https://img.shields.io/discord/1454265644416765974?label=Discord&logo=discord&color=5865F2)](https://discord.gg/HyKMRp47ka)

> [!WARNING]
> **FS Copilot is currently in active development.**
> This preview version is provided for **testing and familiarization purposes only**.
> Features, stability, and compatibility may change in future updates.

# 🛫 FS Copilot

**FS Copilot** is a companion app for **Microsoft Flight Simulator 2024** that lets multiple pilots control the same aircraft together — in real time.
Fly as a real crew. 👨‍✈️👩‍✈️

![FS Copilot](https://raw.githubusercontent.com/yury-sch/FsCopilot/refs/heads/main/preview.png)

## 🤝 Acknowledgements

This project was inspired by the ideas explored in [YourControls](https://github.com/Sequal32/yourcontrols).

Several good concepts and approaches originated there and helped shape the early direction of this work.

*The core of this project has been written entirely from scratch*, features a distinct architecture, and is implemented in a different programming language.

## ✈️ What FS Copilot Does

- Connects several players to the same aircraft.
- Synchronizes controls: yoke, pedals, brakes, trim, lights, and more.
- Shares instrument states and systems between all participants.
- Works peer-to-peer — no external servers required.
- Supports modern dark Fluent-style UI theme.
- Includes a **developer interface** for quick testing and editing of control mappings,
  available when launching the app with the `--dev` argument.

## 💡 How It Works

FS Copilot connects to your Microsoft Flight Simulator and keeps all control inputs synchronized between pilots.
Each participant sees and feels the same cockpit actions — just like a real multi-crew flight.

## 🚀 Getting Started

1. Launch **FS Copilot**.
   On the first run, it will automatically copy all required files into your *Community* folder.
   If this doesn’t happen, you can do it manually.
2. Enter your partner’s **session code** and click Connect.
3. Launch **Microsoft Flight Simulator**, choose supported aircraft and enjoy your shared flight experience! 🛫

## ⚙️ Compatibility

FS Copilot is built for **Microsoft Flight Simulator 2024**.
Compatibility with **MSFS 2020** has **not been tested**... but should works :)

## 💬 Tips

- Both pilots must use the **same FS Copilot version**.
- Make sure both are flying **the same aircraft model** and using **identical YAML configuration files** for proper synchronization.

## ✨ Why It’s Awesome

- Realistic shared cockpit — no complex setup.
- No accounts, no servers, no hassle.
- Inspired by *YourControls*, but simpler and faster.

## 🧩 For Developers

FS Copilot is built with **.NET 9 (C#)** and designed for modular extensibility.
It includes a flexible networking layer using **peer-to-peer UDP connection by hole punching**,
allowing direct low-latency connections without external servers.

Each aircraft is defined via YAML templates that describe variable mappings,
event bindings, and transformation logic.
These templates support embedded **JavaScript expressions** for dynamic data handling
— enabling complex synchronization behavior right inside the config.

For example:

```yaml
- get: L:AS1000_PFD_SelectedNavIndex # NAV 1/2
  set: (>B:AS1000_PFD_1_NAV_Khz_Button_Push)
- get: L:PFD_CDI_Source # CDI
  set: "value < 3 ? `${value} (>K:AP_NAV_SELECT_SET)` : '(>K:TOGGLE_GPS_DRIVES_NAV1)'"
  skp: H:AS1000_PFD_SOFTKEYS_6
- get: A:KOHLSMAN SETTING MB:0, Millibars # BARO
  set: "`${value * 16} 0 (>K:KOHLSMAN_SET)`"
- get: Z:AUDIO_Knob_Selector_1 # MIC
  set: |
    switch (value) {
        case   0: return '(>H:KMA28_TRANSMISSION_KNOB_COM3)'
        case  20: return '(>H:KMA28_TRANSMISSION_KNOB_COM2)'
        case  40: return '(>H:KMA28_TRANSMISSION_KNOB_COM1)'
        case  60: return '(>H:KMA28_TRANSMISSION_KNOB_COM1_2)'
        case  80: return '(>H:KMA28_TRANSMISSION_KNOB_COM2_1)'
        case 100: return '(>H:KMA28_TRANSMISSION_KNOB_TEL)'
        default: return ''
    }
```

You can find a detailed documentation here:
👉 [FS Copilot — Definitions Guide](https://github.com/yury-sch/FsCopilot/wiki)

## 🧑‍💻 Author

**FS Copilot** is created by aviation and MSFS enthusiast **Yury Sсherbakov.**
Born from the idea that flying together should be as easy as sitting next to your co-pilot.

> “Fly together. Control together.” ✈️
