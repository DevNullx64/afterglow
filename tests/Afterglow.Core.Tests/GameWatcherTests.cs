using Afterglow.Core.Services;
using Afterglow.Core.Settings;

namespace Afterglow.Core.Tests;

public sealed class GameWatcherTests
{
    private static GameRule Rule(string exe) => new() { ExecutableName = exe, ProfileName = "p", RevertOnExit = true };

    [Fact]
    public void No_rules_means_no_polling_timer()
    {
        using var watcher = new GameWatcher { RunningProcessNames = () => [] };
        watcher.Start();
        Assert.False(watcher.IsPolling);

        watcher.UpdateRules([Rule("game.exe")]);
        Assert.True(watcher.IsPolling);

        watcher.UpdateRules([]);
        Assert.False(watcher.IsPolling);
    }

    [Fact]
    public void Deleting_the_rule_mid_game_still_restores_on_the_next_scan()
    {
        var running = new List<string> { "explorer", "game" };
        using var watcher = new GameWatcher { RunningProcessNames = () => running };
        GameRule? started = null, exited = null;
        watcher.GameStarted += r => started = r;
        watcher.GameExited += r => exited = r;
        watcher.Start();
        watcher.UpdateRules([Rule("game.exe")]);

        watcher.Scan();
        Assert.Equal("game.exe", started?.ExecutableName);

        // The user deletes the rule while the game is still running: the poll
        // must stay armed and the next scan must hand back the pre-game state.
        watcher.UpdateRules([]);
        Assert.True(watcher.IsPolling);
        watcher.Scan();

        Assert.Equal("game.exe", exited?.ExecutableName);
        Assert.False(watcher.IsPolling);
    }

    [Fact]
    public void A_game_exit_is_reported_once_its_process_is_gone()
    {
        var running = new List<string> { "game" };
        using var watcher = new GameWatcher { RunningProcessNames = () => running };
        int exits = 0;
        watcher.GameExited += _ => exits++;
        watcher.Start();
        watcher.UpdateRules([Rule("game.exe")]);

        watcher.Scan();
        running.Clear();
        watcher.Scan();
        watcher.Scan();

        Assert.Equal(1, exits);
        Assert.True(watcher.IsPolling);   // rules remain, so it keeps watching for the next launch
    }
}
