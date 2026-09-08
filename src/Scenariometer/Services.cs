using Dalamud.IoC;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;

namespace Scenariometer;

/// <summary>
/// Dalamud service locator. Populated once by <c>pluginInterface.Create&lt;Services&gt;()</c>
/// in the <see cref="Plugin"/> constructor; every other type reads it statically instead
/// of threading eight interfaces through constructors.
/// </summary>
internal sealed class Services
{
    [PluginService] internal static IDalamudPluginInterface PluginInterface { get; private set; } = null!;
    [PluginService] internal static ICommandManager Commands { get; private set; } = null!;
    [PluginService] internal static IClientState ClientState { get; private set; } = null!;
    // API 15 moved LocalPlayer off IClientState onto IObjectTable, and replaced
    // IClientState.LocalContentId with IPlayerState.ContentId. Both are needed
    // here so QuestState.cs stays the only file touching FFXIVClientStructs.
    [PluginService] internal static IObjectTable Objects { get; private set; } = null!;
    [PluginService] internal static IPlayerState PlayerState { get; private set; } = null!;
    [PluginService] internal static IDataManager Data { get; private set; } = null!;
    [PluginService] internal static IFramework Framework { get; private set; } = null!;
    [PluginService] internal static IChatGui Chat { get; private set; } = null!;
    [PluginService] internal static IPluginLog Log { get; private set; } = null!;
}
