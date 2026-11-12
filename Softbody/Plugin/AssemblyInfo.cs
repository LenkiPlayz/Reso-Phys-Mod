using Elements.Data;

// Registers this assembly's data model types (the Softbody component and its nested enum) with
// FrooxEngine at engine init (EngineInitializer.InitializeFrooxEngine -> GlobalTypeRegistry.RegisterAssembly).
//
// Core is the only type whose components can exist in normal worlds: World.StartSession / World.LocalWorld
// allow GlobalTypeRegistry.CoreAssemblies by default. UserspaceCore is only allowed in the Userspace world,
// and Optional is never added to any world's allowed list by this engine build. The cost of Core: every
// session this client hosts lists "Softbody" in its assembly manifest, so joiners need the plugin
// (SessionJoiner.SendJoinAuthenticate). See README "Join compatibility".
[assembly: DataModelAssembly(DataModelAssemblyType.Core)]
