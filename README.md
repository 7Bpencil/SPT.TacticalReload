Twin loading unlocks on weapon mastery level 2, Quad - on level 3

Video: <https://youtu.be/CrZBKCG47AY>

![](Previews/video-thumbnail.png)

## Notes

### Animator Controller Setup

- Look at animation data and search for paths to extra patron bones (they should have `patron_in_weapon` prefix in the name), add those path to avatar masks on animator controller layers, otherwise those bones wont be animated

- Shotgun reload animation is played in `RELOAD IN` state, right click on it and select `Create new BlendTree in State`, use `RelTypeOld` as a blend tree parameter, 0 is the original animation, 1 is twin load, 2 is quad load (or whatever animations you have)

- If state `Speed` is not 1, set speed of original animation in blend tree to that number and reset state `Speed` to 1

- Check transition durations from `RELOAD IN` to other states, in case of Benelli they were too short (0.01) which looked fine for default animation, but not for others, so set it to 0.2 (select value that looks good for all reloads,
since it will be shared across all of them in blend tree)

- Also untick `Has Exit Time` on all transitions, we will specify transition times per animation in next section

### Animation Events Setup

- Look at `Animation Events State Behaviour` component on `RELOAD IN` state, open Scriptable Object referenced by `Events Data`, select element in `State Hash To Events Collection` with index from `Events List Id`.

- Add `ShowAmmo` event at the same time as the earliest existing event (dont use 0, events with such time sometimes get skipped), we use that event to spawn shells on patron bones.

- Now state has animation events only for original animation, so add events for all variants, use `RelTypeOld & Float & GreaterThan` and `RelTypeOld & Float & LessThan` event conditions to differentiate between events for different animations.

- All reload animations are looped, which means time when `RELOAD IN` transitions to other state is determined only by parameters of animator controller, in most cases its `CanReload` being set to false by `AddAmmoInMag` event, so you should choose good time for it: after all shells are loaded, but early enough so animation transition to `RELOAD END` looks good

### Bundle setup

- If you are making custom weapon, theres no need to have separate bundle, so skip this section.
- If you are modifying vanilla weapon, set label only on animator controller, all other files should not have labels.

### Config setup

- `BundleName` and `AnimatorControllerPath` are needed only if you modify vanilla weapon and ship separate bundle, otherwise leave them as null
- `WeaponPrefabName` is `WeaponPrefab._objectInstance.name` (use unity explorer for that)
- `OriginalPatronBonePath` is path to `patron_in_weapon` starting from `Weapon_root`
- `ExtraPatronBoneNames` is only names and not full paths, because we assume that they share parent with original patron bone
- `PatronIndicesPerReloadType` determine:
  - how many shells animation loads,
  - to which patron bones shells get parented (because some animations visually mismatch load order of shells, for example in benelli quad reload `patron_in_weapon_003` bone loads its shell first instead of usual `patron_in_weapon`)
