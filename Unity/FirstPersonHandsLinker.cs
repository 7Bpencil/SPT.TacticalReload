using UnityEngine;
using Diz.Skinning;

namespace SevenBoldPencil.TacticalReload
{
	public class FirstPersonHandsLinker : MonoBehaviour
	{
		public const string PathPrefix = "Base HumanPelvis/Base HumanSpine1/Base HumanSpine2/Base HumanSpine3/Base HumanRibcage/";

		public Skin Hands;
		public Transform WeaponRoot;

		public void Start()
		{
			var bonePaths = Hands._bonePaths;
			var boneTransforms = new Transform[bonePaths.Length];
			for (var i = 0; i < bonePaths.Length; i++)
			{
				boneTransforms[i] = WeaponRoot.Find(bonePaths[i].Replace(PathPrefix, ""));
			}

			Hands._skinnedMeshRenderer.rootBone = WeaponRoot.Find(Hands._rootBonePath.Replace(PathPrefix, ""));
			Hands._skinnedMeshRenderer.bones = boneTransforms;
		}
	}
}
