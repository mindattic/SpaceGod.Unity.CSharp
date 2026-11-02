# Skybox sources (U.SPC-T595)

Every panorama here is free to use and to redistribute (the repository is public), taken at the largest
resolution its source offers within Unity's 8192 texture limit. Downloads were treated as data only: images
were opened and re-encoded with Pillow (and the NASA EXR read with the OpenEXR package), nothing in them
was run. All are equirectangular (2:1) and are read by `Core/Rendering/Skyboxes.cs`
(`SkyboxCatalog.PanoramaUv`): the centre column is longitude 0, longitude rises to the left as the sky is
seen from inside, the top row is latitude +90. Each panorama's frame (ecliptic, equatorial or galactic) puts
it in the game's J2000 ecliptic axes, so the real skies show every star where it really is.

## Shipped panoramas

| File | Source | Licence and credit | Frame | Notes |
|---|---|---|---|---|
| `nasa_deep_star_map_8k.jpg` (8192 x 4096) | [NASA SVS Deep Star Maps 2020](https://svs.gsfc.nasa.gov/4851), `starmap_2020_8k.exr` | Public domain (NASA). Credit: NASA/Goddard Space Flight Center Scientific Visualization Studio; Gaia DR2: ESA/Gaia/DPAC | Equatorial (RA/Dec, J2000) | 1.7 billion stars from Hipparcos-2, Tycho-2 and Gaia DR2. The linear EXR was scaled by 2.0, clipped and sRGB-encoded (JPEG q92). The source goes to 64K; 8K is the largest Unity imports. Checked: the galactic centre falls at RA 266°, Dec -29° and the LMC at RA 80°, Dec -69°. |
| `eso_milky_way_6k.jpg` (6000 x 3000) | [ESO eso0932a, The Milky Way panorama](https://www.eso.org/public/images/eso0932a/), original TIFF | CC BY 4.0 ([ESO image use](https://www.eso.org/public/copyright/)). Credit: ESO/S. Brunier | Galactic (l, b), centre at l = 0 | 6000 x 3000 is the largest ESO publishes (the 800-megapixel original is not public). The galactic plane runs across the middle; the Magellanic Clouds sit lower right. |
| `spheremaps_blue_nebulae_8k.jpg` (8192 x 4096) | [Space Spheremaps](https://space-spheremaps.itch.io/space-spheremaps), `blue_nebulae_1.png` | Free for personal and commercial use, sharing and adapting allowed, attribution not required ("similar to CC BY 4.0"; no AI scraping). Credit: Space Spheremaps | Ecliptic (no real orientation) | PNG re-encoded as JPEG q92. |
| `spheremaps_hazy_nebulae_8k.jpg` (8192 x 4096) | Space Spheremaps, `hazy_nebulae_1.png` | As above | Ecliptic | |
| `spheremaps_plain_starfield_8k.jpg` (8192 x 4096) | Space Spheremaps, `plain_starfield_1.png` | As above | Ecliptic | |
| `spheremaps_ringed_gas_giant_8k.jpg` (8192 x 4096) | Space Spheremaps, `ringed_gas_giant_planet.png` | As above | Ecliptic | |

## Made in game

| Sky | Source | Licence | Notes |
|---|---|---|---|
| This system's own nebulae | [wwwtyro/space-3d](https://github.com/wwwtyro/space-3d) ([web tool](https://tools.wwwtyro.net/space-3d/)), ported in `Core/Rendering/ProceduralSky.cs` | Unlicense (public domain) | Space 3D's domain-warped nebula layers (colour, scale, intensity, falloff and offset per layer, one more layer at even odds), seeded by the system's name, baked on worker threads as a 2:1 panorama (Settings > Graphics > Seeded nebula detail: 512, 1024 or 2048, i.e. up to 8192 x 4096). Space 3D's point stars are the game's own starfield; its fake sun is left out, since every system has a real one. |
| Bare starfield | `Runtime/Views/Starfield.cs` | Part of the game | The old sky: point stars on black. |

## Found but not shipped

Searched: itch.io's [space skybox tag](https://itch.io/game-assets/tag-skybox/tag-space) (the owner's
suggestion), "gorgeous space sim skyboxes", "free CC0 space skybox", OpenGameArt and NASA/ESO archives.
These are free to use in a game but not to redistribute as files, so they are not in this public repository.
A player who downloads them can drop the panoramas into the game's `Skyboxes` data folder
(`%USERPROFILE%\AppData\LocalLow\MindAttic\Unity.SpaceGod\Skyboxes` on Windows), where they join the cycle.

| Pack | Resolution | Why not shipped |
|---|---|---|
| [Space Skyboxes Lite](https://willosuna.itch.io/space-skyboxes-lite) (Willosuna), 4 skies | 4096 x 2048 + cubemap + HDR | Commercial use free, but "reselling or redistributing the image files as a skybox pack" is not allowed. |
| [Nebula Drift free sample](https://pack-s.itch.io/nebula-drift-free-sample-3-sci-fi-space-skyboxes-360-cubemap-hdri) (pack_s), 3 skies | 2K | Free for commercial use; "don't resell or redistribute the files as-is". |
| [Skybox Sampler](https://the-knightshift.itch.io/skybox-sampler) (The_KnightShift), 3 space skies | 2048 x 1024 | Free for finished work; no redistribution of the images as assets. |
| [10 8k panoramic skyboxes](https://whynomakethings.itch.io/10-8k-panoramic-skyboxes) (whynomakethings) | 8K | No licence stated. |
| [Free AI-Generated Space Skyboxes](https://crediblesteve.itch.io/space-skyboxes) (Steviepie) | not stated | Personal use only. |
| [Real Night Sky Skybox Sample](https://stumasondev.itch.io/real-sky-skybox-sample) (stumasondev) | 2048 x 1024 | CC BY-SA 4.0, but low resolution with constellation lines drawn in; the NASA map covers the real sky better. |
| [OpenGameArt "Space skyboxes"](https://opengameart.org/node/10087) (arikel), 5 cubemaps | 1024 per face | CC0, but the author calls them placeholders; well below the others. |
| [Procedural Space HDRi Skybox 8K](https://superhivemarket.com/products/free-space-hdri-skybox) (Superhive) | 8K | Licence given only as "Creative Commons", with no version or terms. |
