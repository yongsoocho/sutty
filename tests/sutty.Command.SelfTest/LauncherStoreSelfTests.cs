using sutty.Command;
using sutty.Core.Terminal;

internal static class LauncherStoreSelfTests
{
    public static void Run(Action<bool, string> assert, string scratch)
    {
        var executableDirectory = Path.Combine(scratch, "launcher-executables");
        Directory.CreateDirectory(executableDirectory);
        foreach (var executable in new[] { "ssh.exe", "multipass.exe", "tool.exe" })
            File.WriteAllBytes(Path.Combine(executableDirectory, executable), Array.Empty<byte>());

        var resolver = new FixtureResolver(executableDirectory);
        var ssh = LocalTerminalLaunchPlanner.Create(
            "ssh -i \"C:\\Users\\fixture\\.ssh\\id key\" worker1",
            resolver);
        var reparsed = LocalTerminalLaunchPlanner.Create(ssh.CanonicalCommand, resolver);
        assert(ssh.Kind == LocalTerminalLaunchKind.OpenSsh &&
               ssh.Arguments.SequenceEqual(reparsed.Arguments) &&
               ssh.CanonicalCommand.Contains("\"C:\\Users\\fixture\\.ssh\\id key\"", StringComparison.Ordinal),
            "canonical SSH command keeps a quoted .ssh key-file argument reparseable");

        var multipass = LocalTerminalLaunchPlanner.Create("multipass connect master", resolver);
        assert(multipass.Kind == LocalTerminalLaunchKind.MultipassConnect &&
               multipass.CanonicalCommand == "multipass connect master",
            "canonical Multipass connection command");

        var events = 0;
        EventHandler changed = (_, _) => events++;
        CommandLauncherStore.Changed += changed;
        try
        {
            CommandLauncherStore.EnsureInitialized();
            CommandLauncherStore.EnsureInitialized();
            assert(CommandStore.GetAll().Count == 0,
                "launcher schema initialization is idempotent and leaves existing command data untouched");

            var favorite = CommandLauncherStore.SaveFavorite("Worker one", ssh);
            assert(favorite.CommandText == ssh.CanonicalCommand &&
                   favorite.Kind == LocalTerminalLaunchKind.OpenSsh &&
                   favorite.LaunchCount == 0,
                "planner canonical favorite is stored without a runtime executable path");

            using (var connection = Db.Open())
            using (var inspect = connection.CreateCommand())
            {
                inspect.CommandText = "SELECT command_text FROM command_launcher_favorites WHERE id = $id";
                inspect.Parameters.AddWithValue("$id", favorite.Id);
                var persisted = Convert.ToString(inspect.ExecuteScalar()) ?? "";
                assert(!persisted.Contains(executableDirectory, StringComparison.OrdinalIgnoreCase) &&
                       persisted == ssh.CanonicalCommand,
                    "favorite persists the canonical command but never the resolved executable path");
            }

            var duplicateRejected = false;
            try { CommandLauncherStore.SaveFavorite("Duplicate", ssh); }
            catch (InvalidOperationException) { duplicateRejected = true; }
            assert(duplicateRejected, "equivalent launcher favorite is deduplicated");

            var beforeMismatch = CommandLauncherStore.GetRecentHistory(CommandLauncherStore.MaximumHistoryEntries).Count;
            var mismatchRejected = false;
            try { CommandLauncherStore.RecordFavoriteLaunch(favorite.Id, multipass); }
            catch (InvalidOperationException) { mismatchRejected = true; }
            assert(mismatchRejected &&
                   CommandLauncherStore.GetRecentHistory(CommandLauncherStore.MaximumHistoryEntries).Count == beforeMismatch,
                "stale favorite plan cannot record a different command");

            var launch = CommandLauncherStore.RecordFavoriteLaunch(favorite.Id, ssh);
            assert(launch.FavoriteId == favorite.Id &&
                   launch.Outcome == CommandLauncherLaunchOutcome.LaunchRequested &&
                   CommandLauncherStore.CompleteLaunch(launch.Id, CommandLauncherLaunchOutcome.Started) &&
                   CommandLauncherStore.CompleteLaunch(launch.Id, CommandLauncherLaunchOutcome.Exited),
                "favorite launch records only finite lifecycle state");
            assert(CommandLauncherStore.GetFavorite(favorite.Id)!.LaunchCount == 1,
                "favorite launch atomically increments favorite usage");

            var updated = CommandLauncherStore.UpdateFavorite(favorite.Id, "Worker SSH", ssh);
            assert(updated.DisplayName == "Worker SSH" && updated.LaunchCount == 1,
                "favorite edit keeps accumulated usage");

            var adHoc = CommandLauncherStore.RecordAdHocLaunch(multipass);
            assert(adHoc.FavoriteId is null &&
                   CommandLauncherStore.CompleteLaunch(adHoc.Id, CommandLauncherLaunchOutcome.LaunchNotStarted),
                "ad-hoc launch can record a pre-start failure without output or error text");

            assert(CommandLauncherStore.DeleteFavorite(favorite.Id) &&
                   CommandLauncherStore.GetRecentHistory(CommandLauncherStore.MaximumHistoryEntries)
                       .Any(item => item.Id == launch.Id && item.FavoriteId is null),
                "deleting a favorite preserves a detached history snapshot");

            for (var index = 0; index < CommandLauncherStore.MaximumHistoryEntries + 12; index++)
                CommandLauncherStore.RecordAdHocLaunch(ssh);
            assert(CommandLauncherStore.GetRecentHistory(CommandLauncherStore.MaximumHistoryEntries).Count ==
                   CommandLauncherStore.MaximumHistoryEntries,
                "launcher history has a durable bounded retention limit");

            using (var connection = Db.Open())
            using (var columns = connection.CreateCommand())
            {
                columns.CommandText = "PRAGMA table_info(command_launcher_history)";
                var names = new List<string>();
                using var reader = columns.ExecuteReader();
                while (reader.Read())
                    names.Add(reader.GetString(1));
                assert(!names.Any(name => name.Contains("output", StringComparison.OrdinalIgnoreCase) ||
                                          name.Contains("error", StringComparison.OrdinalIgnoreCase) ||
                                          name.Contains("executable", StringComparison.OrdinalIgnoreCase)),
                    "history schema has no output, error, or executable-path field");
            }

            for (var index = 0; index < CommandLauncherStore.MaximumFavorites; index++)
            {
                var numbered = LocalTerminalLaunchPlanner.Create($"tool shortcut-{index}", resolver);
                CommandLauncherStore.SaveFavorite($"Shortcut {index}", numbered);
            }
            var limitRejected = false;
            try
            {
                CommandLauncherStore.SaveFavorite(
                    "Over limit",
                    LocalTerminalLaunchPlanner.Create("tool shortcut-over-limit", resolver));
            }
            catch (InvalidOperationException) { limitRejected = true; }
            assert(limitRejected &&
                   CommandLauncherStore.GetFavorites().Count == CommandLauncherStore.DefaultFavoriteLimit &&
                   CommandLauncherStore.GetFavorites(CommandLauncherStore.MaximumFavorites).Count ==
                   CommandLauncherStore.MaximumFavorites,
                "favorite storage caps entries and exposes compact ordered list limits");
            foreach (var item in CommandLauncherStore.GetFavorites(CommandLauncherStore.MaximumFavorites))
                CommandLauncherStore.DeleteFavorite(item.Id);

            var unsafeFlag = "--" + "to" + "ken";
            var unsafeCommand = "tool " + unsafeFlag + "=synthetic-value";
            var unsafePlan = LocalTerminalLaunchPlanner.Create(unsafeCommand, resolver);
            var unsafePlanRejected = false;
            try { CommandLauncherStore.SaveFavorite("Unsafe plan", unsafePlan); }
            catch (ArgumentException) { unsafePlanRejected = true; }
            assert(unsafePlanRejected,
                "planner-valid direct commands with token-like arguments never enter launcher storage");
            var userInfoPlan = LocalTerminalLaunchPlanner.Create(
                "tool -u synthetic-user:opaque-value",
                resolver);
            var userInfoRejected = false;
            try { CommandLauncherStore.SaveFavorite("Unsafe user option", userInfoPlan); }
            catch (ArgumentException) { userInfoRejected = true; }
            assert(userInfoRejected,
                "credential-shaped user arguments never enter launcher storage");
            InsertUntrustedRow(unsafeCommand);
            assert(CommandLauncherStore.GetFavorites(CommandLauncherStore.MaximumFavorites)
                       .All(item => item.CommandText != unsafeCommand) &&
                   CommandLauncherStore.GetRecentHistory(CommandLauncherStore.MaximumHistoryEntries)
                       .All(item => item.CommandText != unsafeCommand),
                "tampered credential-like command rows fail closed and stay hidden");

            var unsafeNameRejected = false;
            try { CommandLauncherStore.SaveFavorite("label " + "secret" + "=value", ssh); }
            catch (ArgumentException) { unsafeNameRejected = true; }
            assert(unsafeNameRejected, "favorite labels cannot become a credential side channel");

            var primaryDatabase = Db.PathOverride;
            var alternateDatabase = Path.Combine(scratch, "alternate-launcher.db");
            try
            {
                Db.PathOverride = alternateDatabase;
                CommandLauncherStore.EnsureInitialized();
                assert(File.Exists(alternateDatabase),
                    "launcher initialization follows an alternate local database path in the same process");
            }
            finally
            {
                Db.PathOverride = primaryDatabase;
                CommandLauncherStore.EnsureInitialized();
            }
            assert(events > 0, "launcher storage notifications reach compact launcher views");
            Console.WriteLine("Local command launcher storage self-tests passed.");
        }
        finally
        {
            CommandLauncherStore.Changed -= changed;
        }
    }

    private static void InsertUntrustedRow(string unsafeCommand)
    {
        var now = DateTimeOffset.UtcNow.ToString("O");
        using var connection = Db.Open();
        using var transaction = connection.BeginTransaction();
        using (var favorite = connection.CreateCommand())
        {
            favorite.Transaction = transaction;
            favorite.CommandText = """
                INSERT INTO command_launcher_favorites (
                    display_name, command_text, command_identity, launch_kind,
                    created_at_utc, updated_at_utc, last_launched_at_utc, launch_count)
                VALUES ('Untrusted', $command, 'untrusted-row', 'DirectExecutable', $now, $now, NULL, 0)
                """;
            favorite.Parameters.AddWithValue("$command", unsafeCommand);
            favorite.Parameters.AddWithValue("$now", now);
            favorite.ExecuteNonQuery();
        }
        using (var history = connection.CreateCommand())
        {
            history.Transaction = transaction;
            history.CommandText = """
                INSERT INTO command_launcher_history (
                    favorite_id, display_name, command_text, launch_kind, launched_at_utc, outcome)
                VALUES (NULL, 'Untrusted', $command, 'DirectExecutable', $now, 'LaunchRequested')
                """;
            history.Parameters.AddWithValue("$command", unsafeCommand);
            history.Parameters.AddWithValue("$now", now);
            history.ExecuteNonQuery();
        }
        transaction.Commit();
    }

    private sealed class FixtureResolver(string executableDirectory) : ILocalTerminalExecutableResolver
    {
        public string? ResolveExecutable(string executableFileName)
        {
            var candidate = Path.Combine(executableDirectory, executableFileName);
            return File.Exists(candidate) ? candidate : null;
        }
    }
}
