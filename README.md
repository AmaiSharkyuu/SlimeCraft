# SlimeCraft : Minecraft x Slime Rancher

Un mod pour **Slime Rancher** (le premier) qui y reconstruit Minecraft : on joue Steve dans le Far, Far Range, avec la
physique, la main, le HUD, l'inventaire, les blocs et les sons de Minecraft, éclairés par le soleil et la nuit de
Slime Rancher. Les slimes se rangent dans l'inventaire au clic droit et se tiennent en main en 3D.

Un seul jeu tourne : pas de Minecraft lancé à côté. Les textures, sons et polices de Minecraft sont extraits **de ta
propre copie de Minecraft 1.20.1** au moment de l'installation ; ce dépôt ne contient aucun fichier de Mojang ni de
Monomi Park.

Par Amai.

![HUD Minecraft dans Slime Rancher](docs/hud.png)

## Ce qu'il y a dedans

- **Physique de Minecraft** : marche, sprint, accroupi qui ne tombe pas des bords, saut d'un bloc, dégâts de chute,
  nage. Le jetpack est retiré : on monte en empilant des blocs.
- **Main de Steve** en vue subjective avec les animations de Minecraft (balancement, coup, changement d'objet), vue
  à la 3e personne avec F5.
- **Slimes, plorts, fruits, légumes, poules** : clic droit pour les ranger dans l'inventaire, clic droit avec un slime
  en main pour le reposer, nourrir un slime en le visant avec de la nourriture, lancer un objet avec A/Q (les plorts
  lancés dans le marché se vendent).
- **565 blocs de Minecraft** : cubes, bûches, dalles, escaliers, barrières, tapis, fleurs, torches (qui éclairent),
  coffres, tonneaux et boîtes de Shulker (qui gardent leur contenu, slimes compris).
- **Survie et Créatif** (F3 + F4) : en Survie une palette gratuite de blocs simples ; en Créatif tous les blocs, un
  onglet avec tous les slimes, largos et objets de Slime Rancher, la casse instantanée et le vol (double espace).
- **Stockage de Slime** : un onglet de l'inventaire pour garder ses slimes au chaud.
- HUD : cœurs = vie, cuisses = énergie, barre verte = avancée de la journée, chiffre vert = Newbucks.

![Inventaire créatif](docs/inventaire.png) ![Coffre](docs/coffre.png)

## Installation

Il faut **Slime Rancher** (Steam, version PC) et **Minecraft Java 1.20.1** installé avec le launcher officiel.

1. Télécharge le zip de la dernière version dans [Releases](../../releases).
2. Ouvre le dossier de Slime Rancher (Steam : clic droit sur le jeu, Gérer, Parcourir les fichiers locaux) et
   décompresse le zip dedans, à côté de `SlimeRancher.exe`. Le zip contient BepInEx 5, le chargeur de mods.
3. Si tu n'as jamais lancé Minecraft **1.20.1** : dans le launcher Minecraft, crée une installation 1.20.1, lance-la
   une fois jusqu'au menu, puis ferme-la.
4. Lance `BepInEx\plugins\SlimeCraft\SlimeCraft-Assets.exe` : il copie les textures et les sons depuis ton Minecraft.
5. Lance Slime Rancher et charge une partie.

Conseil : fais une copie de tes sauvegardes avant (`%USERPROFILE%\AppData\LocalLow\Monomi Park\Slime Rancher`).

## Commandes

| Touche | Action |
|---|---|
| ZQSD (AZERTY) / WASD (QWERTY), espace | se déplacer, sauter |
| Ctrl ou double avant | sprinter |
| Maj | s'accroupir |
| Clic droit sur un slime, plort, fruit, légume, poule | le ranger dans l'inventaire |
| Clic droit avec un slime en main | le reposer là où tu vises |
| Clic droit sur un slime avec de la nourriture | le nourrir (Maj + clic droit : l'attraper quand même) |
| Clic droit dans le vide avec un fruit ou un légume | le manger (rend de l'énergie) |
| Clic droit sur un coffre, tonneau, boîte de Shulker | l'ouvrir |
| Clic droit sur un bouton, une porte, un écran de Slime Rancher | l'utiliser (remplace E) |
| Clic gauche | casser un bloc, repousser un slime (3 coups font éclater un Tarr) |
| Clic molette | prendre le bloc visé |
| A (AZERTY) / Q (QWERTY) | lancer l'objet tenu (Ctrl : toute la pile) |
| E | inventaire |
| F3 + F4 | passer en Survie / Créatif |
| F5 | vue à la 3e personne / de face |
| 1 à 9, molette | hotbar |

## Compiler le mod soi-même

- .NET SDK 8, Python 3 avec Pillow, Slime Rancher et BepInEx 5.4 installés.
- `python tools/extract_assets.py` remplit `plugin/assets` depuis ton Minecraft 1.20.1.
- `dotnet build -c Release` dans `plugin/` (le dossier du jeu se règle avec `-p:GameDir=...`) copie la DLL et les
  assets dans `BepInEx/plugins/SlimeCraft`.
- L'outil d'extraction en .exe : `python -m PyInstaller --onefile --name SlimeCraft-Assets --paths tools tools/extract_assets.py`.

## Mentions

Minecraft appartient à Mojang Studios, Slime Rancher à Monomi Park. Projet de fan non officiel, sans lien avec eux.
BepInEx est distribué sous licence LGPL 2.1 ([github.com/BepInEx/BepInEx](https://github.com/BepInEx/BepInEx)).
Le code de SlimeCraft est sous licence MIT.
