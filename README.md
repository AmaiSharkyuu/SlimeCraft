# SlimeCraft : Minecraft x Slime Rancher

[Français](#français) | [English](#english)

## Français

Un mod pour **Slime Rancher** (le premier) qui y reconstruit Minecraft : on joue Steve dans le Far, Far Range, avec la
physique, la main, le HUD, l'inventaire, les blocs, le craft, la redstone et les sons de Minecraft, éclairés par le
soleil et la nuit de Slime Rancher. Les slimes se rangent dans l'inventaire au clic droit, se tiennent en main en 3D,
et se mangent comme un steak.

Un seul jeu tourne : pas de Minecraft lancé à côté. Les textures, sons et polices de Minecraft sont extraits **de ta
propre copie de Minecraft 1.20.1** au moment de l'installation ; ce dépôt ne contient aucun fichier de Mojang ni de
Monomi Park.

Par Amai.

![HUD Minecraft dans Slime Rancher](docs/hud.png)

### Ce qu'il y a dedans

- **Physique de Minecraft** : marche, sprint, accroupi qui ne tombe pas des bords, saut d'un bloc, dégâts de chute,
  nage. Le jetpack est retiré : on monte en empilant des blocs.
- **Casser la map de Slime Rancher** : le sol, les rochers et le décor se creusent bloc par bloc, comme dans le mod
  Mario 64 x Minecraft. Le trou est découpé dans le vrai décor, ses parois deviennent des faces de blocs Minecraft,
  et en dessous on trouve terre, pierre, minerais puis deepslate. La TNT et les Boom slimes font des cratères.
- **Survie complète** : cœurs, faim, expérience, effets de potion, sommeil dans un lit (qui avance l'horloge de
  Slime Rancher), mort qui fait tomber l'inventaire.
- **Craft** : les 1174 recettes de Minecraft 1.20.1, établi, fours, alambic, table d'enchantement, enclume, meule,
  table de forgeron, tailleur de pierre. Les plorts et la nourriture de Slime Rancher servent d'ingrédients : plort
  Rad = redstone, plort Boom = poudre à canon, plort Phosphor = poudre de glowstone, plort Gold = lingot d'or,
  plort Quantum = perle de l'Ender, fruits = pommes et baies, légumes = carottes, betteraves, pommes de terre...
- **Redstone** : poudre, torches, répéteurs, comparateurs, pistons et pistons collants, observateurs, leviers,
  boutons, plaques de pression (que les slimes déclenchent aussi), lampes, portes, trappes, blocs musicaux, TNT,
  capteur de lumière réglé sur l'horloge de Slime Rancher, et distributeurs qui **tirent des slimes**.
- **Manger les slimes** : avec un slime en main, maintenir le clic droit le mange comme de la nourriture de
  Minecraft, et chaque slime donne son effet. Boom explose, Quantum téléporte, Dervish envoie en l'air, Gold fait
  l'effet d'une pomme dorée, Rad empoisonne, Tarr flétrit, Lucky donne de l'expérience...
- **Slimes, plorts, fruits, légumes, poules** : clic droit pour les ranger dans l'inventaire, clic droit rapide avec
  un slime en main pour le reposer, nourrir un slime en le visant avec de la nourriture, lancer un objet avec A/Q
  (les plorts lancés dans le marché se vendent).
- **1003 blocs et 1255 objets de Minecraft**, avec coffres, tonneaux et boîtes de Shulker qui gardent leur contenu,
  slimes compris, et le **Stockage de Slime** : un onglet de l'inventaire pour garder ses slimes au chaud.
- **Mobs aquatiques de Minecraft** dans l'eau de Slime Rancher : morues, saumons, poissons tropicaux, poissons-globes,
  calmars, dauphins, gardiens, noyés. Et la pêche.
- **Survie et Créatif** (F3 + F4) : en Créatif tous les blocs, un onglet avec tous les slimes, largos et objets de
  Slime Rancher, la casse instantanée et le vol (double espace).
- **Chat et commandes** (T ou /) : `/give`, `/gamemode`, `/time`, `/tp`, `/summon`, `/setblock`, `/fill`,
  `/effect`, `/enchant`, `/xp`, `/gamerule`, `/skin`...
- **Skin** : `/skin steve` pour jouer Steve, `/skin amai` pour le skin d'Amai quand il est installé.
- Le mod suit la langue choisie dans les options de Slime Rancher. Les sauvegardes de SlimeCraft 0.1 sont reprises.

![Inventaire créatif](docs/inventaire.png) ![Coffre](docs/coffre.png)

### Installation

Il faut **Slime Rancher** (Steam, version PC) et **Minecraft Java 1.20.1** installé avec le launcher officiel.

1. Télécharge le zip de la dernière version dans [Releases](../../releases).
2. Ouvre le dossier de Slime Rancher (Steam : clic droit sur le jeu, Gérer, Parcourir les fichiers locaux) et
   décompresse le zip dedans, à côté de `SlimeRancher.exe`. Le zip contient BepInEx 5, le chargeur de mods.
3. Si tu n'as jamais lancé Minecraft **1.20.1** : dans le launcher Minecraft, crée une installation 1.20.1, lance-la
   une fois jusqu'au menu, puis ferme-la.
4. Lance `BepInEx\plugins\SlimeCraft\SlimeCraft-Assets.exe` : il copie les textures et les sons depuis ton Minecraft.
5. Lance Slime Rancher et charge une partie.

Si tu avais la 0.1 : décompresse la v2 par-dessus (tes sauvegardes SlimeCraft dans `saves` sont gardées et reprises),
puis relance `SlimeCraft-Assets.exe`.

Conseil : fais une copie de tes sauvegardes avant (`%USERPROFILE%\AppData\LocalLow\Monomi Park\Slime Rancher`).

### Commandes

| Touche | Action |
|---|---|
| ZQSD (AZERTY) / WASD (QWERTY), espace | se déplacer, sauter |
| Ctrl ou double avant | sprinter |
| Maj | s'accroupir |
| Clic gauche | casser un bloc, creuser le décor, frapper (3 coups font éclater un Tarr) |
| Clic droit sur un slime, plort, fruit, légume, poule | le ranger dans l'inventaire |
| Clic droit maintenu avec un slime, un fruit ou un légume | le manger |
| Clic droit rapide avec un slime en main | le reposer là où tu vises |
| Clic droit sur un slime avec de la nourriture | le nourrir (Maj + clic droit : l'attraper quand même) |
| Clic droit sur un coffre, un four, un levier, une porte... | l'utiliser |
| Clic droit sur un bouton, une porte, un écran de Slime Rancher | l'utiliser (remplace E) |
| Clic molette | prendre le bloc visé |
| A (AZERTY) / Q (QWERTY) | lancer l'objet tenu (Ctrl : toute la pile) |
| G | échanger les deux mains |
| E | inventaire |
| T, / | chat, commande |
| F3 | écran de debug |
| F3 + F4 | passer en Survie / Créatif |
| F5 | vue à la 3e personne / de face |
| 1 à 9, molette | hotbar |

### Compiler le mod soi-même

- .NET SDK 8, Python 3 avec Pillow, Slime Rancher et BepInEx 5.4 installés.
- `python tools/extract_mc.py` remplit `plugin/assets` depuis ton Minecraft 1.20.1 (avec `data/`, les données tirées
  du code de Minecraft : états des blocs, recettes, tags, modèles des mobs).
- `dotnet build -c Release` dans `plugin/` (le dossier du jeu se règle avec `-p:GameDir=...`) copie la DLL,
  Newtonsoft.Json et les assets dans `BepInEx/plugins/SlimeCraft`.
- L'outil d'extraction en .exe : `python -m PyInstaller --onefile --name SlimeCraft-Assets --add-data "data;data" tools/extract_mc.py`.

### Mentions

Minecraft appartient à Mojang Studios, Slime Rancher à Monomi Park. Projet de fan non officiel, sans lien avec eux.
Le moteur Minecraft vient de SubnautiCraft, l'autre fusion d'Amai. Code écrit avec l'aide de Claude (Anthropic).
BepInEx est distribué sous licence LGPL 2.1 ([github.com/BepInEx/BepInEx](https://github.com/BepInEx/BepInEx)),
Newtonsoft.Json sous licence MIT. Le code de SlimeCraft est sous licence MIT.


## English

A mod for **Slime Rancher** (the first one) that rebuilds Minecraft inside it: you play Steve in the Far, Far Range,
with Minecraft's physics, hand, HUD, inventory, blocks, crafting, redstone and sounds, lit by Slime Rancher's sun and
nights. Right click a slime to put it in your inventory, hold it in your hand in 3D, and eat it like a steak.

Only one game runs: no Minecraft running alongside. Minecraft's textures, sounds and fonts are extracted **from your
own copy of Minecraft 1.20.1** during installation; this repository contains no files from Mojang or Monomi Park.

By Amai.

### Features

- **Minecraft physics**: walking, sprinting, sneaking that stops at edges, one-block jumps, fall damage, swimming.
  The jetpack is gone: you climb by stacking blocks.
- **Break Slime Rancher's map**: the ground, rocks and scenery can be dug one block at a time, like in the Mario 64 x
  Minecraft mod. The hole is cut out of the real scenery, its walls become Minecraft block faces, and below you find
  dirt, stone, ores, then deepslate. TNT and Boom slimes blow craters.
- **Full Survival**: hearts, hunger, experience, potion effects, sleeping in a bed (which moves Slime Rancher's clock
  forward), death that drops your inventory.
- **Crafting**: all 1174 recipes of Minecraft 1.20.1, crafting table, furnaces, brewing stand, enchanting table,
  anvil, grindstone, smithing table, stonecutter. Slime Rancher's plorts and food are ingredients: Rad plort =
  redstone, Boom plort = gunpowder, Phosphor plort = glowstone dust, Gold plort = gold ingot, Quantum plort = ender
  pearl, fruits = apples and berries, veggies = carrots, beetroots, potatoes...
- **Redstone**: dust, torches, repeaters, comparators, pistons and sticky pistons, observers, levers, buttons,
  pressure plates (slimes trigger them too), lamps, doors, trapdoors, note blocks, TNT, a daylight sensor that follows
  Slime Rancher's clock, and dispensers that **shoot slimes**.
- **Eat slimes**: with a slime in hand, hold right click to eat it like Minecraft food, and every slime has its own
  effect. Boom explodes, Quantum teleports you, Dervish throws you in the air, Gold works like a golden apple, Rad
  poisons you, Tarr withers you, Lucky gives experience...
- **Slimes, plorts, fruits, veggies, chickens**: right click to put them in your inventory, quick right click with a
  slime in hand to put it down, feed a slime by aiming at it with food, throw an item with A/Q (plorts thrown into the
  market are sold).
- **1003 Minecraft blocks and 1255 items**, with chests, barrels and shulker boxes that keep their contents, slimes
  included, and the **Slime Storage**: an inventory tab to keep your slimes safe and warm.
- **Minecraft's water mobs** in Slime Rancher's water: cod, salmon, tropical fish, pufferfish, squid, dolphins,
  guardians, drowned. And fishing.
- **Survival and Creative** (F3 + F4): Creative has every block, a tab with every slime, largo and item from Slime
  Rancher, instant breaking and flying (double tap space).
- **Chat and commands** (T or /): `/give`, `/gamemode`, `/time`, `/tp`, `/summon`, `/setblock`, `/fill`, `/effect`,
  `/enchant`, `/xp`, `/gamerule`, `/skin`...
- **Skin**: `/skin steve` to play Steve, `/skin amai` for Amai's skin when it is installed.
- The mod follows the language set in Slime Rancher's options. SlimeCraft 0.1 saves are carried over.

### Installation

You need **Slime Rancher** (Steam, PC version) and **Minecraft Java 1.20.1** installed with the official launcher.

1. Download the zip of the latest version from [Releases](../../releases).
2. Open the Slime Rancher folder (Steam: right click the game, Manage, Browse local files) and unzip the zip there,
   next to `SlimeRancher.exe`. The zip includes BepInEx 5, the mod loader.
3. If you have never played Minecraft **1.20.1**: in the Minecraft launcher, create a 1.20.1 installation, play it
   once up to the title screen, then close it.
4. Run `BepInEx\plugins\SlimeCraft\SlimeCraft-Assets.exe`: it copies the textures and sounds from your Minecraft.
5. Start Slime Rancher and load a save.

Coming from 0.1: unzip v2 over it (your SlimeCraft saves in `saves` are kept and carried over), then run
`SlimeCraft-Assets.exe` again.

Tip: back up your saves first (`%USERPROFILE%\AppData\LocalLow\Monomi Park\Slime Rancher`).

### Controls

| Key | Action |
|---|---|
| WASD (QWERTY) / ZQSD (AZERTY), space | move, jump |
| Ctrl or double tap forward | sprint |
| Shift | sneak |
| Left click | break a block, dig the scenery, hit (3 hits pop a Tarr) |
| Right click a slime, plort, fruit, veggie, chicken | put it in your inventory |
| Hold right click with a slime, fruit or veggie | eat it |
| Quick right click with a slime in hand | put it down where you aim |
| Right click a slime while holding food | feed it (Shift + right click: pick it up anyway) |
| Right click a chest, furnace, lever, door... | use it |
| Right click a Slime Rancher button, door, screen | use it (replaces E) |
| Middle click | pick the block you aim at |
| Q (QWERTY) / A (AZERTY) | throw the held item (Ctrl: the whole stack) |
| G | swap hands |
| E | inventory |
| T, / | chat, command |
| F3 | debug screen |
| F3 + F4 | switch between Survival and Creative |
| F5 | third person / front view |
| 1 to 9, mouse wheel | hotbar |

### Building the mod yourself

- .NET SDK 8, Python 3 with Pillow, Slime Rancher and BepInEx 5.4 installed.
- `python tools/extract_mc.py` fills `plugin/assets` from your Minecraft 1.20.1 (with `data/`, the data read from
  Minecraft's own code: block states, recipes, tags, mob models).
- `dotnet build -c Release` in `plugin/` (set the game folder with `-p:GameDir=...`) copies the DLL, Newtonsoft.Json
  and the assets into `BepInEx/plugins/SlimeCraft`.
- The extraction tool as an .exe: `python -m PyInstaller --onefile --name SlimeCraft-Assets --add-data "data;data" tools/extract_mc.py`.

### Credits

Minecraft belongs to Mojang Studios, Slime Rancher to Monomi Park. Unofficial fan project, not affiliated with them.
The Minecraft engine comes from SubnautiCraft, Amai's other fusion. Code written with the help of Claude (Anthropic).
BepInEx is distributed under the LGPL 2.1 license ([github.com/BepInEx/BepInEx](https://github.com/BepInEx/BepInEx)),
Newtonsoft.Json under the MIT license. SlimeCraft's code is under the MIT license.
