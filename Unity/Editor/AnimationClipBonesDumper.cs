#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEngine;
using Newtonsoft.Json;

namespace SevenBoldPencil.TacticalReload
{
	public static class AnimationClipBonesDumper
	{
		[MenuItem("Assets/Dump AnimationClip Bones", true)]
		private static bool Validate()
		{
			return Selection.GetFiltered<AnimationClip>(SelectionMode.Assets).Length > 0;
		}

		[MenuItem("Assets/Dump AnimationClip Bones")]
		private static void Dump()
		{
			var paths = new SortedSet<string>(StringComparer.Ordinal);
			foreach (var clip in Selection.GetFiltered<AnimationClip>(SelectionMode.Assets))
			{
				Debug.Log($"Dumping bones from {clip.name}");
				foreach (var binding in AnimationUtility.GetCurveBindings(clip))
				{
					if (binding.type == typeof(Transform) && !string.IsNullOrEmpty(binding.path))
					{
						paths.Add(binding.path);
					}
				}
			}

			var outFile = "out.jsonc";
			File.WriteAllText(outFile, JsonConvert.SerializeObject(paths, Formatting.Indented));
			Debug.Log($"Dumped {paths.Count} bones to {outFile}");
		}
	}
}
#endif
