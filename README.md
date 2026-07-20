# WilcoATC

**Free, AI ATC for Microsoft Flight Simulator 2024.**
Talk to a virtual air traffic controller — in English or French — that understands your requests and answers with a realistic radio voice. No subscription. Runs entirely on your machine.

![status](https://img.shields.io/badge/status-alpha-orange)
![platform](https://img.shields.io/badge/platform-Windows-blue)
![sim](https://img.shields.io/badge/sim-MSFS%202024-blue)
![price](https://img.shields.io/badge/price-free-brightgreen)
![offline](https://img.shields.io/badge/works-offline-brightgreen)

> ⚠️ **Alpha software.** This is an early build under active development. Expect bugs and missing features. Feedback is very welcome.

---

## Why WilcoATC ?

The default ATC in MSFS is limited, and the best third-party options (SayIntentions, BeyondATC) are **paid subscriptions that run in the cloud**. WilcoATC takes the opposite approach:

- 🆓 **100% free** — no subscription, no account.
- 🎙️ **Realistic radio voice** — neural text-to-speech passed through an authentic radio filter.
- 🌍 **Bilingual** — fly in English or in French. (Other languages ​​will be added in the future.)

## Features

- **Voice or text input** — speak to ATC with push-to-talk, or type your requests.
- **Understands intent, not just keywords** — request clearance, pushback, taxi, takeoff, and more.
- **Context-aware** — requests are validated against your flight phase and the frequency you're tuned to (ask for pushback in the air and you'll be refused, just like real life).
- **Full departure flow** — IFR clearance → pushback → taxi → takeoff → frequency handoffs.
- **SimBrief integration** — imports your flight plan to fill in destination, cruise altitude, callsign and the real **SID** for your clearance.
- **Live frequency sync** — reads your COM radio directly from the sim; change frequency in-game and the app follows, then prompts your check-in on the new station.
- **Realistic phraseology** — proper callsigns, readbacks, and ICAO conventions.

## How it works

WilcoATC is a native Windows desktop app that:

1. Connects to the simulator via **SimConnect** to read your aircraft state (position, altitude, COM frequency, etc.).
2. Transcribes your voice locally (offline speech-to-text).
3. Recognizes your **intent** and validates it against a flight-phase state machine.
4. Generates the appropriate ATC transmission and speaks it through a local neural voice + radio-effect pipeline.

Everything runs on your machine — no cloud, no API keys required.

## Requirements

- **Windows** 10/11
- **Microsoft Flight Simulator 2024** (2020 may work)
- **MSFS SDK / SimConnect** available on the system
- **.NET 8** runtime
- *(Optional)* a **SimBrief** account for flight-plan integration

## Installation

1. Download the latest release from the [Releases](../../releases) page.
2. Extract the archive.
3. Run `WilcoATC.exe`.
4. On first launch, the app downloads the default voice model automatically (one-time).

*(Building from source: clone the repo, open the solution in Visual Studio, restore NuGet packages, and build for `x64`.)*

## Getting started

1. Start MSFS and spawn at an airport.
2. Launch WilcoATC — it connects automatically ("Connected").
3. *(Optional)* Enter your SimBrief username in **Settings** and click **Import** to load your flight plan.
4. Tune your COM radio to the airport's clearance/ground frequency.
5. Request your clearance — by voice (push-to-talk) or text:

```
"Geneva Delivery, Swiss 2814, request IFR clearance to Zurich."
```

ATC replies with a full clearance, and you read it back. Continue through pushback, taxi, and takeoff.

## Configuration

- **Language** — English or French (affects the voice, speech recognition, and phraseology).
- **Voice (TTS)** — local neural voice by default; add other voice models in Settings.
- **Audio output** — choose your headset or a virtual audio cable.
- **SimBrief** — username / Pilot ID for flight-plan import.
- **Test Mode** — bypass phase prerequisites to test any interaction (e.g., takeoff) in isolation.

## Roadmap

- [x] SimConnect connection + live COM frequency reading
- [x] Local neural voice + radio-effect pipeline
- [x] Intent recognition + context validation
- [-] Ground → departure flow (clearance, pushback, taxi, takeoff)
- [x] SimBrief integration (destination, callsign, SID)
- [x] In-flight frequency handoffs
- [ ] Arrival flow (approach, landing, taxi-in)
- [ ] Coherent AI traffic sequencing
- [ ] Worldwide procedure/sector data
- [ ] VATSIM-aware frequencies

## Project status

**Alpha (v0.1.0).** Core departure flow works; many features are in progress. Some data (SIDs outside covered regions, en-route center frequencies) may be approximate or unavailable — see the disclaimer below.

## Contributing

Issues, ideas, and pull requests are welcome. If you hit a bug, please include: what you said/typed, the airport and flight phase, and what the ATC replied.

## Credits

Built on great free/open tools and data:

- [SimConnect](https://docs.flightsimulator.com/) — sim integration (Microsoft)
- [sherpa-onnx](https://github.com/k2-fsa/sherpa-onnx) — local speech recognition & TTS
- [Piper](https://github.com/rhasspy/piper) — neural voice models
- [OurAirports](https://ourairports.com/data/) — airports & frequencies
- [OpenFlights](https://openflights.org/data.html) — airline callsigns
- [SimBrief](https://www.simbrief.com/) — flight planning

## Disclaimer

For **flight simulation entertainment only**. Not for real-world navigation, flight training, or actual air traffic control. Procedures, SIDs, and frequencies may be illustrative or approximate. Not affiliated with Microsoft, SimBrief, VATSIM, or any aviation authority.

Heads up: this is an alpha release. You will run into bugs — lots of them, really a lot. Use it knowingly and at your own risk.

Want to contribute, or just curious? Join our Discord: https://discord.gg/z4GgaS6Nnk

Prefer to simply request a feature or report a bug without joining 

## License

Released under the [MIT License](LICENSE).
