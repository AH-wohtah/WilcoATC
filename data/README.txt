data/ folder — OurAirports station resolution (optional)
=======================================================

Station resolution is a fully isolated bonus. Without these files the application works
exactly the same; it simply shows no station name under the frequencies.

To enable it, download the two free OurAirports CSVs and drop them HERE:

  - airports.csv               https://davidmegginson.github.io/ourairports-data/airports.csv
  - airport-frequencies.csv    https://davidmegginson.github.io/ourairports-data/airport-frequencies.csv

(Public domain — https://ourairports.com/data/)

At build time, every *.csv found here is copied into the data/ folder next to the
executable. At startup, OurAirportsStationResolver loads them once, lazily. It matches the
ACTIVE frequency to the airport nearest the aircraft that publishes it, and shows for
instance "118.700 — Paris CDG · TWR".


frequencies-extra.csv — an overlay of REAL frequencies (editable)
=================================================================

Neither the simulator nor OurAirports ALWAYS has the complete list from the real AIP
(Manila/RPLL, for example: clearance 125.50, tower 118.40, ground 122.00, apron 121.55…).
This file lets you ADD those real frequencies by hand. They are merged with the
simulator's live frequencies and with OurAirports (union per channel: the same frequency
appears once, and the simulator wins).

Format — three columns, header REQUIRED on the first line:

    icao,type,mhz
    RPLL,CLR,125.500
    RPLL,TWR,118.400
    RPLL,GND,122.000

  - icao : ICAO code of the airport (RPLL, LFPG, KJFK…).
  - type : ATIS, CLR, GND, TWR, APP, DEP, CTR (recognised positions -> proper label and
           station name for the ATC). Any other text (RMP, RADIO, UNICOM…) is displayed
           as-is, with no controller type attached.
  - mhz  : frequency in megahertz (118.400). Aviation VHF band only (118–137).

The shipped file is pre-filled for RPLL (Ninoy Aquino / Manila). Add other airports below
it; the file is also meant to be shared between users.
