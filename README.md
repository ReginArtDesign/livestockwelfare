# Livestock Welfare (beta 0.9.0)

A Vintage Story mod that deepens animal husbandry: your livestock age, get dirty and sick, need shelter from the weather and water to drink. Keep them clean, groomed, sheltered and watered, and they'll thrive. Neglect them and they suffer.

**Game version:** 1.22.x · **Side:** client and server (install on both) · **Animals:** sheep, goats, pigs and chickens (adults)

> This is a beta. Balance values are first guesses and everything can be changed in the config file. Feedback is very welcome!

## Features

### Aging and old age
- Every animal has an age and its own lifespan: chickens 2–3 years, pigs 3–4, sheep 3–5, goats 4–6 (in-game years).
- Lambs and chicks start at age 0 when they grow up. Wild and existing animals get a random adult age.
- From 75% of its lifespan an animal is **old** and can no longer breed.
- At the end of its lifespan it **dies of old age**. The carcass then gives only **40% of the usual drops**, so it pays to cull old animals in time.

### Dirt, muck and disease
- Animals get **filthy** from crowding (more than 6 others within 4 blocks), from **muck** and from **rot** lying around.
- Kept animals slowly leave **muck** on the ground. It builds up in 4 layers, faster in crowded pens. Shovel it up and you get **rot** for composting. Wild animals never leave muck.
- Optional support for **Natural Fertilizer**: its droppings count as dirt too.
- Filthy animals can get **sick**: they don't breed or heal, lose health every day and can **infect others** of their species.
- Very sick animals **lie down** and don't get up until they recover or die.
- In clean surroundings sick animals can **recover**, and then they're immune for a few days.
- Animals that die of disease leave a carcass with reduced drops.

### Grooming and visible dirt
- **Groom** an animal with **dry grass**: sneak and hold right click for about 2 seconds. Each grooming removes 25% of the dirt. Bits of mud fall off, and every second grooming uses up one dry grass.
- Dirty animals **look dirty**: mud spots appear on the coat in 3 stages (generated for any texture size).
- **Flies** buzz around very dirty or sick animals.
- Hitting livestock with a **shovel** doesn't hurt them. It shoves them aside, so you can clean the muck they're standing on.

### Weather and shelter
- **Rain** and **snow** stress animals that have no roof over them.
- **Cold** below the species' limit stresses animals that aren't in a closed barn (same room rules as for players). Sheep handle the cold best (-10 °C), pigs the worst (+2 °C). A roof alone halves the cold.
- **Strong sun** stresses animals only when it's really hot (26–32 °C depending on species).
- Weather stress makes animals **sick more easily** and stops breeding above 50%. Above 80% they stop healing and slowly lose health.
- **Pet** an animal (right click with an empty hand) to calm it down: -20% stress, once every 2 in-game hours.

### Thirst and water
- Animals get **thirsty** over time, faster above 20 °C and even faster in the hot sun.
- They drink for free from **fresh water** (ponds, rivers) within 6 blocks.
- From **barrels** and placed **buckets** of water the water is really **used up**: about 8 litres a day for 10 sheep. Refill them!
- Hot spring water and salt water are not drinkable.
- Very thirsty animals don't breed. Parched ones stop healing and lose health.

### Information
- The animal's info shows its age, filth, shelter and weather stress, and thirst.
- **Sneak + right click** with an empty hand prints a full status report in chat, including where the animal drinks from.
- English and Hungarian translations.

## Safety options for servers and casual play
Many players asked for a way to play without losing animals:
- **Turn off death** completely (`AnimalsCanDie`), or per cause (`OldAgeCanKill`, `DiseaseCanKill`, `ExposureCanKill`, `ThirstCanKill`). Animals then still get old, sick and weak, but stay at 1 health instead of dying.
- **Pause while nobody is around** (`PauseWhenNoPlayerNearby`): no aging, dirt, sickness, weather stress or thirst for animals with no player within 64 blocks. Useful on servers where owners aren't always online.

## Configuration
Everything is in `ModConfig/livestockwelfare.json` (created on first start): lifespans, dirt and disease rates, muck, grooming, species cold and heat limits, thirst and water amounts, and the safety options. Each system can be switched off on its own.

## Compatibility
- Works next to Natural Fertilizer, Feed or Fallow and Better Husbandry. Petting with an empty hand also triggers Better Husbandry's petting.
- Animals whose textures are changed by **genelib** (e.g. Detailed Animals) may not show the mud spots. Flies and everything else still work.
- Other mods' animals are not covered yet.

## Known limitations (beta)
- Animals don't walk to water or shelter on their own yet. They drink from water within 6 blocks and are protected wherever they happen to stand.
- Only vanilla sheep, goats, pigs and chickens are covered.
- Balance needs testing in long-running worlds.
