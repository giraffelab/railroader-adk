using System;
using System.Collections.Generic;
using UnityEngine;

namespace AssetPack.Common.Components
{
    public class AnimationMap : MonoBehaviour
    {
        [Serializable]
        public struct MapEntry
        {
            public string name;
            public AnimationClip clip;
        }
        
        [SerializeField]
        public List<MapEntry> animationClips = new ();

        public bool TryGetClip(string clipName, out AnimationClip clip)
        {
            foreach (var entry in animationClips)
            {
                if (entry.name == clipName)
                {
                    clip = entry.clip;
                    return true;
                }
            }

            clip = null;
            return false;
        }

        public AnimationClip ClipForName(string clipName)
        {
            if (TryGetClip(clipName, out var clip))
                return clip;

            throw new Exception($"Couldn't find animation named {clipName} in {name}");
        }
    }
}