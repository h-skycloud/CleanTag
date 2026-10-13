using Microsoft.Data.Sqlite;

namespace CleanTag.Core;

public static class LegacyDataMigration
{
    public static Task MigrateAsync(string legacy, string current) => Task.Run(() =>
    {
        if (!Directory.Exists(legacy)) return;
        Directory.CreateDirectory(current);
        var destination = Path.Combine(current, "catalog.sqlite");
        if (File.Exists(destination)) return;

        var source = Path.Combine(legacy, "catalog.sqlite");
        if (File.Exists(source))
        {
            var temporary = Path.Combine(current, $"migration-{Guid.NewGuid():N}.sqlite");
            try
            {
                // SQLite's backup API includes committed WAL data while the old app is open.
                using (var input = new SqliteConnection(new SqliteConnectionStringBuilder
                { DataSource = source, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString()))
                using (var output = new SqliteConnection(new SqliteConnectionStringBuilder
                { DataSource = temporary, Pooling = false }.ToString()))
                {
                    input.Open(); output.Open(); input.BackupDatabase(output);
                    using var check = output.CreateCommand(); check.CommandText = "PRAGMA quick_check;";
                    if (check.ExecuteScalar() as string != "ok") throw new IOException("Eski katalog doğrulanamadı. LookClean verileri korundu.");
                }
                CopyPreference(legacy, current, "last-folder.txt");
                // Publish only a complete snapshot. Never overwrite an existing CleanTag catalog.
                try { File.Move(temporary, destination); }
                catch (IOException) when (File.Exists(destination)) { }
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
        else
        {
            CopyPreference(legacy, current, "decisions.json");
            CopyPreference(legacy, current, "decisions.jsonl");
            CopyPreference(legacy, current, "last-folder.txt");
        }
    });

    private static void CopyPreference(string legacy, string current, string name)
    {
        var source = Path.Combine(legacy, name); var destination = Path.Combine(current, name);
        if (!File.Exists(source) || File.Exists(destination)) return;
        var temporary = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.Copy(source, temporary);
            try { File.Move(temporary, destination); }
            catch (IOException) when (File.Exists(destination)) { }
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
