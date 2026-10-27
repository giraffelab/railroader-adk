using System;
using System.Collections.Generic;
using UnityEngine;

namespace AssetPack.Common.Components
{
    public class MaterialMap : MonoBehaviour
    {
        [Serializable]
        public struct MapEntry
        {
            public string name;
            public Material material;
        }
        
        [SerializeField]
        public List<MapEntry> entries = new ();

        /// <summary>
        /// The material mapped to <paramref name="materialName"/> (null if unassigned); throws when no entry has that name.
        /// </summary>
        public Material MaterialForName(string materialName)
        {
            foreach (var entry in entries)
            {
                if (entry.name == materialName)
                    return entry.material;
            }

            throw new Exception($"Couldn't find material named {materialName} in {name}");
        }

        /// <summary>
        /// Find the material mapped to <paramref name="materialName"/>. False when no entry has that name or
        /// the entry's material is unassigned.
        /// </summary>
        public bool TryGetMaterial(string materialName, out Material material)
        {
            foreach (var entry in entries)
            {
                if (entry.name != materialName)
                    continue;

                material = entry.material;
                return material != null;
            }

            material = null;
            return false;
        }

        /// <summary>
        /// Rewrite each declared material through <paramref name="resolver"/> (e.g. to swap bundle
        /// materials for per-car owned copies). Null entries are left untouched.
        /// </summary>
        public void ReplaceMaterials(Func<Material, Material> resolver)
        {
            for (var i = 0; i < entries.Count; i++)
            {
                var entry = entries[i];
                if (entry.material == null)
                    continue;

                entry.material = resolver(entry.material);
                entries[i] = entry;
            }
        }
    }
}