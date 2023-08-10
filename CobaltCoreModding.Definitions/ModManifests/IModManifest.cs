﻿using CobaltCoreModding.Definitions.ModContactPoints;

namespace CobaltCoreModding.Definitions.ModManifests
{
    public interface IModManifest
    {
        /// <summary>
        /// A unique string for the mod to identify it in the depency chain
        /// </summary>
        public string Name { get; }

        /// <summary>
        /// Mod Identifiers for manifest which should be booted first
        /// </summary>
        public IEnumerable<string> Dependencies { get; }

        /// <summary>
        /// Used by the mod loader to allow a mod to activate its logic.
        /// will be run after cobalt core assembly has been loaded, but before started.
        /// will also try to respect dependencies.
        /// </summary>
        public void BootMod(IModLoaderContact contact);
    }
}