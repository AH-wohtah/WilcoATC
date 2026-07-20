Dossier data/ — (Stretch, OPTIONNEL) résolution de station OurAirports
=====================================================================

Ce dossier est vide par défaut : la résolution de station est un bonus totalement
isolé. Sans ces fichiers, l'application fonctionne exactement pareil (elle
n'affiche simplement aucun nom de station sous les fréquences).

Pour l'activer, télécharge les deux CSV gratuits d'OurAirports et place-les ICI :

  - airports.csv               https://davidmegginson.github.io/ourairports-data/airports.csv
  - airport-frequencies.csv    https://davidmegginson.github.io/ourairports-data/airport-frequencies.csv

(Domaine public — https://ourairports.com/data/)

Au build, tout fichier *.csv présent ici est copié dans le dossier data/ à côté de
l'exécutable. Au démarrage, OurAirportsStationResolver les charge (une seule fois,
paresseusement). Il associe la fréquence ACTIVE à l'aéroport le plus proche de
l'avion partageant cette fréquence, et affiche par ex. « 118.700 — Paris CDG · TWR ».
