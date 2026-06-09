using System;
using Dalamud.Game.Command;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using Dalamud.Interface.Windowing;
using PartyFinderRefresh.Game;
using PartyFinderRefresh.UI;

namespace PartyFinderRefresh;

public sealed class Plugin : IDalamudPlugin
{
    private const string CommandName = "/pfr";

    private readonly IDalamudPluginInterface pluginInterface;
    private readonly ICommandManager commandManager;
    private readonly Configuration config;

    private readonly WindowSystem windowSystem = new("PartyFinderRefresh");
    private readonly MainWindow mainWindow;
    private readonly ConfigWindow configWindow;
    private readonly PartyFinderManager partyFinder;
    private readonly RefreshScheduler scheduler;

    // Dalamud injects these services into the constructor by type.
    public Plugin(
        IDalamudPluginInterface pluginInterface,
        ICommandManager commandManager,
        IFramework framework,
        ISigScanner sigScanner,
        IGameGui gameGui,
        IClientState clientState,
        IPlayerState playerState,
        IPartyList partyList,
        IDataManager dataManager,
        IChatGui chat,
        IPluginLog log)
    {
        this.pluginInterface = pluginInterface;
        this.commandManager = commandManager;

        config = pluginInterface.GetPluginConfig() as Configuration ?? new Configuration();
        config.Initialize(pluginInterface);

        partyFinder = new PartyFinderManager(sigScanner, framework, log, gameGui, playerState, dataManager);
        scheduler = new RefreshScheduler(framework, clientState, partyList, log, config, partyFinder);

        configWindow = new ConfigWindow(config);
        mainWindow = new MainWindow(config, partyFinder, scheduler, chat, () => configWindow.IsOpen = true);
        windowSystem.AddWindow(mainWindow);
        windowSystem.AddWindow(configWindow);

        commandManager.AddHandler(CommandName, new CommandInfo(OnCommand)
        {
            HelpMessage = "Open Party Finder Refresh. Use \"/pfr config\" for settings.",
        });

        pluginInterface.UiBuilder.Draw += windowSystem.Draw;
        pluginInterface.UiBuilder.OpenMainUi += ToggleMain;
        pluginInterface.UiBuilder.OpenConfigUi += ToggleConfig;

        log.Information("[PFR] Party Finder Refresh loaded (native ops {State}).",
            partyFinder.Available ? "available" : "unavailable");
    }

    private void OnCommand(string command, string args)
    {
        if (args.Trim().Equals("config", StringComparison.OrdinalIgnoreCase))
            ToggleConfig();
        else
            ToggleMain();
    }

    private void ToggleMain() => mainWindow.Toggle();

    private void ToggleConfig() => configWindow.Toggle();

    public void Dispose()
    {
        pluginInterface.UiBuilder.Draw -= windowSystem.Draw;
        pluginInterface.UiBuilder.OpenMainUi -= ToggleMain;
        pluginInterface.UiBuilder.OpenConfigUi -= ToggleConfig;
        windowSystem.RemoveAllWindows();
        commandManager.RemoveHandler(CommandName);

        scheduler.Dispose();
        partyFinder.Dispose();
        mainWindow.Dispose();
        configWindow.Dispose();
    }
}
