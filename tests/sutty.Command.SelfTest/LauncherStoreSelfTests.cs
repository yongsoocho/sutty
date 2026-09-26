using sutty.Command;
using sutty.Core.Terminal;

internal static class LauncherStoreSelfTests
{
    public static void Run(Action<bool, string> assert, string scratch)
    {
        var primaryDatabase = Db.PathOverride;
        var executableDirectory = Path.Combine(scratch, "launcher-executables");
        Directory.CreateDirectory(executableDirectory);
        foreach (var executable in new[] { "ssh.exe", "multipass.exe", "tool.exe" })
            File.WriteAllBytes(Path.Combine(executableDirectory, executable), []);
        var resolver = new FixtureResolver(executableDirectory);
        var ssh = LocalTerminalLaunchPlanner.Create("ssh -i \"C:\\Users\\fixture\\.ssh\\id key\" worker1", resolver);
        var multipass = LocalTerminalLaunchPlanner.Create("multipass connect master", resolver);
        Db.PathOverride = Path.Combine(scratch, "unified-favorite-tests.db");
        var changes = 0;
        EventHandler changed = (_, _) => changes++;
        HostProfileStore.Changed += changed;
        try
        {
            SeedLegacyFavorites();
            HostProfileStore.EnsureInitialized();
            var migrated = HostProfileStore.GetAll();
            assert(migrated.Count == 2 && migrated.All(profile => profile.IsExternalCommand && profile.IsFavorite),
                "legacy command favorites migrate once to the shared host favorite list");
            var missing = migrated.Single(profile => profile.LaunchCommand == "multipass connect offline");
            assert(missing.DisplayName == "Offline VM" && missing.LastConnectedAtUtc?.Year == 2025,
                "migration preserves labels and timestamps without resolving an unavailable executable");
            using (var connection = Db.Open())
            using (var inspect = connection.CreateCommand())
            {
                inspect.CommandText = "SELECT COUNT(*) FROM command_launcher_favorites";
                assert(Convert.ToInt32(inspect.ExecuteScalar()) == 3,
                    "legacy archive preserves malformed entries and original usage data");
            }
            var missingRejected = false;
            try { HostProfileStore.CreateCommandLaunchPlan(missing, new MissingResolver()); }
            catch (IOException) { missingRejected = true; }
            assert(missingRejected, "missing programs fail only on explicit launch, never on favorite migration");

            var portableCollisionId = "command_" + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
                System.Text.Encoding.UTF8.GetBytes($"{ssh.Kind}\u001f{ssh.CanonicalCommand}"))).ToLowerInvariant()[..32];
            var integrated = HostProfileStore.Save(new HostProfileDraft
                { Host = "integrated.example", DisplayName = "Existing SSH" }, portableCollisionId);
            var favorite = HostProfileStore.SaveCommandFavorite(ssh, "Worker SSH");
            assert(favorite.Id != integrated.Id && !HostProfileStore.GetById(integrated.Id)!.IsExternalCommand &&
                   HostProfileStore.GetById(integrated.Id)!.Host == "integrated.example",
                "external favorite ID collision never replaces an existing portable SSH profile");
            assert(favorite.IsExternalCommand && favorite.LaunchKind == "OpenSsh" &&
                   favorite.LaunchCommand == ssh.CanonicalCommand && favorite.Host == "" && favorite.CredentialId is null,
                "external favorites have explicit launch identity and no Sutty SSH endpoint or vault binding");
            var duplicate = HostProfileStore.SaveCommandFavorite(ssh);
            assert(duplicate.Id == favorite.Id && duplicate.DisplayName == "Worker SSH",
                "repeated save deduplicates canonical command and preserves the chosen display name");
            HostProfileStore.SetFavorite(favorite.Id, false);
            assert(HostProfileStore.SaveCommandFavorite(ssh).IsFavorite,
                "saving an existing command adds it back to the same host favorites list");
            var plan = HostProfileStore.CreateCommandLaunchPlan(HostProfileStore.GetById(favorite.Id)!, resolver);
            assert(plan.Arguments.SequenceEqual(ssh.Arguments) && plan.ExecutablePath == ssh.ExecutablePath,
                "explicit relaunch reconstructs the same structured arguments through the safe planner");
            var savedVm = HostProfileStore.SaveCommandFavorite(multipass, "Master VM");
            assert(HostProfileStore.CreateCommandLaunchPlan(savedVm, resolver).Kind == LocalTerminalLaunchKind.MultipassConnect,
                "Multipass favorite relaunch retains its external launch kind");

            var exportRejected = false;
            try { DefinitionSharingService.Export([favorite], []); }
            catch (ArgumentException) { exportRejected = true; }
            assert(exportRejected, "external command favorites cannot be serialized as misleading SSH host definitions");
            var wrongKindRejected = false;
            try { HostProfileStore.Save(new HostProfileDraft { Host = "worker1", LaunchCommand = ssh.CanonicalCommand }); }
            catch (ArgumentException) { wrongKindRejected = true; }
            assert(wrongKindRejected, "a Sutty SSH profile cannot accidentally store external launch text");

            var unsafeRejected = false;
            try { HostProfileStore.SaveCommandFavorite(LocalTerminalLaunchPlanner.Create("tool --token=synthetic-value", resolver)); }
            catch (ArgumentException) { unsafeRejected = true; }
            assert(unsafeRejected, "unified favorite storage preserves credential-shaped argument filtering");
            var unsafeNameRejected = false;
            try { HostProfileStore.SaveCommandFavorite(ssh, "label secret=value"); }
            catch (ArgumentException) { unsafeNameRejected = true; }
            assert(unsafeNameRejected, "host command labels retain launcher secret filtering");
            var unsafeDraftRejected = false;
            try { HostProfileStore.Save(new HostProfileDraft { LaunchKind = "OpenSsh", LaunchCommand = "ssh worker1; tool" }); }
            catch (ArgumentException) { unsafeDraftRejected = true; }
            assert(unsafeDraftRejected, "saving external profiles directly cannot bypass no-shell validation");

            CommandLauncherStore.EnsureInitialized();
            var launch = CommandLauncherStore.RecordAdHocLaunch(plan, favorite.DisplayName);
            assert(launch.FavoriteId is null && CommandLauncherStore.CompleteLaunch(launch.Id, CommandLauncherLaunchOutcome.Started) &&
                   CommandLauncherStore.CompleteLaunch(launch.Id, CommandLauncherLaunchOutcome.Exited),
                "shared favorite launch keeps a separate bounded lifecycle history snapshot");
            HostProfileStore.Delete(favorite.Id);
            assert(CommandLauncherStore.GetRecentHistory().Any(item => item.Id == launch.Id),
                "deleting a shared favorite preserves launch history");
            for (var index = 0; index < CommandLauncherStore.MaximumHistoryEntries + 12; index++)
                CommandLauncherStore.RecordAdHocLaunch(ssh);
            assert(CommandLauncherStore.GetRecentHistory(CommandLauncherStore.MaximumHistoryEntries).Count == CommandLauncherStore.MaximumHistoryEntries,
                "launch history remains bounded");

            // Model an earlier install with a full 250-row history. All entries
            // share a timestamp so the ID tie-breaker is exercised as well.
            using (var connection = Db.Open())
            using (var seed = connection.CreateCommand())
            {
                seed.CommandText = """
                    DELETE FROM command_launcher_history;
                    WITH RECURSIVE entries(n) AS (SELECT 1 UNION ALL SELECT n + 1 FROM entries WHERE n < 250)
                    INSERT INTO command_launcher_history
                        (id, display_name, command_text, launch_kind, launched_at_utc, outcome)
                    SELECT n, 'Legacy launch ' || n, 'ssh legacy-host', 'OpenSsh',
                           '2025-01-01T00:00:00Z', 'Exited' FROM entries;
                    """;
                seed.ExecuteNonQuery();
            }

            HostProfileStore.Delete(missing.Id);
            var legacySsh = HostProfileStore.GetAll().Single(item => item.LaunchCommand == "ssh legacy-host");
            HostProfileStore.SetFavorite(legacySsh.Id, false);
            var testDatabase = Db.PathOverride;
            Db.PathOverride = Path.Combine(scratch, "empty-unified-favorites.db");
            HostProfileStore.EnsureInitialized();
            CommandLauncherStore.EnsureInitialized();
            using (var connection = Db.Open())
            using (var inspect = connection.CreateCommand())
            {
                inspect.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE name = 'command_launcher_favorites'";
                assert(Convert.ToInt32(inspect.ExecuteScalar()) == 0, "new installs never create a separate command favorite table");
            }
            Db.PathOverride = testDatabase;
            HostProfileStore.EnsureInitialized();
            var retainedHistory = CommandLauncherStore.GetRecentHistory(int.MaxValue);
            assert(retainedHistory.Count == 15 && retainedHistory.Select(item => item.Id)
                    .SequenceEqual(Enumerable.Range(236, 15).Reverse().Select(id => (long)id)),
                "opening earlier history retains exactly the newest 15 entries without a new launch");
            using (var connection = Db.Open())
            using (var inspect = connection.CreateCommand())
            {
                inspect.CommandText = "SELECT COUNT(*) FROM command_launcher_history";
                assert(Convert.ToInt32(inspect.ExecuteScalar()) == 15,
                    "history retention removes old rows from storage, not only from the Home view");
            }
            assert(HostProfileStore.GetById(savedVm.Id)?.IsFavorite == true,
                "history retention leaves shared host favorites intact");
            assert(HostProfileStore.GetById(missing.Id) is null && !HostProfileStore.GetById(legacySsh.Id)!.IsFavorite,
                "migration marker prevents deleted or unpinned favorites from reappearing after reopen");
            assert(changes > 0, "shared host favorite observers receive save, pin and delete events");
            Console.WriteLine("Unified host command favorites and launcher history self-tests passed.");
        }
        finally
        {
            HostProfileStore.Changed -= changed;
            Db.PathOverride = primaryDatabase;
        }
    }

    private static void SeedLegacyFavorites()
    {
        using var connection = Db.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE command_launcher_favorites (
                id INTEGER PRIMARY KEY, display_name TEXT NOT NULL, command_text TEXT NOT NULL,
                command_identity TEXT NOT NULL, launch_kind TEXT NOT NULL, created_at_utc TEXT NOT NULL,
                updated_at_utc TEXT NOT NULL, last_launched_at_utc TEXT, launch_count INTEGER NOT NULL);
            INSERT INTO command_launcher_favorites VALUES
                (1, 'Legacy SSH', 'ssh legacy-host', 'legacy-ssh', 'OpenSsh', '2024-01-01T00:00:00Z', '2025-01-01T00:00:00Z', NULL, 3),
                (2, 'Offline VM', 'multipass connect offline', 'legacy-vm', 'MultipassConnect', '2024-01-01T00:00:00Z', '2025-01-01T00:00:00Z', '2025-02-01T00:00:00Z', 7),
                (3, 'Malformed original', 'ssh bad; tool', 'bad-row', 'OpenSsh', '2024-01-01T00:00:00Z', '2025-01-01T00:00:00Z', NULL, 1);
            """;
        command.ExecuteNonQuery();
    }

    private sealed class FixtureResolver(string directory) : ILocalTerminalExecutableResolver
    {
        public string? ResolveExecutable(string name) => Path.Combine(directory, name);
    }
    private sealed class MissingResolver : ILocalTerminalExecutableResolver
    {
        public string? ResolveExecutable(string name) => null;
    }
}
