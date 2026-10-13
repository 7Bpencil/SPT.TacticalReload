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
using BepInEx.Logging;
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
using UnityEngine.Animations;
using UnityEngine.Playables;
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
public class WeaponConfig
{
	public string AnimationName;
}

[Serializable]
public class ReloadAnimationDataConfig
{
	public string ReloadStart;
	public string Reload;
	public string ReloadEnd;

	public double StartToReloadTransition;
	public double ReloadToEndTransition;

	public CompatibleMagPair[] CompatibleMagPairs;

	public AnimationEvent[] ReloadAnimationEvents;
}

[Serializable]
public struct CompatibleMagPair
{
	public int Old;
	public int New;
}

public class ReloadAnimationData
{
	public AnimationClip ReloadStart;
	public AnimationClip Reload;
	public AnimationClip ReloadEnd;

	public double StartToReloadTransition;
	public double ReloadToEndTransition;

	public CompatibleMagPair[] CompatibleMagPairs;

	public AnimationEvent[] ReloadAnimationEvents;
}

[Serializable]
public class AnimationBehaviourData
{
	public string FullName;
	public int FullNameHash;
	public int EventsListId;
	public List<AnimationEvent> AnimationEvents;
}

public enum ReloadState
{
	Original,
	Start,
	StartToReload,
	Reload,
	ReloadToEnd,
	End,
}

public record struct AnimationClipWrapper
(
	AnimationClip Clip,
	AnimationClipPlayable Playable,
	int Index,
	AnimationEvent[] AnimationEvents,
	int LastPlayedAnimationEventIndex
);

// TODO we need idle -> reload start and reload end -> idle transitions
public class ReloadData
{
	public ReloadExternalMagOperation Operation;
	public ReloadAnimationData AnimationData;

	public Item OldMagItem;
	public Transform OldMagView;
	public Transform NewMagView;
	public Transform OldBone;

	public AnimationClipWrapper ReloadStart;
	public AnimationClipWrapper Reload;
	public AnimationClipWrapper ReloadEnd;

	public bool IsInit;
	public ReloadState ReloadState;
    public Animator Animator;
    public PlayableGraph Graph;
    public AnimatorControllerPlayable VanillaController;
    public AnimationMixerPlayable Mixer;
    public AnimationPlayableOutput Output;

	public void Init(Animator animator)
	{
		if (IsInit)
		{
			return;
		}

		Animator = animator;
		Graph = PlayableGraph.Create("Tactical Reload");
		Graph.SetTimeUpdateMode(DirectorUpdateMode.GameTime);

		var reloadStartPlayable = AnimationClipPlayable.Create(Graph, AnimationData.ReloadStart);
		var reloadPlayable = AnimationClipPlayable.Create(Graph, AnimationData.Reload);
		var reloadEndPlayable = AnimationClipPlayable.Create(Graph, AnimationData.ReloadEnd);

		VanillaController = AnimatorControllerPlayable.Create(Graph, Animator.runtimeAnimatorController);
		ReloadStart = new(AnimationData.ReloadStart, reloadStartPlayable, 1, [], -1);
		Reload = new(AnimationData.Reload, reloadPlayable, 2, AnimationData.ReloadAnimationEvents, -1);
		ReloadEnd = new(AnimationData.ReloadEnd, reloadEndPlayable, 3, [], -1);

		Mixer = AnimationMixerPlayable.Create(Graph, 4);

		Mixer.ConnectInput(0, VanillaController, 0);
		Mixer.ConnectInput(ReloadStart.Index, ReloadStart.Playable, 0);
		Mixer.ConnectInput(Reload.Index, Reload.Playable, 0);
		Mixer.ConnectInput(ReloadEnd.Index, ReloadEnd.Playable, 0);

		Mixer.SetInputWeight(0, 1);
		Mixer.SetInputWeight(ReloadStart.Index, 0);
		Mixer.SetInputWeight(Reload.Index, 0);
		Mixer.SetInputWeight(ReloadEnd.Index, 0);

		Output = AnimationPlayableOutput.Create(Graph, "Tactical Reload Output", Animator);
		Output.SetSourcePlayable(Mixer);
		Graph.Play();

		IsInit = true;
		ReloadState = ReloadState.Original;
	}

	public void StartReload()
	{
		ReloadState = ReloadState.Start;
        ReloadStart.Playable.SetTime(0);
        Mixer.SetInputWeight(0, 0);
        Mixer.SetInputWeight(ReloadStart.Index, 1);
        Mixer.SetInputWeight(Reload.Index, 0);
        Mixer.SetInputWeight(ReloadEnd.Index, 0);
		TryFireAnimationEvents(ref ReloadStart);
	}

	public void Update()
	{
		if (!IsInit)
		{
			return;
		}

        switch (ReloadState)
        {
            case ReloadState.Start:
			{
				if (PlayState(ref ReloadStart, ref Reload, AnimationData.StartToReloadTransition))
				{
					ReloadState = ReloadState.StartToReload;
				}
                break;
			}
            case ReloadState.StartToReload:
			{
				if (PlayStateTransition(ref ReloadStart, ref Reload, AnimationData.StartToReloadTransition))
				{
					ReloadState = ReloadState.Reload;
				}
                break;
			}
            case ReloadState.Reload:
			{
				if (PlayState(ref Reload, ref ReloadEnd, AnimationData.ReloadToEndTransition))
				{
					ReloadState = ReloadState.ReloadToEnd;
				}
                break;
			}
            case ReloadState.ReloadToEnd:
			{
				if (PlayStateTransition(ref Reload, ref ReloadEnd, AnimationData.ReloadToEndTransition))
				{
					ReloadState = ReloadState.End;
				}
                break;
			}
            case ReloadState.End:
			{
				var reloadEndTime = ReloadEnd.Playable.GetTime();
				var reloadEndDuration = ReloadEnd.Clip.length;

		        if (reloadEndTime >= reloadEndDuration)
		        {
					ReloadState = ReloadState.Original;

					Mixer.SetInputWeight(0, 0);
					Mixer.SetInputWeight(1, 0);
					Mixer.SetInputWeight(2, 0);
					Mixer.SetInputWeight(3, 0);

			        Graph.Stop();
					Output.SetSourcePlayable(Playable.Null);

					// TODO force IDLE state?
		        }
                break;
			}
        }
	}

	public bool PlayState(ref AnimationClipWrapper fromState, ref AnimationClipWrapper toState, double transitionDuration)
	{
		var fromTime = fromState.Playable.GetTime();
		var fromDuration = fromState.Clip.length;
		var (fromWeight, toWeight, toTime) = GetTransitionWeights(fromTime, fromDuration, transitionDuration);

		// Plugin.Instance.LoggerInstance.LogWarning($"PlayState          : {ReloadState}, {fromTime}|{fromWeight} {toTime}|{toWeight}");

		TryFireAnimationEvents(ref fromState);

		if (toTime >= 0)
		{
	        Mixer.SetInputWeight(fromState.Index, fromWeight);
	        Mixer.SetInputWeight(toState.Index, toWeight);
	        toState.Playable.SetTime(toTime);
			TryFireAnimationEvents(ref toState);
			return true;
		}

		return false;
	}

	public bool PlayStateTransition(ref AnimationClipWrapper fromState, ref AnimationClipWrapper toState, double transitionDuration)
	{
		var fromTime = fromState.Playable.GetTime();
		var fromDuration = fromState.Clip.length;
		var (fromWeight, toWeight, toTime) = GetTransitionWeights(fromTime, fromDuration, transitionDuration);

		// Plugin.Instance.LoggerInstance.LogWarning($"PlayStateTransition: {ReloadState}, {fromTime}|{fromWeight} {toTime}|{toWeight}");

        Mixer.SetInputWeight(fromState.Index, fromWeight);
        Mixer.SetInputWeight(toState.Index, toWeight);

		TryFireAnimationEvents(ref fromState);
		TryFireAnimationEvents(ref toState);

		if (toTime >= transitionDuration)
		{
			return true;
		}

		return false;
	}

	public static (float fromWeight, float toWeight, double toTime) GetTransitionWeights(
		double fromClipTime,
		double fromClipDuration,
		double transitionDuration
	) {
		var toTime = fromClipTime - (fromClipDuration - transitionDuration);
		var toWeight = System.Math.Clamp(toTime / transitionDuration, 0, 1);
		var fromWeight = 1 - toWeight;
		return ((float)fromWeight, (float)toWeight, toTime);
	}

	public void TryFireAnimationEvents(ref AnimationClipWrapper state)
	{
		var time = state.Playable.GetTime() / state.Clip.length;
		var animationEvents = state.AnimationEvents;
		var animationEventsEmitter = Operation.Controller._weaponPrefab.AnimationEventsEmitter;
		for (var i = 0; i < animationEvents.Length; i++)
		{
			var animationEvent = animationEvents[i];
			if (time >= animationEvent.Time && i > state.LastPlayedAnimationEventIndex)
			{
				// Plugin.Instance.LoggerInstance.LogWarning($"Fire Event: {animationEvent.FunctionName}");

				state.LastPlayedAnimationEventIndex = i;
				animationEventsEmitter.SendEvent(animationEvent.FunctionName, animationEvent.FunctionNameHash, animationEvent.Parameter, 0);
			}
		}
	}
}

[BepInPlugin("7Bpencil.TacticalReload", "7Bpencil.TacticalReload", "0.0.1")]
public class Plugin : BaseUnityPlugin
{
	public static Plugin Instance;

	public ManualLogSource LoggerInstance;

	public Dictionary<string, ReloadAnimationData> CustomReloadAnimations;
	public Dictionary<string, WeaponConfig> WeaponConfigs;
	public Dictionary<FirearmsAnimator, ReloadData> Reloads;

    private void Awake()
	{
		Instance = this;

		LoggerInstance = Logger;

		new Patch_WeaponPrefab_InitHotObjects().Enable();
		new Patch_ReloadExternalMagOperation_Reset().Enable();
		new Patch_ReloadExternalMagOperation_Start().Enable();
		new Patch_FirearmsAnimator_Reload().Enable();
		new Patch_ReloadExternalMagOperation_OnMagPuttedToRig().Enable();
		new Patch_ReloadExternalMagOperation_OnMagAppeared().Enable();
		new Patch_ReloadExternalMagOperation_UseSecondMagForReload().Enable();
		new Patch_ReloadExternalMagOperation_OnMagInsertedToWeapon().Enable();

#if DEBUG
		new PrintDebug<byte>(typeof(ReloadExternalMagOperation), nameof(ReloadExternalMagOperation.Reset)).Enable();
		new PrintDebug<short>(typeof(ReloadExternalMagOperation), nameof(ReloadExternalMagOperation.Start), [typeof(ReloadExternalMagResult), typeof(Callback)]).Enable();
		new PrintDebug<ushort>(typeof(ReloadExternalMagOperation), nameof(ReloadExternalMagOperation.OnMagPulledOutFromWeapon)).Enable();
		new PrintDebug<int>(typeof(ReloadExternalMagOperation), nameof(ReloadExternalMagOperation.OnMagPuttedToRig)).Enable();
		new PrintDebug<uint>(typeof(ReloadExternalMagOperation), nameof(ReloadExternalMagOperation.OnMagAppeared)).Enable();
		new PrintDebug<long>(typeof(ReloadExternalMagOperation), nameof(ReloadExternalMagOperation.UseSecondMagForReload)).Enable();
		new PrintDebug<ulong>(typeof(ReloadExternalMagOperation), nameof(ReloadExternalMagOperation.OnMagInsertedToWeapon)).Enable();
#endif

        var assemblyDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
		CustomReloadAnimations = LoadCustomReloadAnimations(assemblyDir);
		WeaponConfigs = LoadWeaponConfigs(assemblyDir);
		Reloads = new();
	}

	public Dictionary<string, ReloadAnimationData> LoadCustomReloadAnimations(string assemblyDir)
	{
		var animationsPath = Path.Combine(assemblyDir, "data", "animations");
		var animationConfigPaths = SafeIO.GetFiles(animationsPath, "*.jsonc");
		var result = new Dictionary<string, ReloadAnimationData>(animationConfigPaths.Length);
		foreach (var animationConfigPath in animationConfigPaths)
		{
			if (SafeIO.ReadAllText(animationConfigPath).Ok(out var animationConfigJson, out var _))
			{
				var animationConfig = JsonConvert.DeserializeObject<ReloadAnimationDataConfig>(animationConfigJson);
				var filename = Path.GetFileNameWithoutExtension(animationConfigPath);
				var bundlePath = Path.Combine(assemblyDir, "data", "animations", $"{filename}.bundle");
				var bundle = AssetBundle.LoadFromFile(bundlePath);
				var animationData = new ReloadAnimationData()
				{
					ReloadStart = bundle.LoadAsset<AnimationClip>(animationConfig.ReloadStart),
					Reload = bundle.LoadAsset<AnimationClip>(animationConfig.Reload),
					ReloadEnd = bundle.LoadAsset<AnimationClip>(animationConfig.ReloadEnd),
					StartToReloadTransition = animationConfig.StartToReloadTransition,
					ReloadToEndTransition = animationConfig.ReloadToEndTransition,
					ReloadAnimationEvents = animationConfig.ReloadAnimationEvents,
					CompatibleMagPairs = animationConfig.CompatibleMagPairs,
				};
				result.Add(filename, animationData);
			}
		}

		return result;
	}

	public Dictionary<string, WeaponConfig> LoadWeaponConfigs(string assemblyDir)
	{
		var weaponsPath = Path.Combine(assemblyDir, "data", "weapons");
		var weaponsConfigPaths = SafeIO.GetFiles(weaponsPath, "*.jsonc");
		var result = new Dictionary<string, WeaponConfig>(weaponsConfigPaths.Length);
		foreach (var weaponConfigPath in weaponsConfigPaths)
		{
			if (SafeIO.ReadAllText(weaponConfigPath).Ok(out var weaponConfigJson, out var _))
			{
				var weaponConfig = JsonConvert.DeserializeObject<WeaponConfig>(weaponConfigJson);
				var templateId = Path.GetFileNameWithoutExtension(weaponConfigPath);
				result.Add(templateId, weaponConfig);
			}
		}
		return result;
	}

	public void Update()
	{
		foreach (var reload in Reloads.Values)
		{
			reload.Update();
		}
	}

#if DEBUG
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
        var filePath = "animation-events.jsonc";
        SafeIO.WriteAllTextAsync(filePath, json);
	}
#endif

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
		// if (!Plugin.Instance.CustomAnimationsData.TryGetValue(____weaponData.StringTemplateId, out var customAnimationsData))
		// {
		// 	return;
		// }
		// // replace tactical animations events data with ones designed to work with our system
		// foreach (var ieventBehaviour in __instance.AnimationEventsEmitter._animationEventsStateBehaviours)
		// {
		// 	if (ieventBehaviour is not AnimationEventsStateBehaviour eventBehaviour)
		// 	{
		// 		continue;
		// 	}
		// 	if (!customAnimationsData.TryGetValue(eventBehaviour.FullNameHash, out var customData))
		// 	{
		// 		continue;
		// 	}
		// 	eventBehaviour.EventsData._stateHashToEventsCollection[eventBehaviour.EventsListId]._animationEvents = customData.AnimationEvents;
		// }


		// if (__instance.Animator is UnityAnimatorWrapper animatorWrapper)
		// {
	 //        var animatorOverrideController = new AnimatorOverrideController(animatorWrapper.runtimeAnimatorController);
	 //        animatorWrapper.runtimeAnimatorController = animatorOverrideController;
		// 	__instance._originalAnimatorController = animatorOverrideController;
		// 	foreach (var (originalClipName, overrideClip) in Plugin.Instance.CustomAnimations)
		// 	{
		// 		animatorOverrideController[originalClipName] = overrideClip;
		// 	}
		// }
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
		// TODO we dont support quick reloads for now
		if (reloadExternalMagResult.QuickReload)
		{
			return;
		}
		if (!Plugin.Instance.WeaponConfigs.TryGetValue(__instance.Weapon.StringTemplateId, out var weaponConfig))
		{
			return;
		}
		if (!Plugin.Instance.CustomReloadAnimations.TryGetValue(weaponConfig.AnimationName, out var animationData))
		{
			return;
		}
		if (!IsMagPairCompatible(
			reloadExternalMagResult.OldMagazine.magAnimationIndex,
			reloadExternalMagResult.NextMagazine.magAnimationIndex,
			animationData.CompatibleMagPairs))
		{
			return;
		}

		Plugin.Instance.Reloads.Add(__instance.FirearmsAnimator, new()
		{
			Operation = __instance,
			AnimationData = animationData,
		});
	}

   	public static bool IsMagPairCompatible(int oldMag, int newMag, CompatibleMagPair[] compatiblePairs)
	{
		foreach (var pair in compatiblePairs)
		{
			if (oldMag == pair.Old && newMag == pair.New)
			{
				return true;
			}
		}
		return false;
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

			if (__instance.Animator is UnityAnimatorWrapper animatorWrapper)
			{
				// var layerIndex = animatorWrapper.GetLayerIndex("Hands"); // TODO cache
				// animatorWrapper.Play("RELOAD PROGRESSIVE", layerIndex, 0);

				// TODO reuse playable graph
				reload.Init(animatorWrapper.Animator);
				reload.StartReload();
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
// T is a dummy class, to keep all print debug classes separate,
// otherwise they will share MethodName (because its static)
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
#endif
