using CleanTag.Core;
using Microsoft.Data.Sqlite;

internal static class MigrationChecks
{
    public static async Task RunAsync(string root, Func<string, Func<Task>, Task> test)
    {
        await test("CleanTag migration includes committed live WAL data and preserves the legacy catalog", async () =>
        {
            var legacy = Path.Combine(root, "legacy-wal"); var current = Path.Combine(root, "new-wal");
            Directory.CreateDirectory(legacy);
            using var writer = new SqliteConnection($"Data Source={Path.Combine(legacy, "catalog.sqlite")};Pooling=False");
            writer.Open(); using var command = writer.CreateCommand();
            command.CommandText = "PRAGMA journal_mode=WAL; PRAGMA wal_autocheckpoint=0; CREATE TABLE Probe(Value TEXT); INSERT INTO Probe VALUES('etiket');";
            command.ExecuteNonQuery();
            File.WriteAllText(Path.Combine(legacy, "last-folder.txt"), "sample-folder");
            await LegacyDataMigration.MigrateAsync(legacy, current);
            using var snapshot = new SqliteConnection($"Data Source={Path.Combine(current, "catalog.sqlite")};Pooling=False");
            snapshot.Open(); using var check = snapshot.CreateCommand(); check.CommandText = "SELECT Value FROM Probe;";
            if (check.ExecuteScalar() as string != "etiket") throw new Exception("Committed WAL row missing");
            command.CommandText = "SELECT COUNT(*) FROM Probe;";
            if (Convert.ToInt32(command.ExecuteScalar()) != 1 || File.ReadAllText(Path.Combine(current, "last-folder.txt")) != "sample-folder") throw new Exception("Legacy data or preference changed");
        });
        await test("CleanTag migration never overwrites an existing catalog or preference", async () =>
        {
            var legacy = Path.Combine(root, "legacy-existing"); var current = Path.Combine(root, "new-existing");
            Directory.CreateDirectory(legacy); Directory.CreateDirectory(current);
            File.WriteAllText(Path.Combine(current, "catalog.sqlite"), "existing");
            File.WriteAllText(Path.Combine(current, "last-folder.txt"), "current");
            File.WriteAllText(Path.Combine(legacy, "last-folder.txt"), "legacy");
            await LegacyDataMigration.MigrateAsync(legacy, current);
            if (File.ReadAllText(Path.Combine(current, "catalog.sqlite")) != "existing" || File.ReadAllText(Path.Combine(current, "last-folder.txt")) != "current") throw new Exception("Existing data overwritten");
        });
        await test("CleanTag imports JSON-only legacy data and allows retry after invalid SQLite", async () =>
        {
            var legacy = Path.Combine(root, "legacy-json"); var current = Path.Combine(root, "new-json"); Directory.CreateDirectory(legacy);
            File.WriteAllText(Path.Combine(legacy, "decisions.json"), "{}");
            await LegacyDataMigration.MigrateAsync(legacy, current);
            if (File.ReadAllText(Path.Combine(current, "decisions.json")) != "{}") throw new Exception("JSON not migrated");
            File.WriteAllText(Path.Combine(legacy, "catalog.sqlite"), "broken");
            bool failed = false; try { await LegacyDataMigration.MigrateAsync(legacy, current); } catch (SqliteException) { failed = true; }
            if (!failed || File.Exists(Path.Combine(current, "catalog.sqlite")) || Directory.EnumerateFiles(current, "migration-*").Any()) throw new Exception("Failed snapshot was published or left behind");
        });
    }
}
