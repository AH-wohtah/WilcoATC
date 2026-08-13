# WilcoATC — Release Notes

**26 July 2026**

This update is about getting you flying faster and keeping the frequency data accurate:
a first-run setup assistant, an in-window flight onboarding, community frequency
reporting/import, and a cleaner nav rail — plus a standalone build that needs no .NET install.

---

## Summary

| Topic | In short |
|---|---|
| [Setup assistant](#1-first-run-setup-assistant) | First-launch wizard: models, language, Push-to-Talk |
| [Flight onboarding](#2-in-app-flight-onboarding) | Start a flight in-window, manual entry, choose the phase |
| [Community frequencies](#3-community-frequencies) | Report a missing frequency + import validated ones |
| [Interface](#4-interface) | RAD/FRQ/LOG/CFG become icons, simpler report form |
| [Session data](#5-session-only-flight-data) | Flight data no longer persists between sessions |
| [Fixes](#6-fixes) | Callsign on the radio, ground frequency, joystick PTT |
| [Distribution](#distribution) | Self-contained folder, no .NET install required |

---

## 1. First-run setup assistant

On the **very first launch only**, a guided wizard walks you through everything at once:

- **Downloads all voice + speech-recognition models** up front, so nothing stalls mid-flight.
- Asks for your **interface language**.
- Lets you bind **Push-to-Talk** to a keyboard key **or an external device** — joystick,
  throttle, gamepad. External buttons are read directly, with per-device button detection so
  the right button is captured (see the *Button 17* fix in §6).

---

## 2. In-app flight onboarding

Start a flight **directly in the window** — no separate pop-ups.

- **Enter the flight yourself** (SimBrief is not required) — callsign, aircraft, origin,
  destination — and pick the **airline** you want to fly.
- **Choose your starting phase**: *at the gate*, *taxiing*, or *in flight*.
- The **first read-back and first frequency now match your real flight** — e.g. *Brussels
  Delivery* when you start at EBBR, with the correct company callsign.

---

## 3. Community frequencies

The bundled dataset has gaps versus the sim; this update makes them fixable by the community.

- **Report a missing frequency** in one click — **Discord username + airport + frequency** —
  sent to the project so it can be validated and added.
- **Import validated frequencies** from a CSV in Settings, so approved fixes are merged
  straight back into the app. Live SimConnect facility data still takes priority when present.

---

## 4. Interface

- **Nav rail icons**: the RAD / FRQ / LOG / CFG labels are now clean vector icons that light up
  cyan when active.
- **Simpler frequency report form**: it only handles *missing* frequencies now, and just asks
  for your Discord username, the airport, and the frequency.
- **Cleaner shell**: connection status sits top-right (grey dot + *Not connected* when the sim
  is offline), and the ATC log shows an empty-state message when no flight is running.

---

## 5. Session-only flight data

Flight information is **no longer kept between sessions**. Closing the app leaves a clean
slate and an empty onboarding the next time you open it — no stale flight lingering.

---

## 6. Fixes

- ATC now uses your **callsign** on the radio instead of reading out the flight number.
- ATC **stays silent in menus** — it only talks once you're actually in the aircraft / flying.
- **"Contact ground" now includes the actual ground frequency** when it exists.
- Fixed a phantom **"Button 17"** stuck reading when assigning a controller button for
  Push-to-Talk (baseline edge-detection + per-device button-count filtering).

---

## Distribution

Ships as a **self-contained folder** — **no .NET installation required** on the target PC.
A single-file `.exe` remains impossible (the SimConnect connector is a mixed-mode assembly
.NET cannot embed), so distribution stays a folder you can zip and share.

---

**21 July 2026**

A big session: full switch to English, a new speech recognition engine, a visual identity,
and a series of fixes for bugs that left ATC silent or inconsistent in flight.

---

## Summary

| Topic | In short |
|---|---|
| [English only](#1-english-only) | All radio work moves to English, French is removed |
| [Speech recognition](#2-speech-recognition-stt) | New model: **~40% fewer errors**, measured |
| [Spoken numbers](#3-spoken-numbers) | "level 3 to 0" → 320, decimal frequencies restored |
| [In-flight handoffs](#4-frequency-handoffs) | ATC no longer goes quiet after takeoff |
| [Departure clearance](#5-departure-clearance) | No more arrival procedure/runway in the clearance |
| [Flight phases](#6-flight-phases) | No more "unable at this time" refusals |
| [Interface](#7-interface) | Logo, icon, cabin packs put on hold |
| [Diagnostics](#8-diagnostics) | A log file on every launch + a crash fixed |

---

## 1. English only

The application now speaks **English only** — ICAO phraseology, which is also the most
realistic behaviour.

- **Removed**: French dictionary, French voices, French ATC templates, French recognition
  keywords, French ambient traffic.
- `AtcLanguage` now holds a single member. That is deliberate: the compiler becomes the
  mandatory checkpoint if a language is ever reintroduced.
- The **interface language** stays configurable: English, German, Spanish, Italian,
  Portuguese, Dutch (168 labels, parity verified across all six).
- Status messages and the log move to English — including the famous
  "Connecté à SunRise", now *Connected to SunRise*.

### Voice catalogue

- **25 English voices** (US + GB), including 7 multi-speaker models.
- **The "low" tier is gone.** Those models are sampled at 16 kHz against 22.05 kHz: that is
  exactly what produces the tinny timbre. Every catalogue URL has been verified as
  reachable.
- Bulk download of all missing voices.

---

## 2. Speech recognition (STT)

The transcription engine has been replaced. The decision was made **by measurement**, not by
guesswork: a test bench synthesises ATC phraseology with three different voices, then
compares the engines.

| Model | Word errors | Time / phrase |
|---|---:|---:|
| **Parakeet TDT 0.6B v2** ← new default | **8.8%** | 0.15 s |
| whisper-base.en | 14.4% | 0.18 s |
| whisper-tiny ← old default | 14.7% | 0.13 s |

Two lessons from the measurements that intuition got wrong:

- **A bigger Whisper brought almost nothing** (14.4% against 14.7%). The intermediate
  `base.en` step was dropped.
- **ATC "hotwords" made results worse** (10.5%). The model's vocabulary is sub-word based;
  whole words simply do not encode into it. Avenue abandoned.

Other improvements:

- **Microphone conditioning**: DC offset removal and peak normalisation to −3 dBFS. A headset
  mic set too low was badly degrading transcription. Gain is capped, and silence is never
  amplified.
- **Phraseology corrector**: fillers removed, common mishearings fixed
  (`push back` → pushback, `ready for the party` → ready for departure, `will co` → wilco).
- **Tolerant matching**: a keyword matches within one typo (`departur`, `pushbak`,
  `clerance`), but stays exact on short words so it does not trigger by accident. The
  **longest** keyword wins, not the first one in the list.
- The older Whisper models are still supported; the best **installed** model is selected
  automatically.

> The Parakeet model (~460 MB) was installed straight into
> `%LOCALAPPDATA%\WilcoATC\asr\`. Deleting the folder is enough to roll back.

---

## 3. Spoken numbers

**"level 320" heard as "3 to 0".** The transcription writes *two* as **"to"**. You cannot
convert "to" everywhere without breaking *taxi **to** the holding point* or
*cleared **to** Dubai*. So the rule is contextual: a homophone only becomes a digit when it
is **surrounded by digits**. Same treatment for `for`→4, `won`→1, `ate`→8.

```
level 3 to 0          →  level 320        ✔
squawk 4 for 2 1      →  squawk 4421      ✔
climb to 5000         →  unchanged        ✔
taxi to the holding…  →  unchanged        ✔
```

**Frequencies stripped of their decimal.** "one one eight decimal seven" came out as
`1187`. The decimal is restored — with a strict guard, because a transponder code has exactly
the same shape: the fix only applies when the preceding word announces a frequency *and* the
first 3 digits fall inside the 118–137 MHz band.

```
contact departure on 1187  →  118.7       ✔
monitor 121500             →  121.500     ✔
squawk 1200                →  unchanged   ✔
```

---

## 4. Frequency handoffs

> **Symptom:** a single call after takeoff, then nothing for the rest of the flight.

Two independent causes:

1. **The Center handoff was deliberately silent.** Lacking a reliable frequency, the code
   simply skipped the call — and Center is the link *right after* Departure, so the chain
   died there.
2. **Approach never triggered without a SimBrief plan.** Distance to destination stayed
   infinite, so Approach / Tower / arrival Ground never came.

Fixes:

- With no usable Center frequency, the step is **cleanly skipped**: Departure hands off
  straight to Approach. Every call carries a **real frequency** — no more "contact center"
  without a number.
- With no flight plan, distance is measured to the **nearest controlled airport**.
- **Center frequencies are finally found.** Two stacked bugs: the recognised label was
  `CTR` (23 rows) while the data uses `CNTR` (**1,211 rows**) and `ACC` (157); and above all
  Center is a **sector** service that no major airport publishes — the lookup became
  **geographic** (250 km radius).

  Measured: Dallas 127.450 · Denver 133.950 · Atlanta 133.800 · Toronto 124.025 ·
  Anchorage 132.300.

- **Starting mid-flight**: the sequence restarted from "departure Tower" and waited for a
  takeoff that would never come. It now infers the correct position from the aircraft's real
  state and **announces the frequency you should be on**.
- **No more false "you took off without clearance"** when starting in flight: the *unknown*
  phase was counted as "on the ground". No opening call fires on the first observation any
  more. A genuine takeoff without clearance is still reported.
- **No more handoff to a small airfield**: the dataset holds 42,685 small fields against
  1,173 large ones, and plenty of small ones have a tower. Only *large* and *medium* airports
  are kept now, with a weighting that prefers a large airport slightly further away over a
  medium field right next door.

  Verified: London → EGLL · Brussels → EBBR · Paris → LFPB · Amsterdam → EHAM ·
  Los Angeles → KLAX · Hurghada → HEGN · Liverpool → EGGP.

---

## 5. Departure clearance

> **Symptom:** departing HRG, but the clearance announced the arrival STAR `ELVO1L` and
> runway 27 at the destination.

Two bugs, the main one cruder than expected:

1. **The runway was a hard-coded constant.** `{runway}` was literally "two seven" and never
   read anything from the flight plan — any airport pair showed 27.
2. **The SID trusted the navlog flag alone.** It now requires `is_sid_star == 1` **and** a
   position at the head of the route. A STAR mislabelled at the end of the navlog is
   rejected, and with no SID the clearance falls back to *"as filed"* — never to the arrival
   procedure.

The plan now separates departure and arrival explicitly, and the import prints a diagnostic:

```
DEPARTURE HEGN · SID=TALDA1B · runway=34R  ||  ARRIVAL EGGP · STAR=ELVO1L · runway=27

CLEARANCE : …cleared to Liverpool John Lennon via the TALDA 1 Bravo departure…
TAKEOFF   : …runway 3 4 right, cleared for takeoff.
APPROACH  : …expect ILS approach, runway 2 7.        ← ELVO / 27 only here
```

Runways are spoken correctly: `34R` → *"runway three four right"*. Without a flight plan,
ATC says *"the active runway"* rather than inventing a number.

The "OFP file" path extracted **no** SID/STAR at all: fixed too.

---

## 6. Flight phases

**No request is refused because of the flight phase any more.** The phase is *estimated* from
simulator data: when the estimate was wrong, the controller answered "unable at this time" to
a perfectly valid request, with no way around it.

- `allowedPhases` and the `wrongPhase` refusal are removed.
- The Test Mode phase selector is gone: it only existed to work around those refusals.
- Two guards remain: **on the ground** and **right controller**.

Phases still drive **triggers** (copilot V1/rotation/minimums callouts, approach and landing
calls, ambient traffic consistent with the frequency) — never prohibitions.

---

## 7. Interface

- **Logo**: `logo.png` replaces the header pictogram, serves as the icon for all three
  windows, and a multi-size icon is derived from it for the executable and the taskbar.
- **Cabin sound packs: on hold.** The card shows a **COMING SOON** badge, the settings are
  greyed out, and no cabin sound is played — even if an old config file enabled it. The code
  stays in place: a single constant will be enough to bring it back.
- Help texts rewritten (language, voices, microphone) to reflect how things actually work.

---

## 8. Diagnostics

**New: a log file on every launch**, in `%LOCALAPPDATA%\WilcoATC\logs\`. The last 10
startups are kept. An *Open logs folder* button is available in Settings ▸ Getting started.

It opens **before the application's entry point** and writes unbuffered: a process that dies
abruptly still leaves its last lines behind.

```
WilcoATC 1.0.0.0   started 2026-07-21 17:15:45
Windows      : Microsoft Windows NT 10.0.26100.0
.NET         : 8.0.29     Process: 64-bit
Dependencies expected next to the executable:
    [!!] SimConnect native    SimConnect.dll     MISSING
    [OK] SimConnect managed   …                  160 KB
    [OK] sherpa-onnx …        [OK] onnxruntime …
```

It records: which dependencies are present or missing, DLL load failures, the simulator
connection state, and any unhandled exception (UI, background tasks, fatal).

### A crash fixed along the way

While testing the log — with the DLL deliberately deleted — the application **died outright,
with no window**. Exactly the "I launch it, nothing happens" symptom.

The cause is nasty: the SimConnect connector is a **mixed** assembly. When its native DLL is
missing, **any method that merely mentions the type fails to JIT**. The knock-on: the `catch`
worked, but the `finally` rethrew — and an exception thrown from a `finally` escapes every
`catch` and kills the process.

Fixed. Verified: without the DLL, the window opens, the application stays up, and the log
names the missing file with the steps to fix it.

---

## Known limitations

What is **not** solved, in full transparency:

- **Center frequencies: North American coverage only.** The public data holds almost none for
  Europe. Elsewhere, the Center step is skipped (see §4). Workarounds: enter the Center name
  and frequency in Settings ▸ Handoffs, or enable VATSIM.
- **Unknown runway without a flight plan.** The bundled data contains airports and
  frequencies, not runways. ATC then says "the active runway".
- **The STT test bench uses synthetic speech**, clean and noise-free. The ranking between
  models is reliable, but a headset mic in a real cockpit stays harder for any engine.
- **Cabin sound packs disabled** (see §7).
- **Single-file executable is impossible**: the SimConnect connector is a mixed assembly,
  which .NET cannot embed. Distribution stays a self-contained **folder** (~202 MB) that
  needs no .NET installation.

---

## Technical notes

- ATC rules table at **version 9** (the user file is regenerated automatically).
- New components: `AtcTextNormalizer`, `RunwayFormatter`, `SpeechModelRepository`,
  `FileLog`.
- `WhisperModelRepository` is replaced by `SpeechModelRepository`, which handles both Whisper
  and NeMo transducers.
- Legacy `AtcLanguage` setting removed from preferences; `AppLanguage` now drives the
  interface only.
- Verification: a quality harness covering full English, pilot comprehension, numbers,
  handoffs, clearance, airport selection and parity across the six dictionaries — plus an
  **end-to-end** test that synthesises speech, runs it through the application's real path and
  checks the resulting intent.
