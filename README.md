# Railroader Asset Development Kit

The Railroader ADK is a Unity project for packaging asset packs (mods) for [Railroader][railroader], a railroad operations simulator for PC.

The ADK provides editor tools and shared shaders needed to build rolling stock, scenery, and other content.

For full modding documentation, visit [docs.railroadergame.com][docs-modding].

For help, check out:

- [Railroader Community](https://community.railroadergame.com)
- [Discord](https://discord.gg/7JXdc56Fpc)
- [Getting Help](https://docs.railroadergame.com/support/)

## Concepts/Components

### Asset Pack

Packaging mechanism for a mod. Includes:

- `Info.json` which describes the mod and its dependencies, as well as one or more of:
- `Definitions.json` (describing cars or other data being added to the game)
- Assets (models, materials, textures, audio)
- Relay scripts (custom runtime behavior)

Asset Packs containing models/materials/textures/audio must be built by the Asset Packs window before they can be loaded by the game. This process builds the assets into a Unity [AssetBundle](https://docs.unity3d.com/6000.3/Documentation/Manual/AssetBundlesIntro.html).

### The Asset Packs window

A custom Unity Editor tool that manages, edits, and builds Asset Packs. Open it from the Unity menu bar: **Railroader > Asset Packs**. It lists your packs on one side and shows the selected pack's details -- metadata, dependencies, module exports, and assets -- on the other.

### Asset Pack Workflow: ModDev/ADK/Storage

A pack can live in up to three places, and every action in the window moves it between them:

```
ModDev  <->  ADK  <->  Storage
(runs)      (edit)     (keep)
```

- **Storage**: A directory _outside_ the ADK where the source of your Asset Pack lives. Usually this would be your source control. Set it under **Paths** in the toolbar; packs found there are listed under the **Storage** tab.
- **ADK**: The `Assets/AssetPacks` directory within the ADK. Packs must be copied here, into the ADK's Unity Asset Database, so that they can be compiled. This allows Unity to resolve asset references. This directory is excluded from the ADK source control, and is the only place a pack is editable.
- **ModDev**: The `Documents/Railroader/ModDev` directory. Railroader reads development mods from this location.
  - The in-game Definition Editor also writes `Info.json` and `Definitions.json` files here when saving.
  - **Important**: Users should install mods in `Documents/Railroader/Mods`, never `ModDev`!

A fourth, read-only group -- **Shared** reference packs -- has its own tab. Those ship with the game and exist so Unity can resolve references to shared shaders and materials at build time; they can be browsed but not edited.

The buttons on a selected pack map directly onto that diagram:

| Button | Moves | Use it to |
| --- | --- | --- |
| **Load from Storage** | Storage -> ADK | Start editing a pack you keep in Storage |
| **Build** | ADK -> ModDev | Compile the asset bundle and install it for testing |
| **Push Text** | ADK -> ModDev | Send `Info.json`, `Definitions.json`, and scripts without a full rebuild |
| **Pull** | ModDev -> ADK | Bring in-game Definition Editor changes back into the project |
| **Save** | ADK -> Storage | Keep finished work in your library |
| **Discard** | deletes the ADK copy, the ModDev build, or both | Clean up a pack you are no longer editing |

Editing a pack's metadata uses **Apply** and **Revert** in the detail toolbar: Apply writes your `Info.json` edits to the ADK copy, Revert discards them. Every action in the table above applies pending edits first, so Apply is mostly a confirmation step.

A general workflow looks like this:

- Create a pack, or Load from Storage an existing one
- Build
- Edit the pack using the in-game Definition Editor
- Pull, to bring those edits back into the ADK
- Save, to copy the pack to Storage
- Commit your Storage copy to your own source control
- Discard the ADK copy

The window also has a **Build ADK Packs** button that builds everything currently loaded into the ADK, in dependency order.

Keep your `ModDev` and ADK directories clean. Save to Storage and discard packs that you are not actively working on. This helps prevent confusion about which files are up to date, as well as keeping build times fast.

## Prerequisites

- **Unity 6** (6000.3) -- the ADK currently uses 6000.3.26f1. Install it via [Unity Hub](https://unity.com/download).
  - A Unity license is required. The free Personal license is appropriate for most modders -- check [Unity's license terms](https://unity.com/legal/terms-of-service/software) for eligibility.
- **Railroader** installed, so you have a `Documents/Railroader/` directory for testing.

## Getting Started

1. **Clone or download this repository.**

2. **Open the project in Unity Hub.** Click "Add project from disk" and select the ADK root folder. Open the project in Unity.

3. **Open the Asset Packs window.** From the Railroader menu, click Asset Packs to open the window. It provides a high level view of your asset packs, along with the provided shared packs (used for common dependencies such as shaders).

4. **Create a new asset pack.** Click **+ New**, then enter your **Domain** (your unique author name, e.g. `myhandle`) and **Pack Name** (e.g. `my-freight-car`). These combine to form the pack identifier (`myhandle/my-freight-car`). Click Create to generate a folder under `Assets/AssetPacks/` with a starter `Info.json`.

5. **Add your content.** For details on this, see the modding documentation.

6. **Set your ModDev path.** Click **Paths** in the toolbar and set **ModDev** to your local ModDev directory: `Documents/Railroader/ModDev/`. When you build your asset pack, the result will be copied to this location. Railroader's built-in definition editor will edit packs at this location.

7. **Build and test.** Select your pack and click **Build**. Launch Railroader and your pack will be loaded from ModDev automatically. Note that you will need to enable the mod once, in Manage Mods -- the enabled mod set is global and applies from startup, not chosen per save.

## Project Structure

```
Assets/
  AssetPacks/       # Your asset packs (this is where you work)
  SharedPacks/      # Reference assets from the base game (do not modify)
  Scripts/
    AssetPack/
      Common/       # Runtime C# for the asset pack system
      Editor/       # Editor tooling (Asset Packs window, build pipeline)
  Scenes/
    Scene.unity     # Preview scene
  Settings/         # URP and rendering settings
  URP/              # Universal Render Pipeline assets
```

## Shared Assets

The `Assets/SharedPacks/shared/` directory contains shaders, materials, and textures from the base game that are referenced by many asset packs. These are included so that Unity can resolve references during asset bundle builds. At runtime, the game loads its own copies of these assets -- your built bundle will not include duplicates.

Note that this is a modified version of the actual shared pack that is loaded at runtime. Its shaders have been simplified in some ways (wear removed from the Standard Car Shader, stochastic shader code removed that is licensed for use in the game but not to have its source distributed, etc.). Textures have also been sized down from the originals to keep this project size low. At runtime the actual shared pack with full featured shaders and texture resolution will be used.

We **strongly** recommend that you use the provided shared shaders rather than URP/Lit, and we cannot guarantee support for custom shaders. The included shaders help keep asset pack sizes and memory usage low, as well as allowing for batching.

For more on the shared pack, see [modding documentation][docs-modding].

## Distributing Your Mod

Once your mod is ready for release, distribute the built output folder. Players install mods by placing them in `Documents/Railroader/Mods/`. We recommend naming the folder after the identifier and version, e.g. `pedro-ps1-1.2.0` for identifier `pedro/ps1` version 1.2.0.

## License

The ADK is available under the [MIT License](LICENSE), except for the asset packs in `Assets/SharedPacks/`, which state their own license in `Info.json`.

[docs]: https://docs.railroadergame.com
[docs-modding]: https://docs.railroadergame.com/modding/
[railroader]: https://store.steampowered.com/app/2222930/Railroader/
