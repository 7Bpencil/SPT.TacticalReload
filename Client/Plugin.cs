//
// Copyright (c) 2026 7Bpencil
//
// This source code is licensed under the MIT license found in the
// LICENSE file in the root directory of this source tree.
//

// TODO just make custom animation events

using AnimationSystem;
using AnimationEventSystem;
using BepInEx;
using BepInEx.Configuration;
using Comfort.Common;
using Diz.Skinning;
using Diz.Jobs;
using Diz.Utils;
using EFT;
using EFT.CameraControl;
using EFT.GameTriggers;
using EFT.Impostors;
using EFT.Interactive;
using EFT.Settings.Graphics;
using EFT.UI.Settings;
using EFT;
using EFT.Animations;
using EFT.AssetsManager;
using EFT.InventoryLogic;
using EFT.Visual;
using EFT.UI;
using EFT.UI.WeaponModding;
using EFT.Utilities;
using Newtonsoft.Json;
using HarmonyLib;
using SevenBoldPencil.Common;
using SPT.Reflection.Patching;
using System;
using System.IO;
using System.Reflection;
using System.Collections;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using System.Linq;
using UnityEngine;
using UnityEngine.SceneManagement;
using JsonType;
using GPUInstancer;
using Koenigz.PerfectCulling.EFT;
using FirearmController = EFT.Player.FirearmController;
using ReloadExternalMagOperation = EFT.Player.FirearmController.ReloadExternalMagOperation;
using ReloadExternalMagResult = EFT.Player.FirearmController.ReloadExternalMagResult;
using AnimationEvent = AnimationEventSystem.AnimationEvent;

namespace SevenBoldPencil.TacticalReload;

// event names
// 1551431816 MagShow -> OnMagAppeared
// 798565163  MagHide -> OnMagPuttedToRig
// 1258896930 MagIn   -> OnMagInsertedToWeapon
// 1947938901 MagOut  -> OnMagPulledOutFromWeapon
// PutMagToRig -> Unused
// 443236115 ReplaceSecondMag -> Unused
// 67499002  UseSecondMagForReload -> UseSecondMagForReload/SwitchToReloadWithNewMag

// original:
// [Warning:ModulePatch] Start
// [Warning:ModulePatch] OnMagPulledOutFromWeapon -> MagOut
// [Warning:ModulePatch] OnMagPuttedToRig -> MagHide
// [Warning:ModulePatch] OnMagAppeared -> MagShow
// [Warning:ModulePatch] OnMagInsertedToWeapon -> MagIn
// [Warning:ModulePatch] Start

// new
// [Warning:ModulePatch] Start
// [Warning:ModulePatch] OnMagPulledOutFromWeapon
// [Warning:ModulePatch] OnMagPuttedToRig
// [Warning:ModulePatch] OnMagAppeared
// [Warning:ModulePatch] UseSecondMagForReload
// [Warning:ModulePatch] Start

// hit
// [Warning:ModulePatch] Start
// [Warning:ModulePatch] OnMagAppeared
// [Warning:ModulePatch] OnMagPulledOutFromWeapon
// [Warning:ModulePatch] OnMagInsertedToWeapon
// [Warning:ModulePatch] Start

// admin
// [Warning:ModulePatch] Start
// [Warning:ModulePatch] OnMagAppeared
// [Warning:ModulePatch] OnMagPulledOutFromWeapon
// [Warning:ModulePatch] OnMagInsertedToWeapon
// [Warning:ModulePatch] OnMagPuttedToRig
// [Warning:ModulePatch] Start

[Serializable]
public class AnimationBehaviourData
{
	public string FullName;
	public int FullNameHash;
	public int EventsListId;
	public List<AnimationEvent> AnimationEvents;
}

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

	public Dictionary<string, Dictionary<int, AnimationBehaviourData>> CustomAnimationsData;
	public Dictionary<FirearmsAnimator, ReloadData> Reloads;

    private void Awake()
	{
		Instance = this;

		new Patch_WeaponPrefab_InitHotObjects().Enable();
		new Patch_ReloadExternalMagOperation_Reset().Enable();
		new Patch_ReloadExternalMagOperation_Start().Enable();
		new Patch_FirearmsAnimator_Reload().Enable();
		new Patch_ReloadExternalMagOperation_OnMagPuttedToRig().Enable();
		new Patch_ReloadExternalMagOperation_OnMagAppeared().Enable();
		new Patch_ReloadExternalMagOperation_UseSecondMagForReload().Enable();
		new Patch_ReloadExternalMagOperation_OnMagInsertedToWeapon().Enable();

		new PrintDebug<byte>(typeof(ReloadExternalMagOperation), nameof(ReloadExternalMagOperation.Reset)).Enable();
		new PrintDebug<short>(typeof(ReloadExternalMagOperation), nameof(ReloadExternalMagOperation.Start), [typeof(ReloadExternalMagResult), typeof(Callback)]).Enable();
		new PrintDebug<ushort>(typeof(ReloadExternalMagOperation), nameof(ReloadExternalMagOperation.OnMagPulledOutFromWeapon)).Enable();
		new PrintDebug<int>(typeof(ReloadExternalMagOperation), nameof(ReloadExternalMagOperation.OnMagPuttedToRig)).Enable();
		new PrintDebug<uint>(typeof(ReloadExternalMagOperation), nameof(ReloadExternalMagOperation.OnMagAppeared)).Enable();
		new PrintDebug<long>(typeof(ReloadExternalMagOperation), nameof(ReloadExternalMagOperation.UseSecondMagForReload)).Enable();
		new PrintDebug<ulong>(typeof(ReloadExternalMagOperation), nameof(ReloadExternalMagOperation.OnMagInsertedToWeapon)).Enable();

        var assemblyDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
		CustomAnimationsData = LoadCustomAnimationsData(assemblyDir);
		Reloads = new();
	}

	public Dictionary<string, Dictionary<int, AnimationBehaviourData>> LoadCustomAnimationsData(string assemblyDir)
	{
        var configPath = Path.Combine(assemblyDir, "data", "config.jsonc");
        if (!SafeIO.ReadAllText(configPath).Ok(out var configJson, out var _))
        {
			return [];
		}

		var animationEventsPath = Path.Combine(assemblyDir, "data", "animation-events");
		var animationEventPaths = SafeIO.GetFiles(animationEventsPath, "*.jsonc", SearchOption.AllDirectories);
		var resultEvents = new Dictionary<string, Dictionary<int, AnimationBehaviourData>>(animationEventPaths.Length);
		foreach (var animationEventPath in animationEventPaths)
		{
			if (SafeIO.ReadAllText(animationEventPath).Ok(out var animationEventJson, out var _))
			{
				var animationEventData = JsonConvert.DeserializeObject<List<AnimationBehaviourData>>(animationEventJson);
				var animationEventDataDict = new Dictionary<int, AnimationBehaviourData>(animationEventData.Count);
				foreach (var data in animationEventData)
				{
					animationEventDataDict[data.FullNameHash] = data;
				}

				var filename = Path.GetFileNameWithoutExtension(animationEventPath);
				resultEvents.Add(filename, animationEventDataDict);
			}
		}

		var config = JsonConvert.DeserializeObject<Dictionary<string, string>>(configJson);
		var result = new Dictionary<string, Dictionary<int, AnimationBehaviourData>>(config.Count);
		foreach (var (template, animationName) in config)
		{
			if (resultEvents.TryGetValue(animationName, out var animations))
			{
				result.Add(template, animations);
			}
		}

		return result;
	}

	public void DumpAnimationData(WeaponPrefab weaponPrefab)
	{
		var result = new List<AnimationBehaviourData>();
		foreach (var ieventBehaviour in weaponPrefab.AnimationEventsEmitter._animationEventsStateBehaviours)
		{
			if (ieventBehaviour is not AnimationEventsStateBehaviour eventBehaviour)
			{
				continue;
			}

			var resultAnimationEvents = new List<AnimationEvent>();
			foreach (var animationEvent in eventBehaviour.AnimationEvents)
			{
				resultAnimationEvents.Add((AnimationEvent)animationEvent.Clone());
			}

			result.Add(new()
			{
				FullName = eventBehaviour.FullName,
				FullNameHash = eventBehaviour.FullNameHash,
				EventsListId = eventBehaviour.EventsListId,
				AnimationEvents = resultAnimationEvents,
			});
		}

        var json = JsonConvert.SerializeObject(result, Formatting.Indented);
        var filePath = "animation-events.json";
        SafeIO.WriteAllTextAsync(filePath, json);
	}

	public void AssertEqual(ReloadExternalMagOperation a, ReloadExternalMagOperation b)
	{
		if (a != b)
		{
			Logger.LogError($"Reload operations not equal!");
		}
	}
}

public class Patch_WeaponPrefab_InitHotObjects : ModulePatch
{
    protected override MethodBase GetTargetMethod()
    {
        return AccessTools.Method(typeof(WeaponPrefab), nameof(WeaponPrefab.InitHotObjects));
    }

    [PatchPostfix]
    public static void Postfix(WeaponPrefab __instance, Weapon ____weaponData)
	{
		// old but reliable way of tracking weapon spawn
		if (____weaponData == null)
		{
			return;
		}
		// AnimationEventsEmitter is null on item preview screen
		if (__instance.AnimationEventsEmitter == null)
		{
			return;
		}
		if (!Plugin.Instance.CustomAnimationsData.TryGetValue(____weaponData.StringTemplateId, out var customAnimationsData))
		{
			return;
		}
		// replace tactical animations events data with ones designed to work with our system
		foreach (var ieventBehaviour in __instance.AnimationEventsEmitter._animationEventsStateBehaviours)
		{
			if (ieventBehaviour is not AnimationEventsStateBehaviour eventBehaviour)
			{
				continue;
			}
			if (!customAnimationsData.TryGetValue(eventBehaviour.FullNameHash, out var customData))
			{
				continue;
			}
			eventBehaviour.EventsData._stateHashToEventsCollection[eventBehaviour.EventsListId]._animationEvents = customData.AnimationEvents;
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
    public static void Prefix(ReloadExternalMagOperation __instance)
	{
		// TODO here we decide if reload is tactical

		if (!Plugin.Instance.CustomAnimationsData.ContainsKey(__instance.Weapon.StringTemplateId))
		{
			return;
		}

		Plugin.Instance.Reloads.Add(__instance.FirearmsAnimator, new()
		{
			Operation = __instance,
		});
	}
}

public class Patch_FirearmsAnimator_Reload : ModulePatch
{
    protected override MethodBase GetTargetMethod()
    {
        return AccessTools.Method(typeof(FirearmsAnimator), nameof(FirearmsAnimator.Reload), [typeof(int), typeof(int), typeof(bool)]);
    }

    [PatchPrefix]
    public static bool Prefix(FirearmsAnimator __instance, int currentMagType, int nextMagType, bool isFast)
	{
		// FirearmsAnimator.Reload is called right inside ReloadExternalMagOperation.Start, so I am pretty sure
		// that we are still in the same Operation that wanted tactical reload, and using FirearmsAnimator as a key is fine

		if (Plugin.Instance.Reloads.TryGetValue(__instance, out var reload))
		{
			__instance.SetMagTypeCurrent(currentMagType);
			__instance.SetMagTypeNew(nextMagType);

			if (__instance.Animator is UnityAnimatorWrapper animator)
			{
				var layerIndex = animator.GetLayerIndex("Hands"); // TODO cache
				animator.Play("RELOAD PROGRESSIVE", layerIndex, 0);
			}

			return false;
		}

		return true;
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
			var newBone = oldBone.parent.Find("mod_magazine_new"); // TODO cache
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

// T is a dummy class, to keep all classes separate,
// otherwise they will share method name (because its static)
public class PrintDebug<T> : ModulePatch
{
	private readonly Type Type;
	private readonly Type[] Parameters;
	private static string MethodName;

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
    public static void Prefix()
	{
		Logger.LogWarning(MethodName);
	}
}
