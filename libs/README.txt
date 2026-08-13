SimConnect DLLs — libs/ folder
==============================

This folder holds the two DLLs SimConnect needs:

  1. Microsoft.FlightSimulator.SimConnect.dll   (managed .NET wrapper, referenced by the project)
  2. SimConnect.dll                              (NATIVE x64 library, copied next to the exe)

Both files are ALREADY here (taken from this machine), so the solution builds and runs
as it stands.

--------------------------------------------------------------------------
Getting them yourself (official source: the MSFS SDK)
--------------------------------------------------------------------------
1. In MSFS (2020 or 2024): Options > General > Developers > enable "SDK".
   Then Devmode > Help > SDK Installer, and install the "Core" SDK.
2. The SDK installs by default into:  C:\MSFS SDK\   (or C:\MSFS 2024 SDK\)
3. The DLLs are in:
       <SDK>\SimConnect SDK\lib\Microsoft.FlightSimulator.SimConnect.dll   (managed)
       <SDK>\SimConnect SDK\lib\SimConnect.dll                             (native x64)
   Copy both files into this libs/ folder.

--------------------------------------------------------------------------
How the project references them
--------------------------------------------------------------------------
- The .csproj references the managed wrapper with <Reference> + <HintPath> pointing at
  libs\Microsoft.FlightSimulator.SimConnect.dll  (<Private>true</Private> => copied into
  the output folder).
- The NATIVE SimConnect.dll is copied next to the executable through a <None> with
  CopyToOutputDirectory. It MUST sit next to WilcoATC.exe, otherwise the managed wrapper
  throws DllNotFoundException when it tries to connect.
- The project targets x64 (Platforms/PlatformTarget): native SimConnect is 64-bit.
