//
// Copyright (c) 2026 7Bpencil
//
// This source code is licensed under the MIT license found in the
// LICENSE file in the root directory of this source tree.
//

using BepInEx;
using BepInEx.Logging;
using Comfort.Common;
using EFT.AssetsManager;
using EFT.InventoryLogic;
using HarmonyLib;
using SPT.Reflection.Patching;
using System;
using System.IO;
using System.Reflection;
using System.Collections.Generic;
using UnityEngine;
using ReloadExternalMagOperation = EFT.Player.FirearmController.ReloadExternalMagOperation;
using ReloadExternalMagResult = EFT.Player.FirearmController.ReloadExternalMagResult;

namespace SevenBoldPencil.TacticalReload;

public class ReloadData
{
	public ReloadExternalMagOperation Operation;
	public Item OldMagItem;
	public Transform OldMagView;
	public Transform NewMagView;
	public Transform OldBone;
}

[BepInPlugin("7Bpencil.TacticalReload", "7Bpencil.TacticalReload", "0.0.1")]
public class Plugin : BaseUnityPlugin
{
	public static Plugin Instance;

	public ManualLogSource LoggerInstance;

	public List<ModulePatch> Patches;

	public Dictionary<FirearmsAnimator, ReloadData> Reloads;

    private void Awake()
	{
		Instance = this;

		LoggerInstance = Logger;

		Patches = new();

		RegisterPatch(new Patch_ReloadExternalMagOperation_Reset());
		RegisterPatch(new Patch_ReloadExternalMagOperation_Start());
		RegisterPatch(new Patch_ReloadExternalMagOperation_OnMagPuttedToRig());
		RegisterPatch(new Patch_ReloadExternalMagOperation_OnMagAppeared());
		RegisterPatch(new Patch_ReloadExternalMagOperation_UseSecondMagForReload());
		RegisterPatch(new Patch_ReloadExternalMagOperation_OnMagInsertedToWeapon());

#if DEBUG
		RegisterPatch(new PrintDebug(typeof(ReloadExternalMagOperation), nameof(ReloadExternalMagOperation.Reset)));
		RegisterPatch(new PrintDebug(typeof(ReloadExternalMagOperation), nameof(ReloadExternalMagOperation.Start), [typeof(ReloadExternalMagResult), typeof(Callback)]));
		RegisterPatch(new PrintDebug(typeof(ReloadExternalMagOperation), nameof(ReloadExternalMagOperation.OnMagPulledOutFromWeapon)));
		RegisterPatch(new PrintDebug(typeof(ReloadExternalMagOperation), nameof(ReloadExternalMagOperation.OnMagPuttedToRig)));
		RegisterPatch(new PrintDebug(typeof(ReloadExternalMagOperation), nameof(ReloadExternalMagOperation.OnMagAppeared)));
		RegisterPatch(new PrintDebug(typeof(ReloadExternalMagOperation), nameof(ReloadExternalMagOperation.UseSecondMagForReload)));
		RegisterPatch(new PrintDebug(typeof(ReloadExternalMagOperation), nameof(ReloadExternalMagOperation.OnMagInsertedToWeapon)));
#endif

		var assemblyPath = Assembly.GetExecutingAssembly().Location;
		if (string.IsNullOrWhiteSpace(assemblyPath))
		{
			// happens for plugins loaded from scripts folder by BepInEx.ScriptEngine
			assemblyPath = Path.Combine(BepInEx.Paths.BepInExRootPath, "scripts", "7Bpencil.TacticalReload", "7Bpencil.TacticalReload.dll");
		}

        var assemblyDir = Path.GetDirectoryName(assemblyPath);
		Reloads = new();
	}

	public void RegisterPatch(ModulePatch patch)
	{
		patch.Enable();
		Patches.Add(patch);
	}

	private void OnDestroy()
	{
		foreach (var patch in Patches)
		{
			patch.Disable();
		}
		Patches.Clear();
		Reloads.Clear();
	}

	public void AssertEqual(ReloadExternalMagOperation a, ReloadExternalMagOperation b)
	{
		if (a != b)
		{
			Logger.LogError($"Reload operations not equal!");
		}
	}
}

public class Patch_ReloadExternalMagOperation_Reset : ModulePatch
{
    protected override MethodBase GetTargetMethod()
    {
        return AccessTools.Method(typeof(ReloadExternalMagOperation), nameof(ReloadExternalMagOperation.Reset));
    }

    [PatchPrefix]
    public static void Prefix(ReloadExternalMagOperation __instance)
	{
		// Operations get reused when possible, so we should clean old data too

		if (Plugin.Instance.Reloads.Remove(__instance.FirearmsAnimator, out var reload))
		{
			Plugin.Instance.AssertEqual(__instance, reload.Operation);
			// TODO any extra cleanup?
		}
	}
}

public class Patch_ReloadExternalMagOperation_Start : ModulePatch
{
    protected override MethodBase GetTargetMethod()
    {
        return AccessTools.Method(typeof(ReloadExternalMagOperation), nameof(ReloadExternalMagOperation.Start), [typeof(ReloadExternalMagResult), typeof(Callback)]);
    }

    [PatchPrefix]
    public static void Prefix(ReloadExternalMagOperation __instance, ReloadExternalMagResult reloadExternalMagResult)
	{
		// cannot tactical reload when theres no mag in weapon
		if (reloadExternalMagResult.OldMagazine == null)
		{
			return;
		}
		// TODO we dont support quick reloads for now
		if (reloadExternalMagResult.QuickReload)
		{
			return;
		}
		// if (!Plugin.Instance.WeaponConfigs.TryGetValue(__instance.Weapon.StringTemplateId, out var weaponConfig))
		// {
		// 	return;
		// }
		// if (!Plugin.Instance.CustomReloadAnimations.TryGetValue(weaponConfig.AnimationName, out var animationData))
		// {
		// 	return;
		// }
		if (!IsMagPairCompatible(
			reloadExternalMagResult.OldMagazine.magAnimationIndex,
			reloadExternalMagResult.NextMagazine.magAnimationIndex))
		{
			return;
		}

		Plugin.Instance.Reloads.Add(__instance.FirearmsAnimator, new()
		{
			Operation = __instance,
		});
	}

   	public static bool IsMagPairCompatible(int oldMag, int newMag)
	{
		// TODO hardcoded!
		return oldMag == 0 && newMag == 0;
	}
}

public class Patch_ReloadExternalMagOperation_OnMagPuttedToRig : ModulePatch
{
    protected override MethodBase GetTargetMethod()
    {
        return AccessTools.Method(typeof(ReloadExternalMagOperation), nameof(ReloadExternalMagOperation.OnMagPuttedToRig));
    }

    [PatchPrefix]
    public static bool Prefix(ReloadExternalMagOperation __instance)
	{
		// In "normal" reload animations character first pulls old mag out,
		// its item gets removed from slot and item view is destroyed, then
		// new mag is spawned, item gets inserted into slot and animation of putting it in is played.
		// This means player never sees two mags at the same time.
		// To not break anything I keep order of events the same, but do not dispawn old mag object
		// until UseSecondMagForReload event

		if (Plugin.Instance.Reloads.TryGetValue(__instance.FirearmsAnimator, out var reload))
		{
			Plugin.Instance.AssertEqual(__instance, reload.Operation);

			if (!__instance._magPuttedToRig)
			{
				__instance._magPuttedToRig = true;

				var slot = __instance.ReloadExternalMagResult.MagazineSlot;
				var slotView = __instance.Firearms.ContainerCollectionView.GetViewForSlot(slot);

				reload.OldMagItem = slotView.Item;
				reload.OldMagView = slotView.ItemView;

				// TODO at clean up we will need to copy paste most of the code from RemoveMod

				// if (__instance.ReloadExternalMagResult.RemoveOldMagResult != null)
				// {
				// 	Item item = ReloadExternalMagResult.RemoveOldMagResult.Item;
				// 	DropMod(item, EWeaponModType.mod_magazine);
				// }
				// Firearms.RemoveMod(ReloadExternalMagResult.MagazineSlot);
			}
			return false;
		}
		return true;
	}
}

public class Patch_ReloadExternalMagOperation_OnMagAppeared : ModulePatch
{
    protected override MethodBase GetTargetMethod()
    {
        return AccessTools.Method(typeof(ReloadExternalMagOperation), nameof(ReloadExternalMagOperation.OnMagAppeared));
    }

    [PatchPostfix]
    public static void Postfix(ReloadExternalMagOperation __instance)
	{
		// This event is fired when new mag first appears on the screen, usually its almost at the
		// start of the animation

		if (Plugin.Instance.Reloads.TryGetValue(__instance.FirearmsAnimator, out var reload))
		{
			Plugin.Instance.AssertEqual(__instance, reload.Operation);

			var slot = __instance.ReloadExternalMagResult.MagazineSlot;
			var viewSlot =  __instance.Firearms.ContainerCollectionView.GetViewForSlot(slot);
			var newMagView = viewSlot.ItemView;
			var oldBone = viewSlot.Bone;
			var newBone = oldBone.parent.Find("mod_magazine_new"); // TODO if not here, create it, also cache for future use
			newMagView.SetParent(newBone, false);

			reload.NewMagView = newMagView;
			reload.OldBone = oldBone;
		}
	}
}

public class Patch_ReloadExternalMagOperation_UseSecondMagForReload : ModulePatch
{
    protected override MethodBase GetTargetMethod()
    {
        return AccessTools.Method(typeof(ReloadExternalMagOperation), nameof(ReloadExternalMagOperation.UseSecondMagForReload));
    }

    [PatchPrefix]
    public static bool Prefix(ReloadExternalMagOperation __instance)
	{
		// I time this event when old mag is offscreen and can be despawned without player noticing
		// (or replaced with loot version in fast reload animation)

		if (Plugin.Instance.Reloads.TryGetValue(__instance.FirearmsAnimator, out var reload))
		{
			Plugin.Instance.AssertEqual(__instance, reload.Operation);
			AssetPoolObject.ReturnToPool(reload.OldMagView.gameObject);
			reload.OldMagView = null;
			// TODO spawn dropped mag?

			return false;
		}
		return true;
	}
}

public class Patch_ReloadExternalMagOperation_OnMagInsertedToWeapon : ModulePatch
{
    protected override MethodBase GetTargetMethod()
    {
        return AccessTools.Method(typeof(ReloadExternalMagOperation), nameof(ReloadExternalMagOperation.OnMagInsertedToWeapon));
    }

    [PatchPostfix]
    public static void Postfix(ReloadExternalMagOperation __instance)
	{
		// I time this event when magazine_new bone is exactly in the same position/rotation
		// as ordinary magazine bone, this way snapping magazine object back to the bone game expects
		// wont produce any weird teleporting mags.

		if (Plugin.Instance.Reloads.TryGetValue(__instance.FirearmsAnimator, out var reload))
		{
			Plugin.Instance.AssertEqual(__instance, reload.Operation);
			reload.NewMagView.SetParent(reload.OldBone);
		}
	}
}

#if DEBUG
public class PrintDebug : ModulePatch
{
	private readonly Type Type;
	private readonly Type[] Parameters;
	private readonly string MethodName;

	public PrintDebug(Type type, string methodName, Type[] parameters = null)
	{
		Type = type;
		Parameters = parameters;
		MethodName = methodName;
	}

    protected override MethodBase GetTargetMethod()
    {
        return AccessTools.Method(Type, MethodName, Parameters);
    }

    [PatchPrefix]
    public static void Prefix(MethodBase __originalMethod)
	{
		Logger.LogWarning($"{__originalMethod.DeclaringType.Name}.{__originalMethod.Name}");
	}
}
#endif
