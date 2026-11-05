# Livestock Welfare (beta 0.13.0)

A Vintage Story mod that deepens animal husbandry: your livestock age, get dirty and sick, need shelter from the weather and water to drink. Keep them clean, groomed, sheltered and watered, and they'll thrive. Neglect them and they suffer.

**Game version:** 1.22.x · **Side:** client and server (install on both) · **Animals:** sheep, goats, pigs and chickens, adults and young

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
- Sick animals are **listless** and lie down often. Very sick ones **lie down** and don't get up until they recover or die.
- In clean surroundings sick animals can **recover**, and then they're immune for a few days.
- Sick animals lose health every day in proportion to their size, so a hen holds out as long as a sheep. The info text says why an animal fell sick: dirt, bad weather or another animal.
- Animals that die of disease leave a carcass with reduced drops.

### Grooming and visible dirt
- **Groom** an animal with **dry grass**: sneak and hold right click for about 2 seconds. Each grooming removes 25% of the dirt. Bits of mud fall off, and every second grooming uses up one dry grass.
- Or make a **grooming brush** (stick and flax fibers, with a knife): no sneaking needed, it cleans twice as well and faster, and wears out instead of being used up. It hangs on the tool rack.
- Dirty animals **look dirty**: mud spots appear on the coat in 3 stages (generated for any texture size).
- **Flies** buzz around very dirty animals (flies mean dirt, not sickness).
- Animals **lie down** while you groom them and get up once they're clean.
- Chickens can't be groomed: they take **dust baths** instead. They walk to sand within 10 blocks, sit down in a cloud of sand and get up clean. Sand under the open sky is mud while it rains; under a roof it always works. Without dry sand nearby they get scruffy. Lambs, kids, piglets and chicks never get dirty.
- Hitting livestock with a **shovel** doesn't hurt them. It shoves them aside, so you can clean the muck they're standing on.

### Weather and shelter
- **Rain** and **snow** stress animals that have no roof over them.
- **Cold** below the species' limit stresses animals that aren't in a closed barn (same room rules as for players). Sheep handle the cold best (-10 °C), pigs the worst (+2 °C). A roof alone halves the cold.
- **Strong sun** stresses animals only when it's really hot (26–32 °C depending on species). In the hot sun they **lose their appetite** (they skip the trough), and after a while they get **listless** and lie down now and then. Give them shade.
- At night, **kept animals** with no roof over them have **no safe place to sleep**, and that stresses them too. Wild animals don't mind.
- Stress makes animals **sick more easily** and stops breeding above 50%. Above 80% they stop healing and slowly lose health.
- In bad weather, animals in the open **walk to the nearest roofed spot** (within 24 blocks) and stay there while it lasts. When they get hungry or thirsty enough they go out to eat or drink, then come back.
- At **night** they also go under the roof and **lie down to sleep** until morning.
- **Pet** an animal (right click with an empty hand) to calm it down: -20% stress, once every 2 in-game hours.

**If animals don't go under the roof:** wild animals (caught or placed, not born in your pen) flee from you when you are close, and fleeing wins, so watch from a distance. The roof must be within reach (24 blocks by default), with a walkable way in (no closed gate, fence or high step), two free blocks of height and a roof over the neighbouring blocks too. Animals on a lead or tied to a post follow the rope instead.

### Thirst and water
- Animals get **thirsty** over time, faster above 20 °C and even faster in the hot sun.
- Thirsty animals **walk to water and drink there**, just like they walk to a trough to eat: head down, splashes and a drinking sound. They look for water within 16 blocks.
- Drinking from **fresh water** (ponds, rivers, puddles) is free, and animals prefer it: they walk a few blocks further for it before using a barrel or waterer.
- The clay **waterer** (shaped on the clay forming grid, fired in a pit kiln) holds 10 litres. Fill it with a bucket; chickens reach it easily and sip from the bowl.
- From **barrels** and placed **buckets** of water the water is really **used up**: about 8 litres a day for 10 sheep. Refill them!
- If an animal can't reach the water (e.g. a barrel just outside the fence) it still drinks from up to 6 blocks away once it gets very thirsty.
- Hot spring water and salt water are not drinkable.
- **Hydrate or Diedrate**: animals drink its clean rainwater, well water, distilled and boiled water (from barrels too), but not its salt, muddy, tainted or poisoned water.
- Very thirsty animals don't breed. Parched ones stop healing and lose health.

### Babies and orphans
- Lambs, kids and piglets **suckle** from their mother, so her condition matters: a thirsty, sick or stressed mother has less milk, and a sick mother can pass her illness on.
- Poorly fed babies grow more slowly; starving ones stop healing and can die of malnutrition.
- Babies feel the cold and the heat sooner than adults, and keep their age and health when they grow up.
- If the mother dies the baby becomes an **orphan** and doesn't know how to eat from a trough yet. **Feed it by hand**: sneak and hold right click with milk (bucket or jug) or food it eats. It takes the food from your hand and chews it.
- After three feedings by hand (or a couple of days with its mother) it learns to eat from a trough.

### Chickens
- Hold grain and your hens **follow you** instead of running away (they still flee from predators). Sneak and hold right click to **feed one from your hand**: it counts as a meal from the trough, then she is full for an hour.
- The **chicken coop kit** (planks and two hen boxes) builds a small raised coop where you right-click the ground: four posts, a pitched roof, a ramp up to the pop hole, two hen boxes inside, and steps up to a gate at the back so you can walk in for the eggs. It needs a clear spot of 5 by 9 blocks, 6 high.
- Hens can't hop up onto waterers and troughs.

### Kept and wild animals
- **Kept animals** are the ones you placed or that were born in your care. Only they leave muck, need a safe place to sleep, and can die of thirst, bad weather or disease.
- Wild animals still get thirsty, stressed and sick, but they don't die of it, so the wild doesn't die off around you.

### Information
- The animal's info shows its age, filth, shelter and stress, thirst, and for babies how well fed they are.
- **Sneak + right click** with an empty hand prints a full status report in chat, including where the animal drinks from.
- English and Hungarian translations.

## Safety options for servers and casual play
Many players asked for a way to play without losing animals:
- **Turn off death** completely (`AnimalsCanDie`), or per cause (`OldAgeCanKill`, `DiseaseCanKill`, `ExposureCanKill`, `ThirstCanKill`, `MalnutritionCanKill`). Animals then still get old, sick and weak, but stay at 1 health instead of dying.
- **Pause while nobody is around** (`PauseWhenNoPlayerNearby`): no aging, dirt, sickness, stress or thirst for animals with no player within 64 blocks. Useful on servers where owners aren't always online.

## Configuration
Everything is in `ModConfig/livestockwelfare.json` (created on first start): lifespans, dirt and disease rates, muck, grooming, dust baths, species cold and heat limits, shelter, thirst and water amounts, babies and hand feeding, treats for hens, and the safety options. Mod managers that edit config files (like eViSTool) can change it too. Each system can be switched off on its own.

## Compatibility
- Works next to Natural Fertilizer, Feed or Fallow, Better Husbandry and Hydrate or Diedrate. Petting with an empty hand also triggers Better Husbandry's petting.
- Animals whose textures are changed by **genelib** (e.g. Detailed Animals) may not show the mud spots. Flies and everything else still work.
- Other mods' animals are not covered yet.

## Known limitations (beta)
- Only vanilla sheep, goats, pigs and chickens are covered.
- Balance needs testing in long-running worlds.
- Not tested yet on multiplayer servers, and not fully in existing world saves (added to a world that was already running). Back up your world first.

## How this mod was made
Livestock Welfare is designed and tested by ReginArtDesign and written with the help of an AI assistant (Claude by Anthropic). I'm not a programmer: I came up with the ideas, decided how the animals should behave, and tested every feature in game. The AI wrote the code, looked up how the base game works (decompiling the game's own code to follow its rules, e.g. for rooms, rain and animal AI) and ran automated tests on a headless server. Ideas and feedback from the Vintage Story community shaped many features, like the safety options for servers.

## Support
Livestock Welfare is free and always will be. If you'd like to say thanks, you can [buy me a coffee on Ko-fi](https://ko-fi.com/reginart). It's completely optional: ideas, feedback and bug reports help just as much.

## License
MIT: free to use, change and build on (for example compatibility patches), as long as the copyright notice is kept. See [LICENSE](LICENSE).
