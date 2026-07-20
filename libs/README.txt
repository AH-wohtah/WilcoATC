DLL SimConnect — dossier libs/
==============================

Ce dossier contient les deux DLL nécessaires à SimConnect :

  1. Microsoft.FlightSimulator.SimConnect.dll   (wrapper managé .NET, référencé par le projet)
  2. SimConnect.dll                              (bibliothèque NATIVE x64, copiée à côté de l'exe)

Ces deux fichiers sont DÉJÀ présents ici (récupérés sur cette machine), donc la
solution compile et tourne telle quelle.

--------------------------------------------------------------------------
Où les récupérer soi-même (source officielle : le SDK MSFS)
--------------------------------------------------------------------------
1. Dans MSFS (2020 ou 2024) : menu Options > General > Developers > active
   « SDK ». Puis Devmode > Help > SDK Installer, installe le "Core" SDK.
2. Le SDK s'installe par défaut dans :  C:\MSFS SDK\   (ou C:\MSFS 2024 SDK\)
3. Les DLL se trouvent dans :
       <SDK>\SimConnect SDK\lib\Microsoft.FlightSimulator.SimConnect.dll   (managé)
       <SDK>\SimConnect SDK\lib\SimConnect.dll                             (natif x64)
   Copie ces deux fichiers dans le présent dossier libs/.

--------------------------------------------------------------------------
Comment le projet les référence
--------------------------------------------------------------------------
- Le .csproj référence le wrapper managé via <Reference> + <HintPath> vers
  libs\Microsoft.FlightSimulator.SimConnect.dll  (<Private>true</Private> => copié
  dans le dossier de sortie).
- La DLL NATIVE SimConnect.dll est copiée à côté de l'exécutable via un <None>
  CopyToOutputDirectory. Elle DOIT se trouver à côté de FreqWatch.exe, sinon le
  wrapper managé lève une DllNotFoundException au moment de la connexion.
- Le projet cible x64 (Platforms/PlatformTarget) : SimConnect natif est 64 bits.
